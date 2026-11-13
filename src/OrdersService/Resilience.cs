using System.Threading.RateLimiting;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Ucu.Andis.OrdersService;

/// <summary>
/// Proporciona el pipeline de resiliencia para las llamadas HTTP de Orders a
/// Users y a Products.
/// </summary>
/// <remarks>
/// Cada dependencia tiene su propio pipeline, y por lo tanto su propio bulkhead
/// y su propio circuit breaker: si Products falla, el circuito de Users sigue
/// cerrado.
///
/// Las estrategias se aplican de afuera hacia adentro, en este orden:
/// <code>
/// Bulkhead -> Timeout total -> Retry -> Circuit Breaker -> Timeout por intento -> HTTP
/// </code>
/// El Bulkhead, el Retry y el Circuit Breaker se pueden apagar en tiempo de
/// ejecución -ver <c>PatternSettings</c>-: apagados, dejan pasar las
/// llamadas sin hacer nada.
/// </remarks>
public static class Resilience
{
    /// <summary>
    /// Cantidad de llamadas simultáneas que el Bulkhead permite por defecto a
    /// cada dependencia.
    /// </summary>
    /// <remarks>
    /// Es el tamaño del compartimento. Cada dependencia puede tener el suyo:
    /// Users usa 20 -ver <c>Program.cs</c>-.
    /// </remarks>
    public const int DefaultBulkheadPermitLimit = 5;

    /// <summary>
    /// Cantidad de llamadas que pueden esperar un lugar en el Bulkhead, por
    /// defecto.
    /// </summary>
    /// <remarks>
    /// Con los lugares y la espera llenos, la llamada se rechaza de inmediato,
    /// sin llegar a la red.
    /// </remarks>
    public const int DefaultBulkheadQueueLimit = 5;

    /// <summary>
    /// Cantidad máxima de reintentos del Retry, además del primer intento.
    /// </summary>
    public const int MaxRetries = 3;

    /// <summary>
    /// Demora antes del primer reintento del Retry.
    /// </summary>
    /// <remarks>
    /// El retroceso es exponencial, con jitter: 0,5 segundos, 1 segundo, 2
    /// segundos.
    /// </remarks>
    public static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Fracción de llamadas fallidas que abre el Circuit Breaker.
    /// </summary>
    /// <remarks>
    /// Con 0,5, el circuito se abre si fallan al menos la mitad de las llamadas
    /// en <c>Resilience.CircuitSamplingDuration</c>.
    /// </remarks>
    public const double CircuitFailureRatio = 0.5;

    /// <summary>
    /// Cantidad mínima de llamadas en <c>Resilience.CircuitSamplingDuration</c>
    /// para que el Circuit Breaker evalúe si abre el circuito.
    /// </summary>
    /// <remarks>
    /// Con menos llamadas, unas pocas fallas no alcanzan para abrirlo.
    /// </remarks>
    public const int CircuitMinimumThroughput = 5;

