using System.Diagnostics.Metrics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Ucu.Andis.Common;

/// <summary>
/// Representa el publicador que convierte los health checks en métricas para
/// Grafana.
/// </summary>
/// <remarks>
/// Health Endpoint Monitoring en Grafana: Prometheus no puede leer
/// <c>/health</c>, solo métricas. ASP.NET Core ejecuta periódicamente los
/// mismos chequeos que responde <c>/health</c> y le pasa el resultado a este
/// publicador, que lo guarda y lo publica como métrica: 0 Unhealthy, 1
/// Degraded, 2 Healthy. Hay una medición con el estado general del servicio,
/// <c>check="total"</c>, y una por cada chequeo, con su nombre.
/// </remarks>
public sealed class HealthMetricsPublisher : IHealthCheckPublisher
{
    public const string MeterName = "Ucu.Andis.Health";

    private static readonly Meter Meter = new(MeterName);

    private HealthReport? _report;

    public HealthMetricsPublisher()
    {
        Meter.CreateObservableGauge("service.health", Measurements,
            description: "Salud del servicio y de cada chequeo: 0 Unhealthy, 1 Degraded, 2 Healthy");
    }

    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        _report = report;
        return Task.CompletedTask;
    }

    private IEnumerable<Measurement<int>> Measurements()
    {
        // Antes del primer chequeo no hay nada que publicar.
        if (_report is not { } report)
            yield break;

        yield return Measurement("total", report.Status);
        foreach (var (name, entry) in report.Entries)
            yield return Measurement(name, entry.Status);
    }

    private static Measurement<int> Measurement(string check, HealthStatus status) =>
        new((int)status, new KeyValuePair<string, object?>("check", check));
}
