using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ucu.Andis.Common;

public static class HealthResponse
{
    // Escribe el resultado de los health checks como JSON, con el estado de
    // cada chequeo, en lugar del texto plano "Healthy"/"Unhealthy" por defecto.
    public static Task WriteJson(HttpContext context, HealthReport report) =>
        context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            service = Environment.GetEnvironmentVariable("SERVICE_NAME") ?? Environment.MachineName,
            checks = report.Entries.ToDictionary(
                e => e.Key,
                e => new
                {
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description ?? e.Value.Exception?.Message,
                    duration_ms = (int)e.Value.Duration.TotalMilliseconds
                })
        });
}
