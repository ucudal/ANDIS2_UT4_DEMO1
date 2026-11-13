using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Ucu.Andis.Common;

/// <summary>
/// Representa un evento que hay que publicar en RabbitMQ.
/// </summary>
/// <remarks>
/// Transactional Outbox: se guarda en la misma transacción que el cambio que lo
/// origina, así que se guardan los dos o ninguno. <c>OutboxRelay</c> lo publica
/// después y completa <c>OutboxMessage.PublishedAt</c>.
///
/// Lo usan Orders, en MySQL, y Products, en PostgreSQL: cada uno tiene su tabla
/// <c>outbox_messages</c> en su propia base.
/// </remarks>
[Table("outbox_messages")]
public class OutboxMessage
{
    [Column("id")]
    public long Id { get; set; }

    /// <summary>
    /// Obtiene o establece la clave de ruteo en el exchange
    /// <c>andis.events</c>, por ejemplo <c>order.created</c>.
    /// </summary>
    [Column("routing_key")]
    public string RoutingKey { get; set; } = string.Empty;

    /// <summary>
    /// Obtiene o establece el contenido del evento en JSON.
    /// </summary>
    [Column("payload")]
    public string Payload { get; set; } = "{}";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Obtiene o establece el momento en que se publicó el mensaje.
    /// </summary>
    /// <remarks>
    /// Es nulo mientras el mensaje está pendiente de publicar.
    /// </remarks>
    [Column("published_at")]
    public DateTime? PublishedAt { get; set; }

    public static OutboxMessage Create<T>(string routingKey, T payload) => new()
    {
        RoutingKey = routingKey,
        Payload = JsonSerializer.Serialize(payload)
    };
}

/// <summary>
/// Define el DbContext de un servicio con outbox.
/// </summary>
/// <remarks>
/// <c>OutboxRelay</c> solo necesita la tabla de mensajes.
/// </remarks>
public interface IOutboxDbContext
{
    DbSet<OutboxMessage> OutboxMessages { get; }
}
