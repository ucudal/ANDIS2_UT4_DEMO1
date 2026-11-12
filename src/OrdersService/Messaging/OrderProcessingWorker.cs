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
// Saga: el worker es el paso de Orders en la coreografía. La cola
// orders.process es su suscripción, en el exchange andis.events, a lo que
// responden los otros participantes:
//
// - stock.reserved: Products reservó el stock. El worker verifica el User y
//   confirma la Order, o la rechaza si el User no existe; con order.rejected,
//   Products libera la reserva.
// - stock.rejected: Products no reservó nada. El worker rechaza la Order.
// - order.invoice-failed: el ERP no facturó una Order confirmada. El worker la
//   cancela, y con order.cancelled Products libera la reserva.
//
// Nadie coordina la Saga: cada participante reacciona a los eventos de los
// otros -ver PATTERNS.md-.
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
        "Órdenes procesadas por el worker: Confirmed, Rejected, Cancelled o Requeued");

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
        await RabbitMq.DeclareSubscriberQueueAsync(channel, OrdersQueue,
            [Events.StockReserved, Events.StockRejected, Events.OrderInvoiceFailed], stoppingToken);

        // Cada réplica toma un solo mensaje a la vez: así el trabajo se reparte
        // según la capacidad de cada una, en lugar de que una acapare la cola.
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            bool processed;
            try
            {
                processed = await HandleAsync(delivery.RoutingKey, delivery.Body, stoppingToken);
            }
            catch (JsonException)
            {
                // Un mensaje mal formado no se va a poder procesar nunca: se
                // descarta en lugar de devolverlo a la cola para siempre.
                _logger.LogWarning("Worker {Worker}: se descarta un mensaje {RoutingKey} mal formado", _workerName, delivery.RoutingKey);
                processed = true;
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
                _logger.LogError(ex, "Worker {Worker}: no se pudo procesar {RoutingKey}; vuelve a la cola", _workerName, delivery.RoutingKey);
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

    // Devuelve false si el mensaje no se pudo procesar porque una dependencia no
    // está disponible; en ese caso vuelve a la cola. Un mensaje con otra clave de
    // ruteo no es para el worker: se descarta.
    private Task<bool> HandleAsync(string routingKey, ReadOnlyMemory<byte> body, CancellationToken cancellationToken) =>
        routingKey switch
        {
            Events.StockReserved => ConfirmAsync(JsonSerializer.Deserialize<StockReserved>(body.Span)!.OrderId, cancellationToken),
            Events.StockRejected => RejectAsync(JsonSerializer.Deserialize<StockRejected>(body.Span)!, cancellationToken),
            Events.OrderInvoiceFailed => CancelAsync(JsonSerializer.Deserialize<OrderInvoiceFailed>(body.Span)!, cancellationToken),
            _ => Task.FromResult(true)
        };

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

    // Con el stock reservado, verifica el User y confirma la Order, o la
    // rechaza si el User no existe.
    //
    // Es idempotente: el outbox entrega cada mensaje al menos una vez, no
    // exactamente una, y una Order que ya no está Pending no se procesa de
    // nuevo. Pero preguntar si está Pending y después cambiarla son dos pasos
    // -check-then-act-, y con varias réplicas eso no alcanza:
    //
    // 1. Products publica dos veces el mismo stock.reserved -ver OutboxRelay.cs-.
    // 2. Las dos copias llegan a la vez a dos réplicas.
    // 3. Las dos leen la Order como Pending, esperan la demora y la llamada a
    //    Users, y las dos guardan el cambio.
    //
    // Quedarían dos OrderConfirmed en el historial y dos order.confirmed en el
    // outbox, que llegarían dos veces a los proyectores y al ERP.
    //
    // Por eso la lectura inicial solo evita trabajo inútil con un duplicado que
    // llega después; lo que decide es el primer paso de la transacción: un
    // UPDATE que cambia el estado solo si sigue Pending -ver ChangeStatusAsync-.
    private async Task<bool> ConfirmAsync(int orderId, CancellationToken cancellationToken)
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

        var user = await Dependencies.GetUserAsync(httpClientFactory.CreateClient("UsersService"), order.UserId!, cancellationToken);
        if (!user.Available)
        {
            _logger.LogWarning("Worker {Worker}: la orden {OrderId} vuelve a la cola [usuarios: {Users}]",
                _workerName, orderId, user.Failure);
            Count("Requeued");
            return false;
        }

        if (user.NotFound)
        {
            // Saga: el stock ya está reservado. order.rejected es lo que hace
            // que Products lo libere -la compensación-.
            var reason = $"El usuario {order.UserId} no existe";
            await ChangeStatusAsync(db, orderId, OrderStatus.Pending, OrderStatus.Rejected,
                OrderEvent.Create(orderId, OrderEventTypes.OrderRejected, new OrderRejectedData(reason, _workerName)),
                OutboxMessage.Create(Events.OrderRejected, new OrderRejected(orderId, reason)), reason, cancellationToken);
            return true;
        }

        await ChangeStatusAsync(db, orderId, OrderStatus.Pending, OrderStatus.Confirmed,
            OrderEvent.Create(orderId, OrderEventTypes.OrderConfirmed, new OrderConfirmedData(_workerName)),
            OutboxMessage.Create(Events.OrderConfirmed, new OrderConfirmed(orderId, order.UserId, order.ProductId, order.Quantity)),
            null, cancellationToken);
        return true;
    }

    // Products no reservó el stock: la Order se rechaza. No hay nada que
    // compensar; el order.rejected igual llega a Products, que no encuentra
    // una reserva que liberar.
    private async Task<bool> RejectAsync(StockRejected rejected, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        await ChangeStatusAsync(db, rejected.OrderId, OrderStatus.Pending, OrderStatus.Rejected,
            OrderEvent.Create(rejected.OrderId, OrderEventTypes.OrderRejected, new OrderRejectedData(rejected.Reason, _workerName)),
            OutboxMessage.Create(Events.OrderRejected, new OrderRejected(rejected.OrderId, rejected.Reason)),
            rejected.Reason, cancellationToken);
        return true;
    }

    // Saga: el ERP no facturó la Order. Ya estaba confirmada, así que no se
    // puede rechazar: se cancela, y order.cancelled hace que Products libere el
    // stock.
    private async Task<bool> CancelAsync(OrderInvoiceFailed failed, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var reason = $"El ERP no la facturó: {failed.Reason}";
        await ChangeStatusAsync(db, failed.OrderId, OrderStatus.Confirmed, OrderStatus.Cancelled,
            OrderEvent.Create(failed.OrderId, OrderEventTypes.OrderCancelled, new OrderCancelledData(reason, _workerName)),
            OutboxMessage.Create(Events.OrderCancelled, new OrderCancelled(failed.OrderId, reason)),
            reason, cancellationToken);
        return true;
    }

    // Transactional Outbox: el evento de Event Sourcing, el cambio de estado y
    // el mensaje para los suscriptores se guardan juntos.
    //
    // Competing Consumers: la transición de estado es condicional y atómica. Es
    // una sola sentencia, y MySQL bloquea la fila: si dos réplicas procesan a la
    // vez dos copias del mismo mensaje, la segunda espera a que la primera
    // confirme, ve que la Order ya no está en el estado de origen, no cambia
    // nada y la descarta. El índice único de order_events es una segunda
    // barrera -ver mysqlinit.sql-.
    private async Task ChangeStatusAsync(OrdersDbContext db, int orderId, string from, string to,
        OrderEvent orderEvent, OutboxMessage outboxMessage, string? reason, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var changed = await db.Orders
            .Where(o => o.Id == orderId && o.Status == from)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, to), cancellationToken);
        if (changed == 0)
        {
            // Otra réplica ya la procesó: no se agrega ni el evento ni el mensaje.
            await transaction.RollbackAsync(cancellationToken);
            _logger.LogInformation("[COMPETING CONSUMERS] Worker {Worker} descarta la orden {OrderId}: no está {Status}",
                _workerName, orderId, from);
            return;
        }

        db.OrderEvents.Add(orderEvent);
        db.OutboxMessages.Add(outboxMessage);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("[SAGA] Worker {Worker}: orden {OrderId} {Status}{Reason}",
            _workerName, orderId, to, reason is null ? "" : $" [{reason}]");
        Count(to);
    }

    private void Count(string result) =>
        OrdersProcessed.Add(1, new("worker", _workerName), new("result", result));
}
