using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ucu.Andis.Common;
using Ucu.Andis.OrdersService.Data;
using Ucu.Andis.OrdersService.Models;

namespace Ucu.Andis.OrdersService.Messaging;

// Competing Consumers: cada réplica de orders-worker ejecuta este servicio y
// consume de la misma cola. RabbitMQ entrega cada mensaje a un solo consumidor,
// así que las réplicas se reparten el trabajo. Si una réplica se cae antes de
// confirmar -ack- un mensaje, RabbitMQ se lo entrega a otra.
//
// Publisher/Subscriber: la cola orders.process es la suscripción del worker al
// evento order.created del exchange andis.events. Los proyectores de la
// vista materializada tienen su propia cola y reciben el mismo evento.
public class OrderProcessingWorker : BackgroundService
{
    public const string MeterName = "Ucu.Andis.OrdersWorker";

    // Cola de órdenes pendientes de procesar. Todos los workers consumen de esta
    // misma cola: Competing Consumers.
    public const string OrdersQueue = "orders.process";

    private static readonly TimeSpan RequeueDelay = TimeSpan.FromSeconds(5);

    // Órdenes procesadas por cada réplica, con su resultado: en Grafana se ve
    // cómo se reparte el trabajo entre las réplicas.
    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> OrdersProcessed = Meter.CreateCounter<long>("orders.processed", "{order}",
        "Órdenes procesadas por el worker: Confirmed, Rejected o Requeued");

    private readonly RabbitMq _rabbitMq;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrderProcessingWorker> _logger;
    private readonly TimeSpan _processingDelay;
    private readonly string _workerName = Environment.MachineName;

