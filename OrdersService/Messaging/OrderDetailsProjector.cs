using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ucu.Andis.Common;
using Ucu.Andis.OrdersService.Data;
using Ucu.Andis.OrdersService.Models;

namespace Ucu.Andis.OrdersService.Messaging;

// Materialized View: mantiene la tabla order_details a partir de los eventos.
// Publisher/Subscriber: se suscribe con su propia cola a los eventos de Orders,
// Users y Products; recibe order.created igual que el worker, sin que Orders
// sepa que existe.
//
// Corre en orders-projector, con una sola réplica: los eventos de una misma
// Order llegan y se aplican en orden.
public class OrderDetailsProjector : BackgroundService
{
    public const string DetailsQueue = "orders.details-view";

    private static readonly TimeSpan RequeueDelay = TimeSpan.FromSeconds(5);

    private readonly RabbitMq _rabbitMq;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrderDetailsProjector> _logger;

    public OrderDetailsProjector(RabbitMq rabbitMq, IServiceScopeFactory scopeFactory, ILogger<OrderDetailsProjector> logger)
    {
        _rabbitMq = rabbitMq;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = await ConnectAsync(stoppingToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await RabbitMq.DeclareSubscriberQueueAsync(channel, DetailsQueue,
            ["order.*", Events.UserUpdated, Events.ProductUpdated], stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            bool applied;
            try
            {
                applied = await ApplyAsync(delivery.RoutingKey, delivery.Body, stoppingToken);
            }
            catch (JsonException)
            {
                // Un mensaje mal formado no se va a poder aplicar nunca.
                _logger.LogWarning("[MATERIALIZED VIEW] Se descarta un mensaje {RoutingKey} mal formado", delivery.RoutingKey);
                applied = true;
            }

            if (applied)
            {
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            }
            else
            {
                await Task.Delay(RequeueDelay, stoppingToken);
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, stoppingToken);
            }
        };

        await channel.BasicConsumeAsync(DetailsQueue, autoAck: false, consumer, stoppingToken);
        _logger.LogInformation("[PUB/SUB] Proyector escuchando la cola {Queue}", DetailsQueue);

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

    // Devuelve false si el evento no se pudo aplicar porque una dependencia no
    // está disponible; en ese caso el mensaje vuelve a la cola.
    private async Task<bool> ApplyAsync(string routingKey, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        _logger.LogInformation("[PUB/SUB] Proyector recibió {RoutingKey}", routingKey);

        switch (routingKey)
        {
            case Events.OrderCreated:
                var created = JsonSerializer.Deserialize<OrderCreated>(body.Span)!;
                return await AddOrderAsync(db, scope.ServiceProvider.GetRequiredService<IHttpClientFactory>(), created, cancellationToken);

            case Events.OrderConfirmed:
                var confirmed = JsonSerializer.Deserialize<OrderConfirmed>(body.Span)!;
                await SetStatusAsync(db, confirmed.OrderId, OrderStatus.Confirmed, cancellationToken);
                return true;

            case Events.OrderRejected:
                var rejected = JsonSerializer.Deserialize<OrderRejected>(body.Span)!;
                await SetStatusAsync(db, rejected.OrderId, OrderStatus.Rejected, cancellationToken);
                return true;

            // Los nombres copiados en la vista se actualizan cuando cambian en el
            // servicio que es dueño del dato. Hasta que llega el evento, la vista
            // muestra el nombre anterior: consistencia eventual.
            case Events.UserUpdated:
                var user = JsonSerializer.Deserialize<UserUpdated>(body.Span)!;
                var users = await db.OrderDetails
                    .Where(d => d.UserId == user.UserId)
                    .ExecuteUpdateAsync(d => d
                        .SetProperty(x => x.CustomerName, user.Name)
                        .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), cancellationToken);
                _logger.LogInformation("[MATERIALIZED VIEW] Nombre del User {UserId} actualizado en {Rows} filas", user.UserId, users);
                return true;

            case Events.ProductUpdated:
                var product = JsonSerializer.Deserialize<ProductUpdated>(body.Span)!;
                var products = await db.OrderDetails
                    .Where(d => d.ProductId == product.ProductId)
                    .ExecuteUpdateAsync(d => d
                        .SetProperty(x => x.ProductName, product.Name)
                        .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), cancellationToken);
                _logger.LogInformation("[MATERIALIZED VIEW] Nombre del Product {ProductId} actualizado en {Rows} filas", product.ProductId, products);
                return true;

            default:
                return true;
        }
    }

    // Busca una sola vez el nombre del cliente y del producto, al crearse la
    // Order, en lugar de en cada consulta de detalle. Usa los mismos HttpClient
    // que la consulta síncrona, con Retry, Circuit Breaker y Bulkhead.
    //
    // Es idempotente: si la fila ya existe, se actualiza en lugar de duplicarse,
    // porque el outbox puede entregar el mismo mensaje más de una vez. Si ya
    // llegó la confirmación o el rechazo, no se pisa el estado.
    private async Task<bool> AddOrderAsync(OrdersDbContext db, IHttpClientFactory httpClientFactory, OrderCreated created, CancellationToken cancellationToken)
    {
        var user = await Dependencies.GetUserAsync(httpClientFactory.CreateClient("UsersService"), created.UserId, cancellationToken);
        var product = await Dependencies.GetAsync<ProductDto>(httpClientFactory.CreateClient("ProductsService"), $"{created.ProductId}", cancellationToken);

        if (!user.Available || !product.Available)
        {
            _logger.LogWarning("[MATERIALIZED VIEW] La Order {OrderId} vuelve a la cola (usuarios: {Users}, productos: {Products})",
                created.OrderId, user.Failure ?? "ok", product.Failure ?? "ok");
            return false;
        }

        var details = await db.OrderDetails.FindAsync([created.OrderId], cancellationToken);
        if (details is null)
        {
            details = new OrderDetails { OrderId = created.OrderId, Status = OrderStatus.Pending };
            db.OrderDetails.Add(details);
        }

        details.UserId = created.UserId;
        details.CustomerName = user.Value?.Name;
        details.ProductId = created.ProductId;
        details.ProductName = product.Value?.Name;
        details.Quantity = created.Quantity;
        details.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("[MATERIALIZED VIEW] Order {OrderId} agregada a la vista: {Customer}, {Product}",
            created.OrderId, details.CustomerName ?? "User inexistente", details.ProductName ?? "Product inexistente");
        return true;
    }

    private async Task SetStatusAsync(OrdersDbContext db, int orderId, string status, CancellationToken cancellationToken)
    {
        var rows = await db.OrderDetails
            .Where(d => d.OrderId == orderId)
            .ExecuteUpdateAsync(d => d
                .SetProperty(x => x.Status, status)
                .SetProperty(x => x.UpdatedAt, DateTime.UtcNow), cancellationToken);

        if (rows == 0)
            _logger.LogWarning("[MATERIALIZED VIEW] La Order {OrderId} no está en la vista; se ignora el estado {Status}", orderId, status);
        else
            _logger.LogInformation("[MATERIALIZED VIEW] Order {OrderId} pasa a {Status}", orderId, status);
    }
}
