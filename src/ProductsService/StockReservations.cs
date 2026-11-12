using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ProductsService.Data;
using ProductsService.Models;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ucu.Andis.Common;

namespace ProductsService;

// Saga: el paso de Products en la coreografía. Escucha los eventos de Orders en
// su propia cola, products.stock, y responde con los suyos:
//
// - order.created: reserva el stock y publica stock.reserved, o stock.rejected
//   si falta stock o el Product no existe.
// - order.rejected y order.cancelled: la compensación. Libera la reserva de esa
//   Order, si la hay, y publica stock.released.
//
// Transactional Outbox: el stock, la reserva y el evento se guardan en una sola
// transacción de PostgreSQL, y OutboxRelay publica el evento después. Sin el
// outbox, Products podría descontar el stock y caerse antes de publicar: la
// Order quedaría Pending para siempre, con el stock descontado.
//
// Idempotencia: RabbitMQ y los outbox entregan cada mensaje al menos una vez.
// Una reserva repetida falla por la clave primaria, y una liberación repetida no
// encuentra una reserva que liberar -ver ReserveAsync y ReleaseAsync-.
public class StockReservations : BackgroundService
{
    public const string StockQueue = "products.stock";

    private static readonly TimeSpan RequeueDelay = TimeSpan.FromSeconds(5);

    private readonly RabbitMq _rabbitMq;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProductCache _cache;
    private readonly ILogger<StockReservations> _logger;

    public StockReservations(RabbitMq rabbitMq, IServiceScopeFactory scopeFactory, ProductCache cache, ILogger<StockReservations> logger)
    {
        _rabbitMq = rabbitMq;
        _scopeFactory = scopeFactory;
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = await ConnectAsync(stoppingToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await RabbitMq.DeclareSubscriberQueueAsync(channel, StockQueue,
            [Events.OrderCreated, Events.OrderRejected, Events.OrderCancelled], stoppingToken);
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            bool handled;
            try
            {
                handled = await HandleAsync(delivery.RoutingKey, delivery.Body, stoppingToken);
            }
            catch (JsonException)
            {
                // Un mensaje mal formado no se va a poder procesar nunca.
                _logger.LogWarning("[SAGA] Se descarta un mensaje {RoutingKey} mal formado", delivery.RoutingKey);
                handled = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // La base no responde, un error de SQL: el mensaje vuelve a la
                // cola, como en el worker de Orders.
                _logger.LogError(ex, "[SAGA] No se pudo procesar {RoutingKey}; vuelve a la cola", delivery.RoutingKey);
                handled = false;
            }

            if (handled)
            {
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            }
            else
            {
                await Task.Delay(RequeueDelay, stoppingToken);
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, stoppingToken);
            }
        };

        await channel.BasicConsumeAsync(StockQueue, autoAck: false, consumer, stoppingToken);
        _logger.LogInformation("[SAGA] Products escuchando la cola {Queue}", StockQueue);

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

    private async Task<bool> HandleAsync(string routingKey, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        switch (routingKey)
        {
            case Events.OrderCreated:
                await ReserveAsync(JsonSerializer.Deserialize<OrderCreated>(body.Span)!, cancellationToken);
                return true;
            case Events.OrderRejected:
                await ReleaseAsync(JsonSerializer.Deserialize<OrderRejected>(body.Span)!.OrderId, cancellationToken);
                return true;
            case Events.OrderCancelled:
                await ReleaseAsync(JsonSerializer.Deserialize<OrderCancelled>(body.Span)!.OrderId, cancellationToken);
                return true;
            default:
                return true;
        }
    }

