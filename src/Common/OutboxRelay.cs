using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ucu.Andis.Common;

/// <summary>
/// Proporciona las métricas del relay, con el mismo nombre en cada servicio que
/// lo usa.
/// </summary>
public static class OutboxRelay
{
    public const string MeterName = "Ucu.Andis.Outbox";

    internal static readonly Meter Meter = new(MeterName);
    internal static readonly Counter<long> MessagesPublished = Meter.CreateCounter<long>("outbox.published", "{message}",
        "Mensajes del outbox publicados en RabbitMQ");
}

/// <summary>
/// Representa el servicio en segundo plano que publica en RabbitMQ los mensajes
/// que el servicio dejó en su tabla <c>outbox_messages</c>.
/// </summary>
/// <remarks>
/// Transactional Outbox: en Orders, los mensajes los dejan <c>POST /orders/</c>
/// y el worker; en Products, la reserva y la liberación de stock. Cada segundo
/// toma los pendientes en orden, los publica y les completa la fecha de
/// publicación. Si RabbitMQ no está, los mensajes quedan pendientes y se
/// reintentan en la próxima vuelta: no se pierde ninguno.
///
/// La entrega es al menos una vez, no exactamente una: si el relay se cae
/// después de publicar un mensaje y antes de marcarlo, lo vuelve a publicar al
/// arrancar. Por eso los suscriptores tienen que ser idempotentes.
/// </remarks>
public class OutboxRelay<TDbContext> : BackgroundService
    where TDbContext : DbContext, IOutboxDbContext
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly RabbitMq _rabbitMq;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger _logger;
    private long _pending;
    private bool _rabbitMqDown;

    public OutboxRelay(RabbitMq rabbitMq, IServiceScopeFactory scopeFactory, ILoggerFactory loggerFactory)
    {
        _rabbitMq = rabbitMq;
        _scopeFactory = scopeFactory;
        _logger = loggerFactory.CreateLogger("OutboxRelay");
        // Mensajes que esperan ser publicados: sube si RabbitMQ no está y vuelve
        // a cero cuando el relay se pone al día.
        OutboxRelay.Meter.CreateObservableGauge("outbox.pending", () => Interlocked.Read(ref _pending), "{message}",
            "Mensajes del outbox pendientes de publicar");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("[OUTBOX] No se pudo leer el outbox: {Message}", ex.Message);
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task PublishPendingAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TDbContext>();

        // Hasta 50 mensajes por vuelta. FOR UPDATE SKIP LOCKED bloquea los que
        // toma esta instancia y saltea los que ya tomó otra: con más de una
        // instancia del relay, cada mensaje lo publica una sola. La sentencia es
        // la misma en MySQL y en PostgreSQL.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var messages = await db.OutboxMessages
            .FromSqlRaw("SELECT * FROM outbox_messages WHERE published_at IS NULL ORDER BY id LIMIT 50 FOR UPDATE SKIP LOCKED")
            .ToListAsync(cancellationToken);

        var published = 0;
        var failed = false;
        foreach (var message in messages)
        {
            try
            {
                await _rabbitMq.PublishEventAsync(message.RoutingKey, message.Payload, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Se corta acá para no publicar fuera de orden: este mensaje y los
                // siguientes quedan pendientes para la próxima vuelta.
                if (!_rabbitMqDown)
                    _logger.LogWarning("[OUTBOX] RabbitMQ no está disponible; los mensajes quedan pendientes: {Message}", ex.Message);
                failed = true;
                break;
            }

            message.PublishedAt = DateTime.UtcNow;
            published++;
            _logger.LogInformation("[OUTBOX] Publicado el mensaje {Id}: {RoutingKey} {Payload}", message.Id, message.RoutingKey, message.Payload);
        }

        if (_rabbitMqDown && !failed && published > 0)
            _logger.LogInformation("[OUTBOX] RabbitMQ volvió; se publicaron los mensajes pendientes");
        if (failed || published > 0)
            _rabbitMqDown = failed;

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        OutboxRelay.MessagesPublished.Add(published);

        Interlocked.Exchange(ref _pending, await db.OutboxMessages.LongCountAsync(m => m.PublishedAt == null, cancellationToken));
    }
}
