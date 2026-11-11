using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Ucu.Andis.OrdersService.Models;

// Idempotencia: cada clave que un cliente mandó en el encabezado
// Idempotency-Key de POST /orders, con la Order que creó. Si el cliente
// reintenta con la misma clave, se le responde con esa Order en lugar de crear
// otra -ver OrdersController.cs-.
//
// La clave es de un User: dos clientes distintos pueden elegir la misma sin
// pisarse. Las solicitudes anónimas comparten el User 0.
//
// No se guarda en la tabla orders porque esa tabla es una proyección: POST
// /orders/projections/rebuild la borra y la reconstruye a partir de los
// eventos, y la clave se perdería.
[Table("idempotency_keys")]
[PrimaryKey(nameof(Key), nameof(UserId))]
public class IdempotencyKey
{
    [Column("idempotency_key")]
    public string Key { get; set; } = string.Empty;

    [Column("user_id")]
    public int UserId { get; set; }

    // SHA-256 de los datos de la solicitud: distingue un reintento de un uso
    // equivocado de la misma clave con otra Order.
    [Column("request_hash")]
    public string RequestHash { get; set; } = string.Empty;

    [Column("order_id")]
    public int OrderId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
