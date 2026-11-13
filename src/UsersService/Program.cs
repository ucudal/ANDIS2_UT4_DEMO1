using Ucu.Andis.Common;
using Ucu.Andis.Users;
using Ucu.Andis.Users.Data;
using Ucu.Andis.Users.Soap;
using MongoDB.Driver;
using OpenFeature;
using OpenFeature.Providers.Unleash;
using SoapCore;
using Unleash;
using Ucu.Andis.Users.Models;

var builder = WebApplication.CreateBuilder(args);

// MongoClient es thread-safe y mantiene su propio pool de conexiones: se crea
// una sola vez y se comparte, en lugar de crear uno por solicitud.
builder.Services.AddSingleton<IMongoCollection<User>>(_ =>
{
    var url = MongoUrl.Create(builder.Configuration.GetConnectionString("UsersDb"));
    var settings = MongoClientSettings.FromUrl(url);
    // Falla rápido si la base no está disponible, en lugar de esperar 30 segundos.
    settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
    return new MongoClient(settings).GetDatabase(url.DatabaseName).GetCollection<User>("users");
});

// Health Endpoint Monitoring: el servicio está sano si puede conectarse a su base.
builder.Services.AddHealthChecks()
    .AddCheck<MongoHealthCheck>("users-db", timeout: TimeSpan.FromSeconds(5));

// Métricas para Prometheus en /metrics -ver Common/Observability.cs-, más las
// respuestas por variante del experimento de la versión 2.
builder.AddObservability(Experiment.MeterName);

// Qué versión de Users es este contenedor -ver Release.cs-, y el experimento
// de A/B Testing que trae la versión 2 -ver Experiment.cs-. El experimento lee
// el encabezado X-Segment de la solicitud, por eso necesita el HttpContext.
builder.Services.AddSingleton<Release>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<Experiment>();

// La variante del experimento la decide Unleash, y Experiment se la pregunta a
// OpenFeature. Acá se elige el proveedor: es el único lugar que sabe de Unleash.
// Sin Unleash:Url, como en la versión 1, OpenFeature no tiene proveedor y
// devuelve siempre el valor por omisión.
var unleashUrl = builder.Configuration["Unleash:Url"];
if (!string.IsNullOrEmpty(unleashUrl))
{
    // Espera la primera lectura de los flags, o el primer error si Unleash no
    // está: en ese caso, el cliente sigue intentando en segundo plano.
    await Api.Instance.SetProviderAsync(new UnleashProvider(new UnleashSettings
    {
        AppName = "users-service",
        UnleashApi = new Uri(unleashUrl),
        CustomHttpHeaders = new() { ["Authorization"] = builder.Configuration["Unleash:Token"]! },
        // Cada cuánto vuelve a leer los flags. Por defecto son 30 segundos: en
        // la demo se acorta, para ver enseguida un cambio del reparto.
        FetchTogglesInterval = TimeSpan.FromSeconds(2),
    }));
}
builder.Services.AddSingleton<IFeatureClient>(Api.Instance.GetClient("users-service"));

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
