using System.Globalization;
using Apache.NMS;
using Apache.NMS.ActiveMQ;

namespace Ucu.Andis.LegacyErp;

public class Erp : BackgroundService
{
    private readonly string _brokerUri;
    private readonly int _maxQuantity;
    private readonly ILogger<Erp> _logger;

    public Erp(IConfiguration config, ILogger<Erp> logger)
    {
        _brokerUri = $"failover:(tcp://{config["LegacyBroker:Host"] ?? "localhost"}:61616)?transport.startupMaxReconnectAttempts=0";
        // Saga: un pedido de más unidades necesita una aprobación que el ERP no
        // pide; lo rechaza, y Orders cancela la Order -ver la demo 10-.
        _maxQuantity = config.GetValue("Erp:MaxQuantity", 10);
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // CreateConnectionAsync y ReceiveAsync bloquean mientras el broker no está.
        await Task.Yield();

        using var connection = await new ConnectionFactory(_brokerUri).CreateConnectionAsync();
        await connection.StartAsync();
        // Transaccional: el pedido se consume y la factura se envía juntos, o nada.
        using var session = await connection.CreateSessionAsync(AcknowledgementMode.Transactional);
        using var orders = await session.GetQueueAsync("ERP.PEDIDOS");
        using var invoices = await session.GetQueueAsync("ERP.FACTURAS");
        using var consumer = await session.CreateConsumerAsync(orders);
        using var producer = await session.CreateProducerAsync(invoices);
        producer.DeliveryMode = MsgDeliveryMode.Persistent;
        _logger.LogInformation("[ERP] Esperando pedidos en ERP.PEDIDOS");

        while (!stoppingToken.IsCancellationRequested)
        {
            if (await consumer.ReceiveAsync(TimeSpan.FromSeconds(1)) is not ITextMessage message)
                continue;

            var record = message.Text;
            if (record.Length != 55 || !record.StartsWith("PEDIDO  ", StringComparison.Ordinal))
            {
                _logger.LogWarning("[ERP] Registro inválido: '{Record}'", record);
                await session.CommitAsync();
                continue;
            }

            // La factura sale del número de pedido: si el mismo pedido llega dos
            // veces, se emite la misma factura, o el mismo rechazo.
            var order = record[8..18];
            var response = int.Parse(record[50..55], CultureInfo.InvariantCulture) > _maxQuantity
                ? $"RECHAZO {order}{$"MAS DE {_maxQuantity} UNIDADES",-30}"
                : $"FACTURA {order}{"F" + order,-12}{DateTime.UtcNow:yyyyMMdd}";
            await producer.SendAsync(await session.CreateTextMessageAsync(response));
            await session.CommitAsync();
            _logger.LogInformation("[ERP] Recibido '{Record}'; emitido '{Response}'", record, response);
        }
    }
}
