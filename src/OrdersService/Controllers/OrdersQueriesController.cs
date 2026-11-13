using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ucu.Andis.OrdersService.Data;

namespace Ucu.Andis.OrdersService.Controllers;

/// <summary>
/// Representa el lado de las consultas de CQRS.
/// </summary>
/// <remarks>
/// Lee solo la vista materializada <c>order_details</c>, que tiene la forma exacta
/// de la respuesta: una consulta a MySQL y ninguna llamada a Users ni a Products.
/// El lado de los comandos -crear una Order y procesarla- está en
/// <c>OrdersController</c> y en <c>Messaging.OrderProcessingWorker</c>, y escribe en
/// otras tablas.
///
/// No tiene Throttling: es tan barata que no hace falta protegerla como a
/// <c>GET /orders/user/{userId}/details</c>.
/// </remarks>
[ApiController]
[Route("orders")]
public class OrdersQueriesController : ControllerBase
{
    private readonly OrdersDbContext _db;
    private readonly ILogger<OrdersQueriesController> _logger;

    public OrdersQueriesController(OrdersDbContext db, ILogger<OrdersQueriesController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Devuelve el detalle de las Orders de un User desde la vista materializada, en
    /// <c>GET /orders/user/{userId}/details/view</c>.
    /// </summary>
    /// <remarks>
    /// Es la misma respuesta que <c>GET /orders/user/{userId}/details</c>. Si Users o
    /// Products no están disponibles, igual responde con los nombres, porque ya están
    /// copiados. A cambio, es eventualmente consistente: una Order recién creada puede
    /// no estar todavía, y un nombre recién cambiado puede ser el anterior.
    /// </remarks>
    [HttpGet("user/{userId}/details/view")]
    public async Task<ActionResult<List<object>>> GetOrdersForUser(string userId)
    {
        if (!UserIds.IsValid(userId))
            return BadRequest(new { detail = UserIds.InvalidDetail });

        // Federated Identity: un cliente ve solo sus Orders -ver CallerIdentity.cs-.
        if (CallerIdentity.From(Request) is { } caller && !caller.CanAccess(userId))
            return this.Forbidden(_logger, $"{caller.Name} no puede ver las Orders del User {userId}");

        var details = await _db.OrderDetails
            .Where(d => d.UserId == userId)
            .OrderBy(d => d.OrderId)
            .Select(d => new
            {
                order_id = d.OrderId,
                customer_name = d.CustomerName,
                product_name = d.ProductName,
                quantity = d.Quantity,
                status = d.Status
            })
            .ToListAsync();

        if (details.Count == 0)
            return NotFound();

        return Ok(details);
    }
}
