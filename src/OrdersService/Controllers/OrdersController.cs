using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Ucu.Andis.Common;
using Ucu.Andis.OrdersService.Data;
using Ucu.Andis.OrdersService.Models;

namespace Ucu.Andis.OrdersService.Controllers;

[ApiController]
[Route("orders")]
public class OrdersController : ControllerBase
{
    private readonly OrdersDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;

    public OrdersController(OrdersDbContext db, IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
    }

    // POST /orders
    // Guarda la orden como Pending junto con su evento OrderCreated y deja el
    // mensaje order.created en el outbox, los tres en la misma transacción.
    // Transactional Outbox: no se publica en RabbitMQ acá, lo hace OutboxRelay.
    // Así no puede quedar una orden guardada sin su mensaje, ni un mensaje de
    // una orden que no se guardó, y la orden se acepta aunque RabbitMQ no esté.
    // Asynchronous Request-Reply: responde 202 Accepted, porque la orden todavía
    // no se procesó, con Location para consultar su estado y Retry-After para
    // indicar cada cuánto.
    [HttpPost]
    public async Task<ActionResult<object>> CreateOrder([FromBody] Order order)
    {
        order.Status = OrderStatus.Pending;

        await using (var transaction = await _db.Database.BeginTransactionAsync())
        {
            _db.Orders.Add(order);
            await _db.SaveChangesAsync();
            _db.OrderEvents.Add(OrderEvent.Create(order.Id, OrderEventTypes.OrderCreated,
                new OrderCreatedData(order.UserId, order.ProductId, order.Quantity)));
            _db.OutboxMessages.Add(OutboxMessage.Create(Events.OrderCreated,
                new OrderCreated(order.Id, order.UserId, order.ProductId, order.Quantity)));
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        Response.Headers.RetryAfter = "2";
        return Accepted($"/orders/{order.Id}/status", new { id = order.Id, status = order.Status, msg = "Order accepted" });
    }

    // GET /orders/{id}/status
    // Asynchronous Request-Reply: mientras la orden está Pending responde 200 con
    // el estado y Retry-After. Cuando un worker la confirma o la rechaza,
    // responde 303 See Other a la orden: el cliente sigue la redirección y
    // obtiene el resultado.
    [HttpGet("{id:int}/status")]
    public async Task<ActionResult<object>> GetOrderStatus(int id)
    {
        var order = await _db.Orders.FindAsync(id);
        if (order is null)
            return NotFound();

        if (order.Status != OrderStatus.Pending)
        {
            Response.Headers.Location = $"/orders/{id}";
            return StatusCode(StatusCodes.Status303SeeOther);
        }

        Response.Headers.RetryAfter = "2";
        return Ok(new { id = order.Id, status = order.Status });
    }

    // GET /orders
    [HttpGet]
    public async Task<ActionResult<List<object>>> ListOrders()
    {
        var orders = await _db.Orders
            .Select(o => new
            {
                id = o.Id,
                user_id = o.UserId,
                product_id = o.ProductId,
                quantity = o.Quantity,
                status = o.Status
            })
            .ToListAsync();

        return Ok(orders);
    }

    // GET /orders/{id}
    [HttpGet("{id:int}")]
    public async Task<ActionResult<object>> GetOrder(int id)
    {
        var order = await _db.Orders.FindAsync(id);
        if (order is null)
            return NotFound();

        return Ok(new { id = order.Id, user_id = order.UserId, product_id = order.ProductId, quantity = order.Quantity, status = order.Status });
    }

    // GET /orders/{id}/events
    // Event Sourcing: historial de eventos de la orden y el estado que resulta
    // de reproducirlos.
    [HttpGet("{id:int}/events")]
    public async Task<ActionResult<object>> GetOrderEvents(int id)
    {
        var events = await _db.OrderEvents
            .Where(e => e.OrderId == id)
            .OrderBy(e => e.Id)
            .ToListAsync();

        if (events.Count == 0)
            return NotFound();

        var state = OrderProjection.Replay(events);
        return Ok(new
        {
            order_id = id,
            events = events.Select(e => new { e.Id, e.Type, data = System.Text.Json.JsonDocument.Parse(e.Data).RootElement, occurred_at = e.OccurredAt }),
            replayed_state = state is null ? null : new { state.UserId, state.ProductId, state.Quantity, state.Status }
        });
    }

    // POST /orders/projections/rebuild
    // Event Sourcing: borra la tabla orders y la vuelve a construir reproduciendo
    // todos los eventos. Muestra que los eventos son la fuente de verdad.
    [HttpPost("projections/rebuild")]
    public async Task<ActionResult<object>> RebuildProjections()
    {
        var events = await _db.OrderEvents.OrderBy(e => e.Id).ToListAsync();
        var orders = events
            .GroupBy(e => e.OrderId)
            .Select(OrderProjection.Replay)
            .OfType<Order>()
            .ToList();

        await using var transaction = await _db.Database.BeginTransactionAsync();
        await _db.Orders.ExecuteDeleteAsync();
        _db.Orders.AddRange(orders);
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        return Ok(new { events = events.Count, orders_rebuilt = orders.Count });
    }

    // GET /orders/user/{userId}/details
    // Degradación controlada: si Usuarios o Productos no están disponibles, la
    // respuesta incluye las órdenes igual, con "no disponible" y el motivo.
    // Throttling: el atributo aplica la política "throttling" de Program.cs. Si
    // ya hay 20 consultas de detalle en curso, el middleware responde 503 con
    // Retry-After y este método no llega a ejecutarse. Es la consulta que se
    // limita porque es la más cara: lee la base y llama a Users y a Products.
    [HttpGet("user/{userId}/details")]
    [EnableRateLimiting("throttling")]
    public async Task<ActionResult<List<object>>> GetOrdersForUser(int userId)
    {
        var orders = await _db.Orders
            .Where(o => o.UserId == userId)
            .ToListAsync();

        if (orders.Count == 0)
            return NotFound();

        var usersClient = _httpClientFactory.CreateClient("UsersService");
        var user = await Dependencies.GetUserAsync(usersClient, userId, HttpContext.RequestAborted);

        if (user.NotFound)
            return NotFound("User not found");

        var customerName = user.Available ? user.Value?.Name : $"no disponible [{user.Failure}]";

        var productsClient = _httpClientFactory.CreateClient("ProductsService");

        var result = new List<object>();
        // Una llamada a Products por Order, de a una. Cada llamada pasa por el
        // compartimento del Bulkhead de Products por separado: si está lleno,
        // esa Order queda con "no disponible [bulkhead lleno]" y se sigue con la
        // próxima, que vuelve a intentar conseguir un lugar.
        foreach (var order in orders)
        {
            var product = await Dependencies.GetAsync<ProductDto>(productsClient, $"{order.ProductId}", HttpContext.RequestAborted);
            var productName = product.Available ? product.Value?.Name : $"no disponible [{product.Failure}]";

            result.Add(new
            {
                order_id = order.Id,
                customer_name = customerName,
                product_name = productName,
                quantity = order.Quantity,
                status = order.Status
            });
        }

        return Ok(result);
    }
}
