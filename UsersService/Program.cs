using Ucu.Andis.Users.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<UsersDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("UsersDb")));

builder.Services.AddControllers();

var app = builder.Build();

app.MapControllers();

app.Run();
