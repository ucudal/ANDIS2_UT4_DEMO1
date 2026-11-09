using Ucu.Andis.Common;
using Ucu.Andis.Users;
using Ucu.Andis.Users.Data;
using Ucu.Andis.Users.Soap;
using Microsoft.EntityFrameworkCore;
using SoapCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<UsersDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("UsersDb")));

// Health Endpoint Monitoring: el servicio está sano si puede conectarse a su base.
builder.Services.AddHealthChecks()
    .AddDbContextCheck<UsersDbContext>("users-db");

// Métricas para Prometheus en /metrics -ver Common/Observability.cs-, más las
// respuestas por variante del experimento de la versión 2.
builder.AddObservability(Experiment.MeterName);

// Qué versión de Users es este contenedor -ver Release.cs-, y el experimento
// de A/B Testing que trae la versión 2 -ver Experiment.cs-. El experimento lee
// el encabezado X-Segment de la solicitud, por eso necesita el HttpContext.
builder.Services.AddSingleton<Release>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<Experiment>();

// Users expone SOAP en lugar de REST: SoapCore recibe los sobres XML, los
// despacha a UserService y publica el WSDL.
builder.Services.AddSoapCore();
builder.Services.AddScoped<IUserService, UserService>();

// Publisher/Subscriber: Users publica UserUpdated en RabbitMQ. RabbitMQ no
// entra en /health: Users atiende igual sin él, solo que no avisa los cambios.
builder.Services.AddSingleton<RabbitMq>();

var app = builder.Build();

// Primero, para que también las respuestas de /health y de la inyección de
// fallas digan qué versión respondió.
app.UseReleaseHeader();
// La inyección de fallas va antes que el endpoint SOAP, para que también lo afecte.
app.UseChaos("/users");
app.MapMetrics();
app.MapHealthChecks("/health", new() { ResponseWriter = HealthResponse.WriteJson });
// WebApplication es a la vez IApplicationBuilder e IEndpointRouteBuilder, y
// SoapCore tiene una sobrecarga para cada uno: se elige la de middleware.
((IApplicationBuilder)app).UseSoapEndpoint<IUserService>("/users/UserService.svc", new SoapEncoderOptions(), SoapSerializer.DataContractSerializer);

app.Run();
