using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ucu.Andis.Common;

// Inyección de fallas para la demo: permite que un servicio falle o responda
// lento a propósito, para ver cómo reaccionan los patrones de resiliencia de
// quien lo llama. Se configura en tiempo de ejecución, sin reiniciar:
//
//   PUT    {prefijo}/chaos  {"failureRate": 0.5, "delayMs": 1000}
//   GET    {prefijo}/chaos
//   DELETE {prefijo}/chaos  -vuelve a la normalidad-
//
// Los valores iniciales salen de la configuración, Chaos:FailureRate y
// Chaos:DelayMs -por variable de entorno, CHAOS__FAILURERATE y CHAOS__DELAYMS-.
// Sin valor, son 0: el servicio arranca sin fallas. Las demos de Kubernetes, de
// la 32 a la 34, construyen con ellos una versión de Users que falla desde que
// arranca, para ver cómo se detecta y se vuelve atrás.
public class ChaosSettings
{
    // Fracción de solicitudes que responden 503, entre 0 y 1.
    public double FailureRate { get; set; }

    // Demora agregada a cada solicitud, en milisegundos.
    public int DelayMs { get; set; }
}

public static class ChaosExtensions
{
    public const string MeterName = "Ucu.Andis.Chaos";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Meter Meter = new(MeterName);

    public static IApplicationBuilder UseChaos(this IApplicationBuilder app, string prefix)
    {
        var configuration = app.ApplicationServices.GetRequiredService<IConfiguration>();
        var settings = new ChaosSettings
        {
            FailureRate = Math.Clamp(configuration.GetValue("Chaos:FailureRate", 0.0), 0, 1),
            DelayMs = Math.Max(configuration.GetValue("Chaos:DelayMs", 0), 0),
        };
        var chaosPath = $"{prefix}/chaos";
        var logger = app.ApplicationServices.GetRequiredService<ILoggerFactory>().CreateLogger("Chaos");

        if (settings.FailureRate > 0 || settings.DelayMs > 0)
            logger.LogWarning("Chaos desde el arranque: failureRate={FailureRate}, delayMs={DelayMs}",
                settings.FailureRate, settings.DelayMs);

        // La falla inyectada también se publica como métrica, para ver en
        // Grafana en qué momento empezó y cuándo terminó.
        Meter.CreateObservableGauge("chaos.failure_rate", () => settings.FailureRate,
            description: "Fracción de solicitudes que fallan a propósito");
        Meter.CreateObservableGauge("chaos.delay", () => settings.DelayMs / 1000.0, unit: "s",
            description: "Demora agregada a propósito a cada solicitud");

        return app.Use(async (context, next) =>
        {
            var path = context.Request.Path;

            if (path.Equals(chaosPath, StringComparison.OrdinalIgnoreCase))
            {
                if (HttpMethods.IsPut(context.Request.Method))
                {
                    var newSettings = await JsonSerializer.DeserializeAsync<ChaosSettings>(context.Request.Body, JsonOptions)
                        ?? new ChaosSettings();
                    settings.FailureRate = Math.Clamp(newSettings.FailureRate, 0, 1);
                    settings.DelayMs = Math.Max(newSettings.DelayMs, 0);
                }
                else if (HttpMethods.IsDelete(context.Request.Method))
                {
                    settings.FailureRate = 0;
                    settings.DelayMs = 0;
                }

                logger.LogWarning("Chaos: failureRate={FailureRate}, delayMs={DelayMs}", settings.FailureRate, settings.DelayMs);
                await context.Response.WriteAsJsonAsync(settings);
                return;
            }

            if (path.StartsWithSegments(prefix))
            {
                if (settings.DelayMs > 0)
                    await Task.Delay(settings.DelayMs, context.RequestAborted);

                if (Random.Shared.NextDouble() < settings.FailureRate)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { detail = "Falla simulada [chaos]" });
                    return;
                }
            }

            await next();
        });
    }
}
