using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Ucu.Andis.Common;

// Métricas para Prometheus: cada servicio expone GET /metrics y Prometheus lo
// consulta periódicamente. No hace falta instrumentar el código a mano: ASP.NET
// Core, HttpClient y Polly ya publican sus métricas; solo hay que elegir cuáles
// exportar.
public static class ObservabilityExtensions
{
    private static readonly string[] BuiltInMeters =
    [
        // Duración y cantidad de solicitudes recibidas, por ruta y código HTTP.
        "Microsoft.AspNetCore.Hosting",
        // Solicitudes aceptadas y rechazadas por el rate limiter: Throttling.
        "Microsoft.AspNetCore.RateLimiting",
        // Duración de las llamadas a otros servicios.
        "System.Net.Http",
        // Eventos de los pipelines de resiliencia: reintentos, aperturas del
        // circuito, rechazos del bulkhead y timeouts.
        "Polly",
        ChaosExtensions.MeterName
    ];

    public static WebApplicationBuilder AddObservability(this WebApplicationBuilder builder, params string[] meters)
    {
        var serviceName = Environment.GetEnvironmentVariable("SERVICE_NAME") ?? builder.Environment.ApplicationName;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName))
            .WithMetrics(metrics => metrics
                .AddMeter(BuiltInMeters)
                .AddMeter(meters)
                .AddPrometheusExporter());

        return builder;
    }

    public static IEndpointRouteBuilder MapMetrics(this IEndpointRouteBuilder app)
    {
        app.MapPrometheusScrapingEndpoint("/metrics");
        return app;
    }
}
