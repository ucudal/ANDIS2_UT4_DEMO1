using Ucu.Andis.BffWeb;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("Orders", client =>
    client.BaseAddress = new Uri(builder.Configuration["OrdersService:BaseUrl"]
        ?? throw new InvalidOperationException("Falta la configuración OrdersService:BaseUrl")));
builder.Services.AddHttpClient("Products", client =>
    client.BaseAddress = new Uri(builder.Configuration["ProductsService:BaseUrl"]
        ?? throw new InvalidOperationException("Falta la configuración ProductsService:BaseUrl")));

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.MapGet("/web/orders/user/{userId}", async (string userId, IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    // Orders no tiene una consulta por User con el id de cada Product: se piden
    // todas y se filtran.
    var orders = (await clients.CreateClient("Orders").GetFromJsonAsync<List<OrderRecord>>("", cancellationToken) ?? [])
        .Where(o => o.UserId == userId)
        .ToList();

    // El nombre y el precio de cada Product, una sola vez por Product.
    var products = new Dictionary<int, ProductRecord>();
    foreach (var productId in orders.Select(o => o.ProductId).Distinct())
        products[productId] = await clients.CreateClient("Products").GetFromJsonAsync<ProductRecord>(productId.ToString(), cancellationToken)
            ?? new ProductRecord("desconocido", 0);

    var rows = orders.Select(o => new
    {
        order = o.Id,
        product = products[o.ProductId].Name,
        quantity = o.Quantity,
        unitPrice = products[o.ProductId].Price,
        total = o.Quantity * products[o.ProductId].Price,
        status = o.Status
    }).ToList();

    return Results.Ok(new { user = userId, orders = rows, total = rows.Sum(r => r.total) });
});

app.Run();

/// <summary>
/// Representa el backend de la web, de Backends for Frontends.
/// </summary>
/// <remarks>
/// La web muestra una tabla con cada Order de un User, con el precio de cada
/// Product y el total. Este servicio arma exactamente esa respuesta; la app móvil
/// tiene su propio backend, en BffMobile. Lo mantiene el equipo de la web, y
/// cambia al ritmo de la web.
/// </remarks>
partial class Program { }
