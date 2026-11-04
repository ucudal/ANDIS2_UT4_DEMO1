using MongoDB.Driver;
using ProductsService;
using StackExchange.Redis;
using ProductsService.Models;
using Ucu.Andis.Common;

var builder = WebApplication.CreateBuilder(args);

// MongoClient es thread-safe y mantiene su propio pool de conexiones: se crea
// una sola vez y se comparte, en lugar de crear uno por request.
builder.Services.AddSingleton<IMongoCollection<Product>>(_ =>
{
    var config = builder.Configuration;
    var host = config["MONGO_HOST"] ?? "localhost";
    var port = config["MONGO_PORT"] ?? "27017";
    var db = config["MONGO_DB"] ?? "productsdb";
    var user = config["MONGO_USER"] ?? "productsusr";
    var password = config["MONGO_PASSWORD"] ?? "Pa55w0rd";

    var settings = MongoClientSettings.FromConnectionString($"mongodb://{user}:{password}@{host}:{port}/{db}");
    // Falla rápido si la base no está disponible, en lugar de esperar 30 segundos.
    settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
    var client = new MongoClient(settings);
    return client.GetDatabase(db).GetCollection<Product>("products");
});

// Cache-Aside: Redis guarda los Products leídos de MongoDB -ver ProductCache.cs-.
// AbortOnConnectFail = false: Products arranca aunque Redis no esté, y se
// reconecta solo cuando vuelve. Los timeouts cortos hacen que, si Redis no
// responde, se pase a MongoDB enseguida en lugar de esperar.
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

// Health Endpoint Monitoring: el servicio está sano si puede conectarse a su base.
// Ni Redis ni RabbitMQ entran: Products atiende igual sin ellos.
builder.Services.AddHealthChecks()
    .AddCheck<MongoHealthCheck>("products-db", timeout: TimeSpan.FromSeconds(5));

// Métricas para Prometheus en /metrics -ver Common/Observability.cs-, más los
// aciertos de la caché y las imágenes.
builder.AddObservability(ProductCache.MeterName, ProductImages.MeterName);

builder.Services.AddControllers();

var app = builder.Build();
app.Lifetime.ApplicationStarted.Register(ProductImages.StartMetrics);
app.UseChaos("/products");
app.MapMetrics();
app.MapHealthChecks("/health", new() { ResponseWriter = HealthResponse.WriteJson });
app.MapControllers();
app.Run();