    /// <summary>
    /// Ventana de tiempo en la que el Circuit Breaker cuenta las llamadas y las
    /// fallas.
    /// </summary>
    public static readonly TimeSpan CircuitSamplingDuration = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Tiempo que el Circuit Breaker deja el circuito abierto.
    /// </summary>
    /// <remarks>
    /// Mientras está abierto, las llamadas fallan de inmediato. Al vencer, el
    /// circuito queda semiabierto y deja pasar una llamada de prueba: si
    /// funciona, se cierra; si falla, vuelve a abrirse.
    /// </remarks>
    public static readonly TimeSpan CircuitBreakDuration = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Tiempo máximo de cada intento de llamada.
    /// </summary>
    /// <remarks>
    /// Es la estrategia más interna del pipeline: un intento que vence cuenta como
    /// una falla para el Retry, que lo reintenta, y para el Circuit Breaker.
    /// </remarks>
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Tiempo máximo de una llamada completa, reintentos incluidos.
    /// </summary>
    /// <remarks>
    /// Limita cuánto puede tardar el Retry en total. Empieza a contar recién
    /// cuando la llamada consigue su lugar en el Bulkhead, no mientras espera.
    /// </remarks>
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Agrega el pipeline de resiliencia a las llamadas a una dependencia.
    /// </summary>
    /// <param name="builder">El cliente HTTP de la dependencia.</param>
    /// <param name="dependency">El nombre de la dependencia, para los logs y
    /// las métricas del circuito.</param>
    /// <param name="bulkheadPermitLimit">Las llamadas simultáneas que permite
    /// el Bulkhead de esta dependencia.</param>
    /// <param name="bulkheadQueueLimit">Las llamadas que pueden esperar en el
    /// Bulkhead de esta dependencia.</param>
    /// <returns>El mismo <paramref name="builder"/>, para encadenar
    /// llamadas.</returns>
    public static IHttpClientBuilder AddResilience(this IHttpClientBuilder builder, string dependency,
        int bulkheadPermitLimit = DefaultBulkheadPermitLimit, int bulkheadQueueLimit = DefaultBulkheadQueueLimit)
    {
        builder.AddResilienceHandler($"resilience-{dependency}", (pipeline, context) =>
        {
            var logger = context.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger($"Resilience.{dependency}");
            var patterns = context.ServiceProvider.GetRequiredService<PatternSettings>();

            // Bulkhead: un ConcurrencyLimiter, que funciona como un semáforo
            // con fila. Si hay un lugar libre entre los PermitLimit, la llamada
            // lo toma y lo libera al terminar. Si no hay lugar pero queda
            // espacio entre los QueueLimit, espera y entra cuando se libera
            // uno, en orden de llegada. Si los lugares y la fila están llenos,
            // se rechaza de inmediato con RateLimiterRejectedException, sin
            // llegar a la red; Dependencies.GetAsync la convierte en "bulkhead
            // lleno".
            //
            // Va primero en el pipeline para que un lugar cubra la llamada
            // completa, reintentos incluidos: una llamada que reintenta no
            // ocupa más de un lugar. Por lo mismo, el timeout total empieza a
            // contar recién cuando la llamada consigue su lugar, no mientras
            // espera.
            //
            // Con el Bulkhead apagado, toda llamada recibe lugar sin pasar por
            // el limitador.
            var bulkhead = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
            {
                PermitLimit = bulkheadPermitLimit,
                QueueLimit = bulkheadQueueLimit
            });
            pipeline.AddRateLimiter(new HttpRateLimiterStrategyOptions
            {
                RateLimiter = args => patterns.Bulkhead
                    ? bulkhead.AcquireAsync(1, args.Context.CancellationToken)
                    : ValueTask.FromResult<RateLimitLease>(AcquiredLease.Instance),
                OnRejected = _ =>
                {
                    logger.LogWarning("[BULKHEAD] {Dependency}: compartimento lleno [{PermitLimit} en curso, {QueueLimit} en espera]; se rechaza la llamada",
                        dependency, bulkheadPermitLimit, bulkheadQueueLimit);
                    return default;
                }
            });

            pipeline.AddTimeout(TotalTimeout);

            // Con el Retry apagado, ninguna falla se reintenta.
            var retry = new HttpRetryStrategyOptions();
            var shouldRetry = retry.ShouldHandle;
            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                ShouldHandle = args => patterns.Retry ? shouldRetry(args) : PredicateResult.False(),
                MaxRetryAttempts = MaxRetries,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = RetryBaseDelay,
                OnRetry = args =>
                {
                    logger.LogWarning("[RETRY] {Dependency}: reintento {Attempt} de {MaxAttempts} en {DelayMs}ms [causa: {Cause}]",
                        dependency, args.AttemptNumber + 1, MaxRetries, (int)args.RetryDelay.TotalMilliseconds, Cause(args.Outcome));
                    return default;
                }
            });

            // Con el Circuit Breaker apagado, ninguna falla cuenta para abrir el
            // circuito; al apagarlo, además, se cierra si estaba abierto.
            var circuitBreaker = new HttpCircuitBreakerStrategyOptions();
            var shouldBreak = circuitBreaker.ShouldHandle;
            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                ShouldHandle = args => patterns.CircuitBreaker ? shouldBreak(args) : PredicateResult.False(),
                ManualControl = patterns.RegisterCircuit(),
                StateProvider = patterns.RegisterCircuitState(dependency),
                FailureRatio = CircuitFailureRatio,
                MinimumThroughput = CircuitMinimumThroughput,
                SamplingDuration = CircuitSamplingDuration,
                BreakDuration = CircuitBreakDuration,
                OnOpened = args =>
                {
                    logger.LogError("[CIRCUIT BREAKER] {Dependency}: circuito ABIERTO por {Seconds}s [causa: {Cause}]; las llamadas fallan de inmediato",
                        dependency, (int)args.BreakDuration.TotalSeconds, Cause(args.Outcome));
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    logger.LogWarning("[CIRCUIT BREAKER] {Dependency}: circuito SEMIABIERTO; se deja pasar una llamada de prueba", dependency);
                    return default;
                },
                OnClosed = _ =>
                {
                    logger.LogInformation("[CIRCUIT BREAKER] {Dependency}: circuito CERRADO; la dependencia se recuperó", dependency);
                    return default;
                }
            });

            pipeline.AddTimeout(AttemptTimeout);
        });

        return builder;
    }

    private static string Cause(Outcome<HttpResponseMessage> outcome) =>
        outcome.Exception?.GetType().Name ?? $"HTTP {(int?)outcome.Result?.StatusCode}";

    // El lugar que recibe una llamada cuando el Bulkhead está apagado.
    private sealed class AcquiredLease : RateLimitLease
    {
        public static readonly AcquiredLease Instance = new();

        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
