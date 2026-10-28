using Ucu.Andis.Common;
using Ucu.Andis.Users.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<UsersDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("UsersDb")));

// Health Endpoint Monitoring: el servicio está sano si puede conectarse a su base.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<UsersDbContext>("users-db");

// Métricas para Prometheus en /metrics -ver Common/Observability.cs-.
builder.AddObservability();

builder.Services.AddControllers();

var app = builder.Build();

app.UseChaos("/users");
app.MapMetrics();
app.MapHealthChecks("/health", new() { ResponseWriter = HealthResponse.WriteJson });
app.MapControllers();

app.Run();
