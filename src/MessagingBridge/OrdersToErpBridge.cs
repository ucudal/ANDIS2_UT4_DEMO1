using System.Text.Json;
using Apache.NMS;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Ucu.Andis.Common;

namespace Ucu.Andis.MessagingBridge;

/// <summary>
/// Representa el puente de RabbitMQ a ActiveMQ.
/// </summary>
/// <remarks>
/// Cada Order confirmada pasa a la cola del ERP. El puente es un suscriptor más de
/// <c>andis.events</c>, y Orders no sabe que existe.
///
/// Confirma el mensaje en RabbitMQ recién después de que ActiveMQ lo recibió. Si
/// ActiveMQ no está, el mensaje vuelve a la cola <c>bridge.erp</c> y espera ahí: no
/// se pierde. A cambio, la entrega es al menos una vez: si el puente se cae entre
/// el envío y la confirmación, el ERP recibe el pedido dos veces.
/// </remarks>
public class OrdersToErpBridge : BackgroundService
{
    public const string BridgeQueue = "bridge.erp";

    private static readonly TimeSpan RequeueDelay = TimeSpan.FromSeconds(5);

    private readonly RabbitMq _rabbitMq;
    private readonly LegacyBroker _legacyBroker;
    private readonly ILogger<OrdersToErpBridge> _logger;

    public OrdersToErpBridge(RabbitMq rabbitMq, LegacyBroker legacyBroker, ILogger<OrdersToErpBridge> logger)
    {
        _rabbitMq = rabbitMq;
        _legacyBroker = legacyBroker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connection = await ConnectAsync(stoppingToken);
        var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await RabbitMq.DeclareSubscriberQueueAsync(channel, BridgeQueue, [Events.OrderConfirmed], stoppingToken);

        // De a un mensaje, para que los pedidos lleguen al ERP en orden.
        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, delivery) =>
        {
            OrderConfirmed? order;
            try
            {
                order = JsonSerializer.Deserialize<OrderConfirmed>(delivery.Body.Span);
            }
            catch (JsonException)
            {
                order = null;
            }
            if (order is null)
            {
                _logger.LogWarning("[MESSAGING BRIDGE] Se descarta un mensaje mal formado");
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
                return;
            }

            try
            {
                await SendToErpAsync(ErpFormat.ToOrderRecord(order));
                await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("[MESSAGING BRIDGE] ActiveMQ no está disponible; la Order {OrderId} vuelve a {Queue}", order.OrderId, BridgeQueue);
                await Task.Delay(RequeueDelay, stoppingToken);
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, stoppingToken);
            }
        };

        await channel.BasicConsumeAsync(BridgeQueue, autoAck: false, consumer, stoppingToken);
        _logger.LogInformation("[MESSAGING BRIDGE] Escuchando la cola {Queue} de RabbitMQ", BridgeQueue);

        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    private async Task SendToErpAsync(string record)
    {
        var connection = await _legacyBroker.GetConnectionAsync();
        using var session = await connection.CreateSessionAsync();
        using var queue = await session.GetQueueAsync(LegacyBroker.OrdersQueue);
        using var producer = await session.CreateProducerAsync(queue);
        producer.DeliveryMode = MsgDeliveryMode.Persistent;
        await producer.SendAsync(await session.CreateTextMessageAsync(record));
        _logger.LogInformation("[MESSAGING BRIDGE] RabbitMQ -> ActiveMQ {Queue}: '{Record}'", LegacyBroker.OrdersQueue, record);
    }

    private async Task<RabbitMQ.Client.IConnection> ConnectAsync(CancellationToken stoppingToken)
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
}
