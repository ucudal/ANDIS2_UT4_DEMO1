using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ucu.Andis.OrdersService.Data;

namespace Ucu.Andis.OrdersService.Controllers;

// CQRS: el lado de las consultas. Lee solo la vista materializada
// order_details, que tiene la forma exacta de la respuesta: una consulta a
// MySQL y ninguna llamada a Users ni a Products. El lado de los comandos -crear
// una Order y procesarla- está en OrdersController y en OrderProcessingWorker,
// y escribe en otras tablas.
//
// No tiene Throttling: es tan barata que no hace falta protegerla como a
// GET /orders/user/{userId}/details.
[ApiController]
[Route("orders")]
public class OrdersQueriesController : ControllerBase
{
    private readonly OrdersDbContext _db;

    public OrdersQueriesController(OrdersDbContext db)
    {
        _db = db;
    }

    // GET /orders/user/{userId}/details/view
    // La misma respuesta que GET /orders/user/{userId}/details. Si Users o
    // Products no están disponibles, igual responde con los nombres, porque ya
    // están copiados. A cambio, es eventualmente consistente: una Order recién
    // creada puede no estar todavía, y un nombre recién cambiado puede ser el
    // anterior.
    [HttpGet("user/{userId}/details/view")]
    public async Task<ActionResult<List<object>>> GetOrdersForUser(int userId)
    {
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
