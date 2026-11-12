using Microsoft.EntityFrameworkCore;
using ProductsService;
using ProductsService.Data;
using StackExchange.Redis;
using Ucu.Andis.Common;

var builder = WebApplication.CreateBuilder(args);

// External Configuration Store: la configuración central, en Consul -ver
// Common/ExternalConfiguration.cs-.
builder.AddExternalConfiguration();

builder.Services.AddDbContext<ProductsDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("ProductsDb")));

// Cache-Aside: Redis guarda los Products leídos de PostgreSQL -ver
// ProductCache.cs-. AbortOnConnectFail = false: Products arranca aunque Redis no
// esté, y se reconecta solo cuando vuelve. Los timeouts cortos hacen que, si
// Redis no responde, se pase a PostgreSQL enseguida en lugar de esperar.
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(new ConfigurationOptions
    {
        EndPoints = { builder.Configuration["Redis:Host"] ?? "localhost" },
        AbortOnConnectFail = false,
        ConnectTimeout = 1000,
        SyncTimeout = 500,
        AsyncTimeout = 500
    }));
builder.Services.AddSingleton<ProductCache>();

// Valet Key: las imágenes de los Products, en Azurite -ver ProductImages.cs-.
// Como Redis, Azurite no entra en /health: sin él, Products atiende todo lo
// demás.
builder.Services.AddSingleton<ProductImages>();

// Publisher/Subscriber: Products publica ProductUpdated en RabbitMQ.
builder.Services.AddSingleton<RabbitMq>();

// Saga: Products reserva el stock de cada Order y lo libera si la Order se
// rechaza o se cancela -ver StockReservations.cs-. Los eventos de la Saga pasan
// por su outbox -ver Common/OutboxRelay.cs-.
builder.Services.AddHostedService<StockReservations>();
builder.Services.AddHostedService<OutboxRelay<ProductsDbContext>>();
builder.Services.AddHostedService<StockMetrics>();

// Health Endpoint Monitoring: el servicio está sano si puede conectarse a su base.
// Ni Redis ni RabbitMQ entran: Products atiende igual sin ellos.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ProductsDbContext>("products-db");

// Métricas para Prometheus en /metrics -ver Common/Observability.cs-, más los
// aciertos de la caché, las imágenes, los mensajes del outbox y el stock.
builder.AddObservability(ProductCache.MeterName, ProductImages.MeterName, OutboxRelay.MeterName, StockMetrics.MeterName);

builder.Services.AddControllers();

var app = builder.Build();
app.Lifetime.ApplicationStarted.Register(ProductImages.StartMetrics);
app.UseChaos("/products");
app.MapMetrics();
app.MapHealthChecks("/health", new() { ResponseWriter = HealthResponse.WriteJson });
app.MapControllers();
app.Run();
