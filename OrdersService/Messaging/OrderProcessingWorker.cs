using System.Diagnostics.Metrics;
using System.Text;
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
public class OrderProcessingWorker : BackgroundService
{
    public const string MeterName = "Ucu.Andis.OrdersWorker";

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
        await RabbitMq.DeclareOrdersQueueAsync(channel, stoppingToken);

        // Cada réplica toma un solo mensaje a la vez: así el trabajo se reparte
        // según la capacidad de cada una, en lugar de que una acapare la cola.
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            var orderId = int.Parse(Encoding.UTF8.GetString(delivery.Body.Span));
            if (await ProcessAsync(orderId, stoppingToken))
            {
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            }
            else
            {
                await Task.Delay(RequeueDelay, stoppingToken);
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, stoppingToken);
            }
        };

        await channel.BasicConsumeAsync(RabbitMq.OrdersQueue, autoAck: false, consumer, stoppingToken);
        _logger.LogInformation("[COMPETING CONSUMERS] Worker {Worker} escuchando la cola {Queue}", _workerName, RabbitMq.OrdersQueue);

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
    private async Task<bool> ProcessAsync(int orderId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
        var httpClientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

        var order = await db.Orders.FindAsync([orderId], cancellationToken);
        if (order is null || order.Status != OrderStatus.Pending)
            return true;

        _logger.LogInformation("[COMPETING CONSUMERS] Worker {Worker} procesando la orden {OrderId}", _workerName, orderId);
        await Task.Delay(_processingDelay, cancellationToken);

        var user = await Dependencies.GetAsync<UserDto>(httpClientFactory.CreateClient("UsersService"), $"{order.UserId}", cancellationToken);
        var product = await Dependencies.GetAsync<ProductDto>(httpClientFactory.CreateClient("ProductsService"), $"{order.ProductId}", cancellationToken);

        if (!user.Available || !product.Available)
        {
            _logger.LogWarning("Worker {Worker}: la orden {OrderId} vuelve a la cola (usuarios: {Users}, productos: {Products})",
                _workerName, orderId, user.Failure ?? "ok", product.Failure ?? "ok");
            Count("Requeued");
            return false;
        }

        var rejection = user.NotFound ? $"El usuario {order.UserId} no existe"
            : product.NotFound ? $"El producto {order.ProductId} no existe"
            : null;

        var orderEvent = rejection is null
            ? OrderEvent.Create(orderId, OrderEventTypes.OrderConfirmed, new OrderConfirmedData(_workerName))
            : OrderEvent.Create(orderId, OrderEventTypes.OrderRejected, new OrderRejectedData(rejection, _workerName));

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.OrderEvents.Add(orderEvent);
        order.Status = rejection is null ? OrderStatus.Confirmed : OrderStatus.Rejected;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("[COMPETING CONSUMERS] Worker {Worker}: orden {OrderId} {Status}{Reason}",
            _workerName, orderId, order.Status, rejection is null ? "" : $" ({rejection})");
        Count(order.Status.ToString());
        return true;
    }

    private void Count(string result) =>
        OrdersProcessed.Add(1, new("worker", _workerName), new("result", result));
}
