using System.ComponentModel.DataAnnotations.Schema;

namespace Ucu.Andis.OrdersService.Models;

[Table("orders")]
public class Order
{
    [Column("id")]
    public int Id { get; set; }

    /// <summary>
    /// Obtiene o establece el ObjectId del User en Users, como texto de 24 caracteres.
    /// </summary>
    [Column("user_id")]
    public string? UserId { get; set; }

    [Column("product_id")]
    public int ProductId { get; set; }

    [Column("quantity")]
    public int Quantity { get; set; }

    /// <summary>
    /// Obtiene o establece el estado: <c>OrderStatus.Pending</c>, <c>OrderStatus.Confirmed</c>, <c>OrderStatus.Rejected</c> o <c>OrderStatus.Cancelled</c>.
    /// </summary>
    /// <remarks>
    /// Esta tabla es una proyección: el estado se deriva de los eventos de la orden
    /// -ver <c>OrderEvent</c>-.
    /// </remarks>
    [Column("status")]
    public string Status { get; set; } = OrderStatus.Pending;
}

public static class OrderStatus
{
    public const string Pending = "Pending";
    public const string Confirmed = "Confirmed";
    public const string Rejected = "Rejected";
    /// <summary>
    /// Estado de una Order confirmada que el ERP no pudo facturar.
    /// </summary>
    /// <remarks>
    /// Saga: es el resultado de la compensación.
    /// </remarks>
    public const string Cancelled = "Cancelled";
}
