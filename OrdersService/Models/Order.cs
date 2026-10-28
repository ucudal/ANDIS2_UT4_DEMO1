using System.ComponentModel.DataAnnotations.Schema;

namespace Ucu.Andis.OrdersService.Models;

[Table("orders")]
public class Order
{
    [Column("id")]
    public int Id { get; set; }

    [Column("user_id")]
    public int UserId { get; set; }

    [Column("product_id")]
    public string? ProductId { get; set; }

    [Column("quantity")]
    public int Quantity { get; set; }

    // Pending, Confirmed o Rejected. Esta tabla es una proyección: el estado se
    // deriva de los eventos de la orden -ver OrderEvent-.
    [Column("status")]
    public string Status { get; set; } = OrderStatus.Pending;
}

public static class OrderStatus
{
    public const string Pending = "Pending";
    public const string Confirmed = "Confirmed";
    public const string Rejected = "Rejected";
}
