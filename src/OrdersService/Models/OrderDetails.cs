using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Ucu.Andis.OrdersService.Models;

/// <summary>
/// Representa el detalle de una Order ya armado, con el nombre del cliente y el del
/// producto copiados de Users y Products.
/// </summary>
/// <remarks>
/// Materialized View: la consulta de detalle lo lee tal cual, sin llamar a nadie.
/// Lo mantiene al día <c>Messaging.OrderDetailsProjector</c> a partir de los eventos; nadie
/// más lo escribe.
/// </remarks>
[Table("order_details")]
public class OrderDetails
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.None)]
    [Column("order_id")]
    public int OrderId { get; set; }

    [Column("user_id")]
    public string UserId { get; set; } = string.Empty;

    [Column("customer_name")]
    public string? CustomerName { get; set; }

    /// <summary>
    /// Obtiene o establece el momento en que cambió el nombre del cliente, según quien
    /// lo informó.
    /// </summary>
    /// <remarks>
    /// Con varios proyectores, un nombre más viejo no pisa a uno más nuevo -ver
    /// <c>Messaging.OrderDetailsProjector</c>-.
    /// </remarks>
    [Column("customer_name_at")]
    public DateTime? CustomerNameAt { get; set; }

    [Column("product_id")]
    public int ProductId { get; set; }

    [Column("product_name")]
    public string? ProductName { get; set; }

    [Column("product_name_at")]
    public DateTime? ProductNameAt { get; set; }

    [Column("quantity")]
    public int Quantity { get; set; }

    [Column("status")]
    public string Status { get; set; } = OrderStatus.Pending;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
