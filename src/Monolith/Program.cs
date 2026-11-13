using Microsoft.EntityFrameworkCore;
using SoapCore;
using Ucu.Andis.Monolith;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<MonolithDb>(options => options.UseSqlite("Data Source=/tmp/monolith.db"));
builder.Services.AddSoapCore();
builder.Services.AddScoped<IUserService, UserService>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<MonolithDb>();
    if (await db.Database.EnsureCreatedAsync())
    {
        db.Users.AddRange(new User { Id = "665f1c2ab4e8d9a0c1f2e301", Name = "alice", Email = "alice@example.com" },
                          new User { Id = "665f1c2ab4e8d9a0c1f2e302", Name = "bob", Email = "bob@example.com" });
        db.Products.AddRange(new Product { Id = 1, Name = "Camisa", Price = 1200.50 },
                             new Product { Id = 2, Name = "Pantalón", Price = 2300.00 },
                             new Product { Id = 3, Name = "Zapatos", Price = 3500.75 });
        db.Orders.AddRange(new Order { Id = 1, UserId = "665f1c2ab4e8d9a0c1f2e301", ProductId = 1, Quantity = 2 },
                           new Order { Id = 2, UserId = "665f1c2ab4e8d9a0c1f2e302", ProductId = 2, Quantity = 1 },
                           new Order { Id = 3, UserId = "665f1c2ab4e8d9a0c1f2e301", ProductId = 3, Quantity = 5 });
        await db.SaveChangesAsync();
    }
}

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

((IApplicationBuilder)app).UseSoapEndpoint<IUserService>("/users/UserService.svc", new SoapEncoderOptions(), SoapSerializer.DataContractSerializer);

app.MapGet("/products/", async (MonolithDb db) => await db.Products.ToListAsync());

app.MapGet("/products/{id:int}", async (int id, MonolithDb db) =>
    await db.Products.FindAsync(id) is { } product ? Results.Ok(product) : Results.NotFound(new { detail = "Product not found" }));

app.MapPut("/products/{id:int}", async (int id, Product changes, MonolithDb db) =>
{
    if (await db.Products.FindAsync(id) is not { } product)
        return Results.NotFound(new { detail = "Product not found" });
    product.Name = changes.Name;
    product.Price = changes.Price;
    await db.SaveChangesAsync();
    return Results.Ok(new { id, msg = "Product updated" });
});

app.MapGet("/orders/", async (MonolithDb db) => await db.Orders
    .Select(o => new { id = o.Id, user_id = o.UserId, product_id = o.ProductId, quantity = o.Quantity, status = o.Status })
    .ToListAsync());

// En el monolito, la Order se crea y se confirma en una sola transacción.
app.MapPost("/orders/", async (Order order, MonolithDb db) =>
{
    order.Status = "Confirmed";
    db.Orders.Add(order);
    await db.SaveChangesAsync();
    return Results.Accepted($"/orders/{order.Id}/status", new { id = order.Id, status = order.Status, msg = "Order accepted" });
});

// El detalle es un JOIN entre tres tablas de la misma base: es la comodidad que
// se pierde al separar los datos, y por la que Orders tiene que llamar a Users y
// a Products por HTTP.
app.MapGet("/orders/user/{userId}/details", async (string userId, MonolithDb db) =>
{
    var details = await (
        from o in db.Orders
        join u in db.Users on o.UserId equals u.Id
        join p in db.Products on o.ProductId equals p.Id
        where o.UserId == userId
        orderby o.Id
        select new { order_id = o.Id, customer_name = u.Name, product_name = p.Name, quantity = o.Quantity, status = o.Status })
        .ToListAsync();
    return details.Count == 0 ? Results.NotFound() : Results.Ok(details);
});

app.Run();

/// <summary>
/// Representa el monolito, el sistema que se quiere reemplazar con Strangler Fig.
/// </summary>
/// <remarks>
/// Un solo proceso y una sola base de datos con Users, Products y Orders, con los
/// mismos contratos que los microservicios, para que un cliente no note quién le
/// responde. No tiene patrones: es lo que la aplicación habría sido antes de
/// separarse en servicios.
///
/// La base es un archivo SQLite dentro del contenedor, con los mismos datos
/// iniciales que los microservicios, ids incluidos: al recrear el contenedor vuelve
/// a ese estado.
/// </remarks>
partial class Program { }
