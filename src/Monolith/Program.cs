using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.Serialization;
using System.ServiceModel;
using Microsoft.EntityFrameworkCore;
using SoapCore;

// Strangler Fig: el sistema que se quiere reemplazar. Un solo proceso y una sola
// base de datos con Users, Products y Orders, con los mismos contratos que los
// microservicios, para que un cliente no note quién le responde. No tiene
// patrones: es lo que la aplicación habría sido antes de separarse en servicios.
//
// La base es un archivo SQLite dentro del contenedor, con los mismos datos
// iniciales que los microservicios: al recrear el contenedor vuelve a ese estado.
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
        db.Users.AddRange(new User { Id = 1, Name = "alice", Email = "alice@example.com" },
                          new User { Id = 2, Name = "bob", Email = "bob@example.com" });
        db.Products.AddRange(new Product { Id = "60f7c2b5e1d3c2a1b8e4d123", Name = "Camisa", Price = 1200.50 },
                             new Product { Id = "60f7c2b5e1d3c2a1b8e4d124", Name = "Pantalón", Price = 2300.00 },
                             new Product { Id = "60f7c2b5e1d3c2a1b8e4d125", Name = "Zapatos", Price = 3500.75 });
        db.Orders.AddRange(new Order { Id = 1, UserId = 1, ProductId = "60f7c2b5e1d3c2a1b8e4d123", Quantity = 2 },
                           new Order { Id = 2, UserId = 2, ProductId = "60f7c2b5e1d3c2a1b8e4d124", Quantity = 1 },
                           new Order { Id = 3, UserId = 1, ProductId = "60f7c2b5e1d3c2a1b8e4d125", Quantity = 5 });
        await db.SaveChangesAsync();
    }
}

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

((IApplicationBuilder)app).UseSoapEndpoint<IUserService>("/users/UserService.svc", new SoapEncoderOptions(), SoapSerializer.DataContractSerializer);

app.MapGet("/products/", async (MonolithDb db) => await db.Products.ToListAsync());

app.MapGet("/products/{id}", async (string id, MonolithDb db) =>
    await db.Products.FindAsync(id) is { } product ? Results.Ok(product) : Results.NotFound(new { detail = "Product not found" }));

app.MapPut("/products/{id}", async (string id, Product changes, MonolithDb db) =>
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
app.MapGet("/orders/user/{userId:int}/details", async (int userId, MonolithDb db) =>
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

public class MonolithDb : DbContext
{
    public MonolithDb(DbContextOptions<MonolithDb> options) : base(options) { }
    public DbSet<User> Users => Set<User>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Order> Orders => Set<Order>();
}

[DataContract(Namespace = "http://ucu.edu.uy/andis/users")]
public class User
{
    [DataMember] public int Id { get; set; }
    [DataMember] public string Name { get; set; } = string.Empty;
    [DataMember] public string Email { get; set; } = string.Empty;
}

public class Product
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public double Price { get; set; }
}

public class Order
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string? ProductId { get; set; }
    public int Quantity { get; set; }
    public string Status { get; set; } = "Confirmed";
}

// El mismo contrato SOAP que Users.
[ServiceContract(Namespace = "http://ucu.edu.uy/andis/users")]
public interface IUserService
{
    [OperationContract] Task<User?> GetUser(int id);
    [OperationContract] Task<User[]> ListUsers();
    [OperationContract] Task<User> UpdateUser(int id, string name, string email);
}

public class UserService : IUserService
{
    private readonly MonolithDb _db;

    public UserService(MonolithDb db) => _db = db;

    public async Task<User?> GetUser(int id) => await _db.Users.FindAsync(id);

    public Task<User[]> ListUsers() => _db.Users.ToArrayAsync();

    public async Task<User> UpdateUser(int id, string name, string email)
    {
        var user = await _db.Users.FindAsync(id) ?? throw new FaultException($"No existe el User {id}");
        user.Name = name;
        user.Email = email;
        await _db.SaveChangesAsync();
        return user;
    }
}
