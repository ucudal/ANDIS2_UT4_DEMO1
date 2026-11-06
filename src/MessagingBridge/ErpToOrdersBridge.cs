using Apache.NMS;
using Ucu.Andis.Common;

namespace MessagingBridge;

// De ActiveMQ a RabbitMQ: cada factura del ERP se publica como un evento
// order.invoiced en andis.events. Para la aplicación es un evento más: no sabe
// que viene de otro broker.
//
// Igual que en el otro sentido, confirma el mensaje en ActiveMQ recién después
// de que RabbitMQ lo recibió.
public class ErpToOrdersBridge : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly RabbitMq _rabbitMq;
    private readonly LegacyBroker _legacyBroker;
    private readonly ILogger<ErpToOrdersBridge> _logger;

    public ErpToOrdersBridge(RabbitMq rabbitMq, LegacyBroker legacyBroker, ILogger<ErpToOrdersBridge> logger)
    {
        _rabbitMq = rabbitMq;
        _legacyBroker = legacyBroker;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ForwardInvoicesAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("[MESSAGING BRIDGE] Se reintenta leer {Queue}: {Message}", LegacyBroker.InvoicesQueue, ex.Message);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }

    private async Task ForwardInvoicesAsync(CancellationToken stoppingToken)
    {
        var connection = await _legacyBroker.GetConnectionAsync();
        using var session = await connection.CreateSessionAsync(AcknowledgementMode.IndividualAcknowledge);
        using var queue = await session.GetQueueAsync(LegacyBroker.InvoicesQueue);
        using var consumer = await session.CreateConsumerAsync(queue);
        _logger.LogInformation("[MESSAGING BRIDGE] Escuchando la cola {Queue} de ActiveMQ", LegacyBroker.InvoicesQueue);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (await consumer.ReceiveAsync(TimeSpan.FromSeconds(1)) is not ITextMessage message)
                continue;

            OrderInvoiced invoiced;
            try
            {
                invoiced = ErpFormat.ToOrderInvoiced(message.Text);
            }
            catch (FormatException ex)
            {
                _logger.LogWarning("[MESSAGING BRIDGE] Se descarta un mensaje mal formado: {Message}", ex.Message);
                await message.AcknowledgeAsync();
                continue;
            }

            await _rabbitMq.PublishEventAsync(Events.OrderInvoiced, invoiced, stoppingToken);
            await message.AcknowledgeAsync();
            _logger.LogInformation("[MESSAGING BRIDGE] ActiveMQ -> RabbitMQ {RoutingKey}: Order {OrderId}, factura {InvoiceNumber}",
                Events.OrderInvoiced, invoiced.OrderId, invoiced.InvoiceNumber);
        }
    }
}
