using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Ucu.Andis.Common;

// Transactional Outbox: un evento que hay que publicar en RabbitMQ. Se guarda
// en la misma transacción que el cambio que lo origina, así que se guardan los
// dos o ninguno. OutboxRelay lo publica después y completa PublishedAt.
//
// Lo usan Orders, en MySQL, y Products, en PostgreSQL: cada uno tiene su tabla
// outbox_messages en su propia base.
[Table("outbox_messages")]
public class OutboxMessage
{
    [Column("id")]
    public long Id { get; set; }

    // Clave de ruteo en el exchange andis.events, por ejemplo order.created.
    [Column("routing_key")]
    public string RoutingKey { get; set; } = string.Empty;

    // Contenido del evento en JSON.
    [Column("payload")]
    public string Payload { get; set; } = "{}";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Nula mientras el mensaje está pendiente de publicar.
    [Column("published_at")]
    public DateTime? PublishedAt { get; set; }

    public static OutboxMessage Create<T>(string routingKey, T payload) => new()
    {
        RoutingKey = routingKey,
        Payload = JsonSerializer.Serialize(payload)
    };
}

// El DbContext de un servicio con outbox: OutboxRelay solo necesita la tabla de
// mensajes.
public interface IOutboxDbContext
{
    DbSet<OutboxMessage> OutboxMessages { get; }
}
