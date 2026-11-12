using Ucu.Andis.PurchasesService;
using Ucu.Andis.PurchasesService.AntiCorruption;

// Anti-corruption Layer: Purchases muestra, para un Product, todas sus compras con
// su propio modelo -ver Purchase.cs-. Los datos vienen de Orders y de Users, que
// tienen otro modelo y otro protocolo. La capa anticorrupción, en la carpeta
// AntiCorruption, los traduce: si Orders o Users cambian, se cambia la capa, y
// Purchases no se entera.
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
