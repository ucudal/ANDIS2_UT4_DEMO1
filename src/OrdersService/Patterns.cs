using System.Diagnostics.Metrics;
using System.Text.Json;
using Polly.CircuitBreaker;

namespace Ucu.Andis.OrdersService;

/// <summary>
/// Representa los ajustes que prenden y apagan cada patrón de resiliencia de Orders
/// en tiempo de ejecución, sin reiniciar, igual que el chaos.
/// </summary>
/// <remarks>
/// Sirve para que cada demo muestre un solo patrón: en la de Retry no actúa el
/// Circuit Breaker, en la de Throttling no actúa el Bulkhead, etc.
/// <code>
/// PUT    /orders/patterns  {"retry": true, "circuitBreaker": false, "bulkhead": false, "throttling": false}
/// GET    /orders/patterns
/// DELETE /orders/patterns  -vuelve a prender todos-
/// </code>
/// Cada proceso tiene sus propios ajustes: cambian los de <c>orders-service</c>,
/// que es el que arma la consulta de detalle, y no los de <c>orders-worker</c> ni
/// los de <c>orders-projector</c>.
/// </remarks>
public class PatternSettings
{
    public const string MeterName = "Ucu.Andis.Patterns";

    public bool Retry { get; set; } = true;
    public bool CircuitBreaker { get; set; } = true;
    public bool Bulkhead { get; set; } = true;
    public bool Throttling { get; set; } = true;

    private readonly List<CircuitBreakerManualControl> _circuits = [];
    private readonly Dictionary<string, CircuitBreakerStateProvider> _circuitStates = [];

    /// <summary>
    /// Registra el circuito de una dependencia, para poder cerrarlo al prender o
    /// apagar el patrón.
    /// </summary>
    /// <remarks>
    /// Al apagarlo, porque si quedó abierto por una demo anterior, no volvería a dejar
    /// pasar las llamadas. Al prenderlo, porque mientras estuvo apagado contó como
    /// exitosa cada llamada, y esas llamadas impedirían que se abra: cerrarlo borra lo
    /// contado.
    /// </remarks>
    public CircuitBreakerManualControl RegisterCircuit()
    {
        var control = new CircuitBreakerManualControl();
        lock (_circuits)
            _circuits.Add(control);
        return control;
    }

    /// <summary>
    /// Registra el estado del circuito de una dependencia, para mostrarlo en Grafana.
    /// </summary>
    public CircuitBreakerStateProvider RegisterCircuitState(string dependency)
    {
        var state = new CircuitBreakerStateProvider();
        lock (_circuitStates)
            _circuitStates[dependency] = state;
        return state;
    }

    public KeyValuePair<string, CircuitState>[] CircuitStates()
    {
        lock (_circuitStates)
            return [.. _circuitStates.Select(c => KeyValuePair.Create(c.Key, c.Value.CircuitState))];
    }

    public async Task ApplyAsync(PatternSettings settings)
    {
        var circuitBreakerChanged = CircuitBreaker != settings.CircuitBreaker;
        Retry = settings.Retry;
        CircuitBreaker = settings.CircuitBreaker;
        Bulkhead = settings.Bulkhead;
        Throttling = settings.Throttling;

        if (circuitBreakerChanged)
        {
            CircuitBreakerManualControl[] circuits;
            lock (_circuits)
                circuits = [.. _circuits];
            foreach (var circuit in circuits)
                await circuit.CloseAsync();
        }
    }
}

public static class PatternExtensions
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Meter Meter = new(PatternSettings.MeterName);

    public static IEndpointRouteBuilder MapPatterns(this IEndpointRouteBuilder app, string path)
    {
        var settings = app.ServiceProvider.GetRequiredService<PatternSettings>();
        var logger = app.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Patterns");

        // Qué patrones están prendidos, como métrica: 1 prendido, 0 apagado.
        // Así se ve en Grafana qué patrones actuaban en cada momento.
        Meter.CreateObservableGauge("orders.patterns.enabled", () => new[]
        {
            Measurement("retry", settings.Retry),
            Measurement("circuit breaker", settings.CircuitBreaker),
            Measurement("bulkhead", settings.Bulkhead),
            Measurement("throttling", settings.Throttling)
        }, description: "Patrones de resiliencia prendidos en Orders: 1 prendido, 0 apagado");

        // Estado del circuito de cada dependencia, como métrica: -1 con el
        // patrón apagado, 0 cerrado, 1 semiabierto, 2 abierto. Muestra cuánto
        // tiempo queda abierto el circuito, que los eventos de Polly no
        // muestran: solo marcan cuándo se abre y cuándo se cierra.
        Meter.CreateObservableGauge("orders.circuit.state", () => settings.CircuitStates()
            .Select(c => new Measurement<int>(settings.CircuitBreaker ? StateValue(c.Value) : -1,
                new KeyValuePair<string, object?>("dependency", c.Key))),
            description: "Estado del circuito de cada dependencia: -1 apagado, 0 cerrado, 1 semiabierto, 2 abierto");

        app.MapGet(path, () => settings);
        app.MapPut(path, async (HttpRequest request) =>
        {
            var newSettings = await JsonSerializer.DeserializeAsync<PatternSettings>(request.Body, JsonOptions)
                ?? new PatternSettings();
            await settings.ApplyAsync(newSettings);
            Log(logger, settings);
            return settings;
        });
        app.MapDelete(path, async () =>
        {
            await settings.ApplyAsync(new PatternSettings());
            Log(logger, settings);
            return settings;
        });

        return app;
    }

    private static int StateValue(CircuitState state) => state switch
    {
        CircuitState.Closed => 0,
        CircuitState.HalfOpen => 1,
        _ => 2
    };

    private static Measurement<int> Measurement(string pattern, bool enabled) =>
        new(enabled ? 1 : 0, new KeyValuePair<string, object?>("pattern", pattern));

    private static void Log(ILogger logger, PatternSettings s) =>
        logger.LogWarning("[PATTERNS] retry={Retry}, circuitBreaker={CircuitBreaker}, bulkhead={Bulkhead}, throttling={Throttling}",
            s.Retry, s.CircuitBreaker, s.Bulkhead, s.Throttling);
}
