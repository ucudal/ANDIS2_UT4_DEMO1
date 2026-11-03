using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

namespace Ucu.Andis.Common;

// Conexión compartida con RabbitMQ. La conexión se crea la primera vez que se
// necesita y se reutiliza; los canales se crean por operación.
//
// Si RabbitMQ se reinicia, la conexión se recupera sola, junto con los
// consumidores, las colas y los enlaces: no hay que reemplazarla. Reemplazarla
// dejaría a los consumidores en la conexión vieja, sin recibir más mensajes.
public class RabbitMq : IAsyncDisposable
{
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
            ClientProvidedName = Environment.MachineName,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true
        };
    }

    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_connection is not null)
            return _connection;

        await _lock.WaitAsync(cancellationToken);
        try
        {
            return _connection ??= await _factory.CreateConnectionAsync(cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    // Publisher/Subscriber: el exchange donde se publican todos los eventos.
    // Durable, para que sobreviva a un reinicio de RabbitMQ.
    public static Task DeclareEventsExchangeAsync(IChannel channel, CancellationToken cancellationToken = default) =>
        channel.ExchangeDeclareAsync(Events.Exchange, ExchangeType.Topic, durable: true, autoDelete: false, cancellationToken: cancellationToken);

    // Declara la cola de un suscriptor y la enlaza con cada clave de ruteo. Una
    // clave puede tener comodines: "order.*" recibe order.created,
    // order.confirmed y order.rejected.
    public static async Task DeclareSubscriberQueueAsync(IChannel channel, string queue, string[] routingKeys, CancellationToken cancellationToken = default)
    {
        await DeclareEventsExchangeAsync(channel, cancellationToken);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        foreach (var routingKey in routingKeys)
            await channel.QueueBindAsync(queue, Events.Exchange, routingKey, cancellationToken: cancellationToken);
    }

    public Task PublishEventAsync<T>(string routingKey, T payload, CancellationToken cancellationToken = default) =>
        PublishEventAsync(routingKey, JsonSerializer.Serialize(payload), cancellationToken);

    // Publica un evento ya serializado en JSON. Con las confirmaciones del
    // publicador habilitadas, BasicPublishAsync termina recién cuando RabbitMQ
    // confirma que recibió el mensaje, y lanza una excepción si no lo recibió:
    // quien publica sabe si el evento llegó.
    public async Task PublishEventAsync(string routingKey, string json, CancellationToken cancellationToken = default)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await DeclareEventsExchangeAsync(channel, cancellationToken);

        var body = Encoding.UTF8.GetBytes(json);
        var properties = new BasicProperties { Persistent = true, ContentType = "application/json" };
        await channel.BasicPublishAsync(Events.Exchange, routingKey, mandatory: false, properties, body, cancellationToken);
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

    // Si RabbitMQ no responde, el resultado es el failureStatus con el que se
    // registró el chequeo: Unhealthy por defecto, o Degraded para un servicio
    // que puede seguir atendiendo sin RabbitMQ -ver el Program.cs de Orders-.
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var connection = await _rabbitMq.GetConnectionAsync(cancellationToken);
            return connection.IsOpen
                ? HealthCheckResult.Healthy("Conectado a RabbitMQ")
                : new HealthCheckResult(context.Registration.FailureStatus, "La conexión con RabbitMQ está cerrada");
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "No se puede conectar a RabbitMQ", ex);
        }
    }
}
