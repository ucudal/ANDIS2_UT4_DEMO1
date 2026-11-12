using System.Net;
using System.Text.Json.Serialization;

// Backends for Frontends: el backend de la app móvil. La pantalla del teléfono es
// chica y la red puede ser lenta: la app muestra solo el Product y el estado de
// cada Order, y cuántas son. Este servicio arma esa respuesta con una sola
// consulta a Orders, el detalle por User. La web tiene su propio backend, en
// BffWeb. Lo mantiene el equipo de la app, y cambia al ritmo de la app.
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient("Orders", client =>
    client.BaseAddress = new Uri(builder.Configuration["OrdersService:BaseUrl"]
        ?? throw new InvalidOperationException("Falta la configuración OrdersService:BaseUrl")));

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.MapGet("/mobile/orders/user/{userId}", async (string userId, IHttpClientFactory clients, CancellationToken cancellationToken) =>
{
    using var response = await clients.CreateClient("Orders").GetAsync($"user/{userId}/details", cancellationToken);

    // Orders responde 404 Not Found si el User no tiene Orders: para la app es
    // una lista vacía.
    var details = response.StatusCode == HttpStatusCode.NotFound
        ? []
        : await response.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<List<DetailRecord>>(cancellationToken) ?? [];

    return Results.Ok(new
    {
        count = details.Count,
        orders = details.Select(d => new { product = d.ProductName, status = d.Status })
    });
});

app.Run();

record DetailRecord(
    [property: JsonPropertyName("product_name")] string ProductName,
    [property: JsonPropertyName("status")] string Status);
