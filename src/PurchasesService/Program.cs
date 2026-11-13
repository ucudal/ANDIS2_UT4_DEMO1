using Ucu.Andis.PurchasesService;
using Ucu.Andis.PurchasesService.AntiCorruption;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient<OrdersTranslator>(client =>
    client.BaseAddress = new Uri(builder.Configuration["OrdersService:BaseUrl"]
        ?? throw new InvalidOperationException("Falta la configuración OrdersService:BaseUrl")));
builder.Services.AddHttpClient<UsersTranslator>(client =>
    client.BaseAddress = new Uri(builder.Configuration["UsersService:BaseUrl"]
        ?? throw new InvalidOperationException("Falta la configuración UsersService:BaseUrl")));
builder.Services.AddScoped<IPurchaseSource, OrdersPurchaseSource>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

app.MapGet("/purchases/product/{productId}", async (int productId, IPurchaseSource purchases, CancellationToken cancellationToken) =>
    Results.Ok(await purchases.ForProductAsync(productId, cancellationToken)));

app.Run();

/// <summary>
/// Representa el servicio Purchases, que muestra, para un Product, todas sus
/// compras con su propio modelo -ver <c>Ucu.Andis.PurchasesService.Purchase</c>-.
/// </summary>
/// <remarks>
/// Anti-corruption Layer: los datos vienen de Orders y de Users, que tienen otro
/// modelo y otro protocolo. La capa anticorrupción, en la carpeta
/// <c>AntiCorruption</c>, los traduce: si Orders o Users cambian, se cambia la
/// capa, y Purchases no se entera.
/// </remarks>
partial class Program { }
