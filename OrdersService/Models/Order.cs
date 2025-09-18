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
}