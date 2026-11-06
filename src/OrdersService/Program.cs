using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Ucu.Andis.Common;
using Ucu.Andis.OrdersService;
using Ucu.Andis.OrdersService.Data;
using Ucu.Andis.OrdersService.Messaging;

var builder = WebApplication.CreateBuilder(args);

// External Configuration Store: la configuración central, en Consul -ver
// Common/ExternalConfiguration.cs-. Va primero, para que todo lo que sigue la vea.
builder.AddExternalConfiguration();

// Qué patrones de resiliencia están prendidos -ver Patterns.cs-. Los usan los
// pipelines de Resilience.cs y la política de throttling.
builder.Services.AddSingleton<PatternSettings>();
builder.Services.AddDbContext<OrdersDbContext>(options =>
    options.UseMySQL(
        builder.Configuration.GetConnectionString("OrdersDb")
            ?? throw new InvalidOperationException("Falta la cadena de conexión OrdersDb")
    )
);

// Circuit Breaker, Retry y Bulkhead: cada dependencia tiene su propio pipeline
// de resiliencia -ver Resilience.cs-. Como cada HttpClient con nombre tiene su
// pipeline, también tiene su propio compartimento del Bulkhead: las llamadas
// lentas a Products llenan el de Products, pero no ocupan lugares del de Users.
// El compartimento se dimensiona por dependencia: Users es rápido y se llama en
// cada consulta, así que tiene más lugar; Products usa el tamaño por defecto -5
// en curso y 5 en espera-.
//
// External Configuration Store: esta función corre cada vez que se crea un
// HttpClient, y lee la URL de la configuración. Si la URL cambia en el almacén,
// el próximo HttpClient ya usa la nueva.
builder.Services.AddHttpClient("UsersService", client =>
{
    var baseUrl = builder.Configuration["UsersService:BaseUrl"]
        ?? throw new InvalidOperationException("Falta la configuración UsersService:BaseUrl");
    client.BaseAddress = new Uri(baseUrl);
}).AddResilience("users", bulkheadPermitLimit: 20);
builder.Services.AddHttpClient("ProductsService", client =>
{
    var baseUrl = builder.Configuration["ProductsService:BaseUrl"]
        ?? throw new InvalidOperationException("Falta la configuración ProductsService:BaseUrl");
    client.BaseAddress = new Uri(baseUrl);
}).AddResilience("products");

// Throttling: el servicio se protege a sí mismo limitando cuántas consultas de
// detalle atiende a la vez. Las que exceden el límite reciben 503 con
// Retry-After, en lugar de degradar el servicio para todos.
//
// Se usa el middleware de rate limiting de ASP.NET Core. Como nginx, ASP.NET
// Core lo llama "rate limiting", pero en el catálogo de patrones de Azure esto
// es Throttling: lo aplica el servicio sobre lo que recibe -ver PATTERNS.md-.
//
// La política "throttling" es un ConcurrencyLimiter, el mismo tipo de limitador
// del Bulkhead -ver Resilience.cs-, pero en la dirección opuesta: el Bulkhead
// limita las llamadas que Orders hace a una dependencia; el Throttling, las
// solicitudes que Orders recibe. Funciona como un semáforo de 20 lugares sin
// fila: si hay un lugar libre, la solicitud lo toma y lo libera cuando termina
// la respuesta; si no, se rechaza de inmediato, sin llegar al controlador.
// QueueLimit = 0 porque encolar solo agregaría demora a un servicio que ya está
// saturado; es mejor que el cliente reintente más tarde, cuando le indica
// Retry-After.
//
// El límite se aplica solo a los endpoints marcados con
// [EnableRateLimiting("throttling")] -GetOrdersForUser, en OrdersController.cs-,
// y es para todo el servicio, sin importar quién pide. El Throttling por
// cliente está en el gateway -ver ApiGateway/nginxconf/nginx.conf-.
builder.Services.AddRateLimiter(options =>
{
    // 503 y no 429: el problema no es que este cliente pida demasiado, sino que
    // el servicio está saturado.
    options.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
    //
    // Con el Throttling apagado -ver Patterns.cs-, la política no limita.
    options.AddPolicy("throttling", context =>
        context.RequestServices.GetRequiredService<PatternSettings>().Throttling
            ? RateLimitPartition.GetConcurrencyLimiter("throttling", _ => new ConcurrencyLimiterOptions
            {
                PermitLimit = 20,
                QueueLimit = 0
            })
            : RateLimitPartition.GetNoLimiter("sin throttling"));
    options.OnRejected = (context, _) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "2";
        context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Throttling")
            .LogWarning("[THROTTLING] Demasiadas consultas simultáneas; se responde 503 a {Path}", context.HttpContext.Request.Path);
        return ValueTask.CompletedTask;
    };
});

// La misma imagen cumple tres papeles, según la configuración:
// - orders-service, con Outbox:Enabled: la API y el relay del outbox, que
//   publica en RabbitMQ los eventos que la API y el worker dejan en la base.
// - orders-worker, con Worker:Enabled: Competing Consumers, las réplicas
//   consumen la cola orders.process.
// - orders-projector, con Projector:Enabled: mantiene la vista materializada.
builder.Services.AddSingleton<RabbitMq>();
var outboxEnabled = builder.Configuration.GetValue<bool>("Outbox:Enabled");
if (outboxEnabled)
    builder.Services.AddHostedService<OutboxRelay>();
if (builder.Configuration.GetValue<bool>("Worker:Enabled"))
    builder.Services.AddHostedService<OrderProcessingWorker>();
if (builder.Configuration.GetValue<bool>("Projector:Enabled"))
    builder.Services.AddHostedService<OrderDetailsProjector>();

// Health Endpoint Monitoring: el servicio está sano si llega a su base y a
// RabbitMQ. No se chequea a Usuarios ni a Productos: si se cae una dependency,
// Órdenes sigue funcionando degradado y no debe marcarse como caído.
//
// Con el outbox, la API ya no necesita a RabbitMQ para aceptar órdenes: si no
// está, el servicio queda Degraded -responde 200- en lugar de Unhealthy. El
// worker y el proyector sí lo necesitan, porque consumen de RabbitMQ.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrdersDbContext>("orders-db")
    .AddCheck<RabbitMqHealthCheck>("rabbitmq",
        failureStatus: outboxEnabled ? HealthStatus.Degraded : HealthStatus.Unhealthy,
        timeout: TimeSpan.FromSeconds(5));

// Métricas para Prometheus en /metrics -ver Common/Observability.cs-, más las
// órdenes que procesa cada réplica del worker y los mensajes del outbox.
builder.AddObservability(OrderProcessingWorker.MeterName, OutboxRelay.MeterName, PatternSettings.MeterName);

builder.Services.AddControllers();

var app = builder.Build();
// Throttling: el middleware que aplica las políticas de AddRateLimiter. Va antes
// de los controladores para que una solicitud rechazada no llegue a ejecutarse.
app.UseRateLimiter();
// Después del rate limiter: la demora simulada ocurre mientras la solicitud
// ocupa un lugar del throttling.
app.UseChaos("/orders");
app.MapMetrics();
app.MapPatterns("/orders/patterns");
app.MapHealthChecks("/health", new() { ResponseWriter = HealthResponse.WriteJson });
app.MapControllers();
app.Run();
