using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Ucu.Andis.OrdersService.Models;

// Event Sourcing: cada cambio de una orden se guarda como un evento inmutable.
// Los eventos solo se agregan, nunca se modifican ni se borran.
[Table("order_events")]
public class OrderEvent
{
    [Column("id")]
    public long Id { get; set; }

    [Column("order_id")]
    public int OrderId { get; set; }

    [Column("type")]
    public string Type { get; set; } = string.Empty;

    // Datos del evento en JSON.
    [Column("data")]
    public string Data { get; set; } = "{}";

    [Column("occurred_at")]
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    public static OrderEvent Create(int orderId, string type, object data) => new()
    {
        OrderId = orderId,
        Type = type,
        Data = JsonSerializer.Serialize(data)
    };
}

public static class OrderEventTypes
{
    public const string OrderCreated = "OrderCreated";
    public const string OrderConfirmed = "OrderConfirmed";
    public const string OrderRejected = "OrderRejected";
}

public record OrderCreatedData(int UserId, string? ProductId, int Quantity);

public record OrderConfirmedData(string ProcessedBy);

public record OrderRejectedData(string Reason, string ProcessedBy);
