using System.Text;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace Ucu.Andis.OrdersService.Messaging;

// Conexión compartida con RabbitMQ. La conexión se crea la primera vez que se
// necesita y se reutiliza; los canales se crean por operación.
public class RabbitMq : IAsyncDisposable
{
    // Cola de órdenes pendientes de procesar. Todos los workers consumen de esta
    // misma cola: Competing Consumers.
    public const string OrdersQueue = "orders.process";

    private readonly ConnectionFactory _factory;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;

    public RabbitMq(IConfiguration config)
    {
        _factory = new ConnectionFactory
        {
            HostName = config["RabbitMq:Host"] ?? "localhost",
            UserName = config["RabbitMq:User"] ?? "guest",
            Password = config["RabbitMq:Password"] ?? "guest",
            ClientProvidedName = Environment.MachineName
        };
    }

    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is { IsOpen: true })
            return _connection;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is not { IsOpen: true })
            {
                if (_connection is not null)
                    await _connection.DisposeAsync();
                _connection = await _factory.CreateConnectionAsync(cancellationToken);
            }
            return _connection;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static Task DeclareOrdersQueueAsync(IChannel channel, CancellationToken cancellationToken = default) =>
        channel.QueueDeclareAsync(OrdersQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);

    public async Task PublishOrderAsync(int orderId, CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);
        await DeclareOrdersQueueAsync(channel, cancellationToken);

        var body = Encoding.UTF8.GetBytes(orderId.ToString());
        var properties = new BasicProperties { Persistent = true };
        await channel.BasicPublishAsync(string.Empty, OrdersQueue, mandatory: false, properties, body, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
        _lock.Dispose();
    }
}

public class RabbitMqHealthCheck : IHealthCheck
{
    private readonly RabbitMq _rabbitMq;

    public RabbitMqHealthCheck(RabbitMq rabbitMq)
    {
        _rabbitMq = rabbitMq;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await _rabbitMq.GetConnectionAsync(cancellationToken);
            return connection.IsOpen
                ? HealthCheckResult.Healthy("Conectado a RabbitMQ")
                : HealthCheckResult.Unhealthy("La conexión con RabbitMQ está cerrada");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("No se puede conectar a RabbitMQ", ex);
        }
    }
}