    // Descuenta el stock solo si alcanza, con un UPDATE condicional: dos
    // reservas a la vez del mismo Product no pueden dejarlo negativo, porque
    // PostgreSQL bloquea la fila y la segunda vuelve a evaluar la condición con
    // el stock que dejó la primera.
    //
    // Idempotencia: la reserva se inserta en la misma transacción. Si es la
    // segunda copia de order.created, la inserción falla por la clave primaria
    // -la Order- y la transacción se deshace entera, con el descuento. Preguntar
    // antes si la reserva existe no alcanzaría: dos copias en paralelo pasarían
    // las dos el chequeo y descontarían el stock dos veces.
    private async Task ReserveAsync(OrderCreated order, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var reserved = await db.Products
            .Where(p => p.Id == order.ProductId && p.Stock >= order.Quantity)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock - order.Quantity), cancellationToken) == 1;

        string? reason = null;
        if (!reserved)
            reason = await db.Products.AnyAsync(p => p.Id == order.ProductId, cancellationToken)
                ? $"No hay stock suficiente del producto {order.ProductId}"
                : $"El producto {order.ProductId} no existe";

        db.StockReservations.Add(new StockReservation
        {
            OrderId = order.OrderId,
            ProductId = order.ProductId,
            Quantity = order.Quantity,
            Status = reserved ? ReservationStatus.Reserved : ReservationStatus.Rejected,
            Reason = reason
        });
        db.OutboxMessages.Add(reserved
            ? OutboxMessage.Create(Events.StockReserved, new StockReserved(order.OrderId, order.ProductId, order.Quantity))
            : OutboxMessage.Create(Events.StockRejected, new StockRejected(order.OrderId, reason!)));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ProductsDbContext.IsDuplicateKey(ex))
        {
            // Al salir, la transacción se deshace, y con ella el descuento.
            _logger.LogInformation("[SAGA] La Order {OrderId} ya tenía una reserva; se ignora la copia de {RoutingKey}",
                order.OrderId, Events.OrderCreated);
            return;
        }
        await transaction.CommitAsync(cancellationToken);

        if (reserved)
        {
            await _cache.InvalidateAsync(ProductsController.ProductKey(order.ProductId), ProductsController.AllProductsKey);
            _logger.LogInformation("[SAGA] Reservadas {Quantity} unidades del Product {ProductId} para la Order {OrderId}",
                order.Quantity, order.ProductId, order.OrderId);
        }
        else
        {
            _logger.LogInformation("[SAGA] No se reservó stock para la Order {OrderId}: {Reason}", order.OrderId, reason);
        }
    }

    // La compensación: devuelve el stock de una reserva. Una transición de
    // estado condicional decide si hay algo que devolver: el UPDATE solo cambia
    // una reserva Reserved, y el stock se suma solo si cambió una fila. Una
    // copia repetida de order.rejected encuentra la reserva Released y no hace
    // nada; una Order cuya reserva se rechazó, tampoco.
    private async Task ReleaseAsync(int orderId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var released = await db.StockReservations
            .Where(r => r.OrderId == orderId && r.Status == ReservationStatus.Reserved)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, ReservationStatus.Released)
                .SetProperty(r => r.UpdatedAt, DateTime.UtcNow), cancellationToken);
        if (released == 0)
        {
            _logger.LogInformation("[SAGA] La Order {OrderId} no tiene stock reservado que liberar", orderId);
            return;
        }

        var reservation = await db.StockReservations.AsNoTracking().FirstAsync(r => r.OrderId == orderId, cancellationToken);
        await db.Products
            .Where(p => p.Id == reservation.ProductId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, p => p.Stock + reservation.Quantity), cancellationToken);
        db.OutboxMessages.Add(OutboxMessage.Create(Events.StockReleased,
            new StockReleased(orderId, reservation.ProductId, reservation.Quantity)));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await _cache.InvalidateAsync(ProductsController.ProductKey(reservation.ProductId), ProductsController.AllProductsKey);
        _logger.LogInformation("[SAGA] Liberadas {Quantity} unidades del Product {ProductId} de la Order {OrderId}",
            reservation.Quantity, reservation.ProductId, orderId);
    }
}
