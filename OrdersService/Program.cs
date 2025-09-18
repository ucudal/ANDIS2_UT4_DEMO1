using Microsoft.EntityFrameworkCore;
using Ucu.Andis.OrdersService.Data;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<OrdersDbContext>(options =>
    options.UseMySql(
        builder.Configuration.GetConnectionString("OrdersDb"),
        new MySqlServerVersion(new Version(9, 3, 0))
    )
);
builder.Services.AddHttpClient("UsersService", client =>
{
    var baseUrl = builder.Configuration["UsersService:BaseUrl"];
    client.BaseAddress = new Uri(baseUrl);
});
builder.Services.AddHttpClient("ProductsService", client =>
{
    var baseUrl = builder.Configuration["ProductsService:BaseUrl"];
    client.BaseAddress = new Uri(baseUrl);
});
builder.Services.AddControllers();

var app = builder.Build();
app.MapControllers();
app.Run();
