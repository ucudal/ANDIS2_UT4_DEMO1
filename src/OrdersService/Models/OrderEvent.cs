using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace Ucu.Andis.OrdersService.Models;

/// <summary>
/// Representa un cambio de una orden, guardado como un evento inmutable.
/// </summary>
/// <remarks>
/// Event Sourcing: los eventos solo se agregan, nunca se modifican ni se borran.
/// </remarks>
[Table("order_events")]
public class OrderEvent
{
    [Column("id")]
    public long Id { get; set; }

    [Column("order_id")]
    public int OrderId { get; set; }

    [Column("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Obtiene o establece los datos del evento en JSON.
    /// </summary>
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
    public const string OrderInvoiced = "OrderInvoiced";
    public const string OrderCancelled = "OrderCancelled";
}

public record OrderCreatedData(string UserId, int ProductId, int Quantity);

public record OrderConfirmedData(string ProcessedBy);

public record OrderRejectedData(string Reason, string ProcessedBy);

public record OrderInvoicedData(string InvoiceNumber, string IssuedOn);

public record OrderCancelledData(string Reason, string ProcessedBy);
