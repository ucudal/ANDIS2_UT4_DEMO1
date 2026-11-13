using Apache.NMS;
using Apache.NMS.ActiveMQ;

namespace Ucu.Andis.MessagingBridge;

/// <summary>
/// Representa la conexión con ActiveMQ, el broker del ERP.
/// </summary>
/// <remarks>
/// <c>failover</c> hace que el cliente se reconecte solo si el broker se reinicia;
/// <c>timeout</c>, que un envío con el broker caído falle a los 3 segundos en lugar
/// de quedar bloqueado para siempre.
/// </remarks>
public class LegacyBroker : IDisposable
{
    public const string OrdersQueue = "ERP.PEDIDOS";
    public const string InvoicesQueue = "ERP.FACTURAS";

    private readonly ConnectionFactory _factory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public LegacyBroker(IConfiguration config)
    {
        var host = config["LegacyBroker:Host"] ?? "localhost";
        _factory = new ConnectionFactory($"failover:(tcp://{host}:61616)?transport.timeout=3000&transport.startupMaxReconnectAttempts=0");
    }

    public async Task<IConnection> GetConnectionAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_connection is null)
            {
                var connection = await _factory.CreateConnectionAsync();
                await connection.StartAsync();
                _connection = connection;
            }
            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _lock.Dispose();
    }
}
