using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Ucu.Andis.OrdersService.Models;

/// <summary>
/// Representa una clave que un cliente mandó en el encabezado
/// <c>Idempotency-Key</c> de <c>POST /orders</c>, con la Order que creó.
/// </summary>
/// <remarks>
/// Idempotencia: si el cliente reintenta con la misma clave, se le responde con esa
/// Order en lugar de crear otra -ver <c>Controllers.OrdersController</c>-.
///
/// La clave es de un User: dos clientes distintos pueden elegir la misma sin
/// pisarse. Las solicitudes anónimas comparten el User 0.
///
/// No se guarda en la tabla <c>orders</c> porque esa tabla es una proyección:
/// <c>POST /orders/projections/rebuild</c> la borra y la reconstruye a partir de
/// los eventos, y la clave se perdería.
/// </remarks>
[Table("idempotency_keys")]
[PrimaryKey(nameof(Key), nameof(UserId))]
public class IdempotencyKey
{
    [Column("idempotency_key")]
    public string Key { get; set; } = string.Empty;

    [Column("user_id")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// Obtiene o establece el SHA-256 de los datos de la solicitud.
    /// </summary>
    /// <remarks>
    /// Distingue un reintento de un uso equivocado de la misma clave con otra Order.
    /// </remarks>
    [Column("request_hash")]
    public string RequestHash { get; set; } = string.Empty;

    [Column("order_id")]
    public int OrderId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