    public OrderProcessingWorker(RabbitMq rabbitMq, IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<OrderProcessingWorker> logger)
    {
        _rabbitMq = rabbitMq;
        _scopeFactory = scopeFactory;
        _logger = logger;
        // Demora artificial para que se vea cómo se reparten las órdenes entre réplicas.
        _processingDelay = TimeSpan.FromMilliseconds(config.GetValue("Worker:ProcessingDelayMs", 2000));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = await ConnectAsync(stoppingToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await RabbitMq.DeclareSubscriberQueueAsync(channel, OrdersQueue, [Events.OrderCreated], stoppingToken);

        // Cada réplica toma un solo mensaje a la vez: así el trabajo se reparte
        // según la capacidad de cada una, en lugar de que una acapare la cola.
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            // Un mensaje que no es un OrderCreated válido no se va a poder procesar
            // nunca: se descarta en lugar de devolverlo a la cola para siempre.
            OrderCreated? created;
            try
            {
                created = JsonSerializer.Deserialize<OrderCreated>(delivery.Body.Span);
            }
            catch (JsonException)
            {
                created = null;
            }
            if (created is null)
            {
                _logger.LogWarning("Worker {Worker}: se descarta un mensaje que no es un OrderCreated", _workerName);
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
                return;
            }

            bool processed;
            try
            {
                processed = await ProcessAsync(created.OrderId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Cualquier falla -la base no responde, un error de SQL- devuelve
                // el mensaje a la cola. Sin este catch, RabbitMQ.Client registra
                // la excepción y deja el mensaje sin ack ni nack: con prefetch 1,
                // esta réplica no recibe más mensajes hasta que se cierre el
                // canal. Trade-off: un mensaje que falla siempre -un poison
                // message- vuelve a la cola para siempre; la alternativa es una
                // dead-letter queue, que la demo no tiene.
                _logger.LogError(ex, "Worker {Worker}: no se pudo procesar la orden {OrderId}; vuelve a la cola", _workerName, created.OrderId);
                processed = false;
            }

            if (processed)
            {
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            }
            else
            {
                await Task.Delay(RequeueDelay, stoppingToken);
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, stoppingToken);
            }
        };

        await channel.BasicConsumeAsync(OrdersQueue, autoAck: false, consumer, stoppingToken);
        _logger.LogInformation("[COMPETING CONSUMERS] Worker {Worker} escuchando la cola {Queue}", _workerName, OrdersQueue);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task<IConnection> ConnectAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            try
            {
                return await _rabbitMq.GetConnectionAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Esperando a RabbitMQ: {Message}", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
    }

    // Devuelve false si la orden no se pudo procesar porque una dependencia no
    // está disponible; en ese caso el mensaje vuelve a la cola.
    //
    // Es idempotente: el outbox entrega cada mensaje al menos una vez, no
    // exactamente una, y una Order que ya no está Pending no se procesa de
    // nuevo. Pero preguntar si está Pending y después cambiarla son dos pasos
    // -check-then-act-, y con varias réplicas eso no alcanza:
    //
    // 1. El relay publica dos veces el mismo order.created -ver OutboxRelay.cs-.
    // 2. Las dos copias llegan a la vez a dos réplicas.
    // 3. Las dos leen la Order como Pending, esperan la demora y las llamadas a
    //    Users y Products, y las dos guardan el cambio.
    //
    // Quedarían dos OrderConfirmed en el historial y dos order.confirmed en el
    // outbox, que llegarían dos veces a los proyectores y al ERP.
    //
    // Por eso la lectura inicial solo evita trabajo inútil con un duplicado que
    // llega después; lo que decide es el primer paso de la transacción: un
    // UPDATE que cambia el estado solo si sigue Pending. Es una sola sentencia,
    // y MySQL bloquea la fila: la segunda réplica espera a que la primera
    // confirme, ve que ya no está Pending, no cambia nada y descarta la Order.
    // El índice único de order_events es una segunda barrera -ver
    // mysqlinit.sql-.
    private async Task<bool> ProcessAsync(int orderId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        // Sin seguimiento de cambios: el estado se escribe con el UPDATE
        // condicional, no guardando esta entidad.
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null || order.Status != OrderStatus.Pending)
            return true;

        _logger.LogInformation("[COMPETING CONSUMERS] Worker {Worker} procesando la orden {OrderId}", _workerName, orderId);
        await Task.Delay(_processingDelay, cancellationToken);

        var user = await Dependencies.GetUserAsync(httpClientFactory.CreateClient("UsersService"), order.UserId, cancellationToken);
        var product = await Dependencies.GetAsync<ProductDto>(httpClientFactory.CreateClient("ProductsService"), $"{order.ProductId}", cancellationToken);

        if (!user.Available || !product.Available)
        {
            _logger.LogWarning("Worker {Worker}: la orden {OrderId} vuelve a la cola [usuarios: {Users}, productos: {Products}]",
                _workerName, orderId, user.Failure ?? "ok", product.Failure ?? "ok");
            Count("Requeued");
            return false;
        }

        var rejection = user.NotFound ? $"El usuario {order.UserId} no existe"
            : product.NotFound ? $"El producto {order.ProductId} no existe"
            : null;
        var newStatus = rejection is null ? OrderStatus.Confirmed : OrderStatus.Rejected;

        var orderEvent = rejection is null
            ? OrderEvent.Create(orderId, OrderEventTypes.OrderConfirmed, new OrderConfirmedData(_workerName))
            : OrderEvent.Create(orderId, OrderEventTypes.OrderRejected, new OrderRejectedData(rejection, _workerName));

        // Transactional Outbox: el evento de Event Sourcing, el cambio de estado y
        // el mensaje para los suscriptores se guardan juntos.
        var outboxMessage = rejection is null
            ? OutboxMessage.Create(Events.OrderConfirmed, new OrderConfirmed(orderId, order.UserId, order.ProductId, order.Quantity))
            : OutboxMessage.Create(Events.OrderRejected, new OrderRejected(orderId, rejection));

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Competing Consumers: la transición de estado condicional y atómica.
        var changed = await db.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, newStatus), cancellationToken);
        if (changed == 0)
        {
            // Otra réplica ya la procesó: no se agrega ni el evento ni el mensaje.
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogInformation("[COMPETING CONSUMERS] Worker {Worker} descarta la orden {OrderId}: ya la procesó otra réplica",
                _workerName, orderId);
            return true;
        }

        db.OrderEvents.Add(orderEvent);
        db.OutboxMessages.Add(outboxMessage);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("[COMPETING CONSUMERS] Worker {Worker}: orden {OrderId} {Status}{Reason}",
            _workerName, orderId, newStatus, rejection is null ? "" : $" [{rejection}]");
        Count(newStatus);
        return true;
    }

    private void Count(string result) =>
        OrdersProcessed.Add(1, new("worker", _workerName), new("result", result));
}
