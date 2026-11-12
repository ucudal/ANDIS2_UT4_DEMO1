using System.ComponentModel.DataAnnotations.Schema;

namespace ProductsService.Models;

// Saga: el stock que Products reservó para una Order. Hay una por Order -es la
// clave primaria-, así que una reserva repetida falla -ver pginit.sql-.
[Table("stock_reservations")]
public class StockReservation
{
    [Column("order_id")]
    public int OrderId { get; set; }

    [Column("product_id")]
    public int ProductId { get; set; }

    [Column("quantity")]
    public int Quantity { get; set; }

    [Column("status")]
    public string Status { get; set; } = ReservationStatus.Reserved;

    // Por qué se rechazó, si se rechazó.
    [Column("reason")]
    public string? Reason { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class ReservationStatus
{
    public const string Reserved = "Reserved";
    public const string Rejected = "Rejected";
    public const string Released = "Released";
}
