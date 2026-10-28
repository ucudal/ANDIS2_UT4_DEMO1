using System.Threading.RateLimiting;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Ucu.Andis.OrdersService;

// Pipeline de resiliencia para las llamadas HTTP de Orders a Users y a
// Products. Cada dependencia tiene su propio pipeline, y por lo tanto su propio
// bulkhead y su propio circuit breaker: si Products falla, el circuito de
// Users sigue cerrado.
//
// Las estrategias se aplican de afuera hacia adentro, en este orden:
//
//   Bulkhead -> Timeout total -> Retry -> Circuit Breaker -> Timeout por intento -> HTTP
public static class Resilience
{
    // Bulkhead: llamadas simultáneas permitidas y llamadas en espera, por
    // defecto. Cada dependencia puede tener su propio tamaño de compartimento.
    public const int DefaultBulkheadPermitLimit = 5;
    public const int DefaultBulkheadQueueLimit = 5;

    // Retry: reintentos con retroceso exponencial: 0,5 s, 1 s, 2 s, con jitter.
    public const int MaxRetries = 3;
    public static readonly TimeSpan RetryBaseDelay = TimeSpan.FromMilliseconds(500);

    // Circuit Breaker: se abre si fallan al menos la mitad de las llamadas en
    // 10 segundos, con un mínimo de 5 llamadas, y queda abierto 15 segundos.
    public const double CircuitFailureRatio = 0.5;
    public const int CircuitMinimumThroughput = 5;
    public static readonly TimeSpan CircuitSamplingDuration = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CircuitBreakDuration = TimeSpan.FromSeconds(15);

    // Timeouts.
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(10);

    public static IHttpClientBuilder AddResilience(this IHttpClientBuilder builder, string dependency,
        int bulkheadPermitLimit = DefaultBulkheadPermitLimit, int bulkheadQueueLimit = DefaultBulkheadQueueLimit)
    {
        builder.AddResilienceHandler($"resilience-{dependency}", (pipeline, context) =>
        {
            var logger = context.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger($"Resilience.{dependency}");

            // Bulkhead: un ConcurrencyLimiter, que funciona como un semáforo con
            // fila. Si hay un lugar libre entre los PermitLimit, la llamada lo
            // toma y lo libera al terminar. Si no hay lugar pero queda espacio
            // entre los QueueLimit, espera y entra cuando se libera uno, en orden
            // de llegada. Si los lugares y la fila están llenos, se rechaza de
            // inmediato con RateLimiterRejectedException, sin llegar a la red;
            // Dependencies.GetAsync la convierte en "bulkhead lleno".
            //
            // Va primero en el pipeline para que un lugar cubra la llamada
            // completa, reintentos incluidos: una llamada que reintenta no ocupa
            // más de un lugar. Por lo mismo, el timeout total empieza a contar
            // recién cuando la llamada consigue su lugar, no mientras espera.
            pipeline.AddRateLimiter(new HttpRateLimiterStrategyOptions
            {
                DefaultRateLimiterOptions = new ConcurrencyLimiterOptions
                {
                    PermitLimit = bulkheadPermitLimit,
                    QueueLimit = bulkheadQueueLimit
                },
                OnRejected = _ =>
                {
                    logger.LogWarning("[BULKHEAD] {Dependency}: compartimento lleno [{PermitLimit} en curso, {QueueLimit} en espera]; se rechaza la llamada",
                        dependency, bulkheadPermitLimit, bulkheadQueueLimit);
                    return default;
                }
            });

            pipeline.AddTimeout(TotalTimeout);

            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
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

            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
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
}
