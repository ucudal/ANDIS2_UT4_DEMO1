using System.Security.Cryptography;
using System.Text;
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
    private readonly IConfiguration _config;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(OrdersDbContext db, IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<OrdersController> logger)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
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
    // Federated Identity: si la solicitud viene con identidad, la Order es del
    // User del token. El cuerpo puede omitir userId; si trae otro, se rechaza.
    // Idempotencia: el encabezado opcional Idempotency-Key evita crear dos veces
    // la misma Order cuando el cliente reintenta -ver más abajo-.
    [HttpPost]
    public async Task<ActionResult<object>> CreateOrder([FromBody] Order order)
    {
        var caller = CallerIdentity.From(Request);
        if (caller is not null)
        {
            if (caller.UserId is null)
                return this.Forbidden(_logger, $"{caller.Name} no es cliente: no puede crear Orders");
            if (order.UserId != 0 && order.UserId != caller.UserId)
                return this.Forbidden(_logger, $"{caller.Name} no puede crear Orders para el User {order.UserId}");
            order.UserId = caller.UserId.Value;
            _logger.LogInformation("[FEDERATED IDENTITY] Order para el User {UserId}, según el token de {Name}", order.UserId, caller.Name);
        }

        // External Configuration Store: el límite viene del almacén, y se lee en
        // cada solicitud. Sin un valor, no hay límite.
        if (_config.GetValue<int?>("Orders:MaxQuantity") is { } maxQuantity && order.Quantity > maxQuantity)
            return BadRequest(new { detail = $"La cantidad máxima por Order es {maxQuantity}" });

        // Idempotencia: POST no es idempotente, y cada llamada crea una Order. Si
        // el cliente no recibe el 202 -un timeout, un corte de red- no sabe si la
        // Order se creó, y si reintenta se crea una segunda, con su propio
        // order.created, su confirmación y otro pedido al ERP, con otro número:
        // ni el ERP puede darse cuenta de que es la misma.
        //
        // Con el encabezado Idempotency-Key, el cliente elige una clave por
        // Order y la repite en cada reintento:
        // - Clave nueva: se crea la Order y se guarda la clave.
        // - Clave conocida, con los mismos datos: no se crea nada; se responde
        //   lo mismo que la primera vez, con Idempotent-Replayed: true.
        // - Clave conocida, con otros datos: 422; el cliente usó la clave para
        //   otra Order.
        // Sin el encabezado, todo sigue como antes: cada llamada crea una Order.
        // Las claves no vencen en la demo; en producción se borran después de un
        // plazo, y un reintento posterior crearía otra Order.
        var idempotencyKey = Request.Headers[IdempotencyKeyHeader].FirstOrDefault();
        // La clave es del User del token, o del User 0 si la solicitud es anónima.
        var keyOwner = caller?.UserId ?? 0;
        string? requestHash = null;
        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            if (idempotencyKey.Length > 255)
                return BadRequest(new { detail = $"{IdempotencyKeyHeader} admite hasta 255 caracteres" });
            requestHash = RequestHash(order);
            var previous = await FindKeyAsync(idempotencyKey, keyOwner);
            if (previous is not null)
                return Replay(previous, requestHash);
        }

        order.Status = OrderStatus.Pending;

        try
        {
            await using var transaction = await _db.Database.BeginTransactionAsync();
            _db.Orders.Add(order);
            await _db.SaveChangesAsync();
            _db.OrderEvents.Add(OrderEvent.Create(order.Id, OrderEventTypes.OrderCreated,
                new OrderCreatedData(order.UserId, order.ProductId, order.Quantity)));
            _db.OutboxMessages.Add(OutboxMessage.Create(Events.OrderCreated,
                new OrderCreated(order.Id, order.UserId, order.ProductId, order.Quantity)));
            // La clave se guarda en la misma transacción que la Order: no puede
            // quedar una sin la otra.
            if (requestHash is not null)
                _db.IdempotencyKeys.Add(new IdempotencyKey
                {
                    Key = idempotencyKey!, UserId = keyOwner, RequestHash = requestHash, OrderId = order.Id
                });
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateException ex) when (requestHash is not null && OrdersDbContext.IsDuplicateKey(ex))
        {
            // Dos solicitudes con la misma clave a la vez: las dos pasaron la
            // búsqueda de arriba antes de que la otra guardara la clave. La
            // clave primaria de idempotency_keys deja guardar solo una; esta
            // espera a que la otra confirme, choca con la clave y se deshace,
            // Order incluida. Se responde como a un reintento.
            _db.ChangeTracker.Clear();
            return Replay((await FindKeyAsync(idempotencyKey!, keyOwner))!, requestHash);
        }

        Response.Headers.RetryAfter = "2";
        return Accepted($"/orders/{order.Id}/status", new { id = order.Id, status = order.Status, msg = "Order accepted" });
    }

    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string IdempotentReplayedHeader = "Idempotent-Replayed";

    private Task<IdempotencyKey?> FindKeyAsync(string key, int userId) =>
        _db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Key == key && k.UserId == userId);

    // SHA-256 de los datos de la Order ya leídos del cuerpo, y no del texto del
    // cuerpo: dos reintentos que difieren solo en espacios o en el orden de los
    // campos son la misma Order.
    private static string RequestHash(Order order) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{order.UserId}|{order.ProductId}|{order.Quantity}")));

    // La misma respuesta que la primera vez: el estado actual se consulta en
    // Location. Idempotent-Replayed no es parte del estándar: avisa al cliente
    // que no se creó nada.
    private ActionResult<object> Replay(IdempotencyKey previous, string requestHash)
    {
        if (previous.RequestHash != requestHash)
        {
            _logger.LogWarning("[IDEMPOTENCY] La clave {Key} ya se usó para la Order {OrderId}, con otros datos", previous.Key, previous.OrderId);
            return UnprocessableEntity(new { detail = $"La clave {previous.Key} ya se usó para otra Order" });
        }

        _logger.LogInformation("[IDEMPOTENCY] La clave {Key} ya creó la Order {OrderId}: no se crea otra", previous.Key, previous.OrderId);
        Response.Headers[IdempotentReplayedHeader] = "true";
        Response.Headers.RetryAfter = "2";
        return Accepted($"/orders/{previous.OrderId}/status", new { id = previous.OrderId, status = OrderStatus.Pending, msg = "Order accepted" });
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
        if (CallerIdentity.From(Request) is { } caller && !caller.CanAccess(order.UserId))
            return this.Forbidden(_logger, $"{caller.Name} no puede ver la Order {id}");

        if (order.Status != OrderStatus.Pending)
        {
            Response.Headers.Location = $"/orders/{id}";
            return StatusCode(StatusCodes.Status303SeeOther);
        }

        Response.Headers.RetryAfter = "2";
        return Ok(new { id = order.Id, status = order.Status });
    }

    // GET /orders
    // Federated Identity: un cliente ve solo sus Orders; un admin, o una
    // solicitud anónima, todas.
    [HttpGet]
    public async Task<ActionResult<List<object>>> ListOrders()
    {
        var caller = CallerIdentity.From(Request);
        var onlyUserId = caller is { IsAdmin: false } ? caller.UserId ?? -1 : (int?)null;

        var orders = _db.Orders.Where(o => onlyUserId == null || o.UserId == onlyUserId);

        // Anti-corruption Layer: un cambio en el modelo de Orders. Con el flag
        // prendido, cada Order trae la fecha de su evento OrderCreated, de la misma
        // base. La capa anticorrupción de Purchases lee solo los campos que usa:
        // la demo 22 muestra que Purchases responde igual.
        if (_config.GetValue<bool>("Features:OrderDates"))
            return Ok(await (
                from o in orders
                join e in _db.OrderEvents.Where(e => e.Type == OrderEventTypes.OrderCreated)
                    on o.Id equals e.OrderId into created
                from e in created.DefaultIfEmpty()
                select new
                {
                    id = o.Id,
                    user_id = o.UserId,
                    product_id = o.ProductId,
                    quantity = o.Quantity,
                    status = o.Status,
                    created_at = (DateTime?)e.OccurredAt
                })
                .ToListAsync());

        return Ok(await orders
            .Select(o => new
            {
                id = o.Id,
                user_id = o.UserId,
                product_id = o.ProductId,
                quantity = o.Quantity,
                status = o.Status
            })
            .ToListAsync());
    }

    // GET /orders/{id}
    [HttpGet("{id:int}")]
    public async Task<ActionResult<object>> GetOrder(int id)
    {
        var order = await _db.Orders.FindAsync(id);
        if (order is null)
            return NotFound();
        if (CallerIdentity.From(Request) is { } caller && !caller.CanAccess(order.UserId))
            return this.Forbidden(_logger, $"{caller.Name} no puede ver la Order {id}");

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
        if (CallerIdentity.From(Request) is { } caller && state is not null && !caller.CanAccess(state.UserId))
            return this.Forbidden(_logger, $"{caller.Name} no puede ver la Order {id}");
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
        // Federated Identity: un cliente ve solo sus Orders -ver CallerIdentity.cs-.
        if (CallerIdentity.From(Request) is { } caller && !caller.CanAccess(userId))
            return this.Forbidden(_logger, $"{caller.Name} no puede ver las Orders del User {userId}");

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

        // External Configuration Store: un feature flag. Con el flag prendido, el
        // detalle incluye el precio de cada Product.
        var showPrices = _config.GetValue<bool>("Features:ProductPrices");

        var result = new List<object>();
        // Una llamada a Products por Order, de a una. Cada llamada pasa por el
        // compartimento del Bulkhead de Products por separado: si está lleno,
        // esa Order queda con "no disponible [bulkhead lleno]" y se sigue con la
        // próxima, que vuelve a intentar conseguir un lugar.
        foreach (var order in orders)
        {
            var product = await Dependencies.GetAsync<ProductDto>(productsClient, $"{order.ProductId}", HttpContext.RequestAborted);
            var productName = product.Available ? product.Value?.Name : $"no disponible [{product.Failure}]";

            result.Add(showPrices
                ? (object)new
                {
                    order_id = order.Id,
                    customer_name = customerName,
                    product_name = productName,
                    quantity = order.Quantity,
                    status = order.Status,
                    unit_price = product.Value?.Price
                }
                : new
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
