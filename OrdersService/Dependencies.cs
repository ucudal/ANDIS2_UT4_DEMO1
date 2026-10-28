using System.Net;
using System.Net.Http.Json;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace Ucu.Andis.OrdersService;

// Resultado de llamar a Usuarios o Productos. Distingue tres casos:
// - Value: la dependencia respondió con el recurso.
// - NotFound: la dependencia respondió 404, o 400 si el id no es válido.
// - Failure: la dependencia no está disponible; dice por qué, para que se vea qué
//   patrón actuó: circuito abierto, bulkhead lleno, timeout, error HTTP.
public record CallResult<T>(T? Value, bool NotFound, string? Failure)
{
    public bool Available => Failure is null;
}

public static class Dependencies
{
    public static async Task<CallResult<T>> GetAsync<T>(HttpClient client, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await client.GetAsync(path, cancellationToken);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                return new(default, true, null);

            if (!response.IsSuccessStatusCode)
                return new(default, false, $"error HTTP {(int)response.StatusCode}");

            var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
            return new(value, false, null);
        }
        catch (BrokenCircuitException)
        {
            return new(default, false, "circuito abierto");
        }
        // Bulkhead: el compartimento de la dependencia estaba lleno -lugares y
        // fila- y la llamada se rechazó sin salir a la red -ver Resilience.cs-.
        catch (RateLimiterRejectedException)
        {
            return new(default, false, "bulkhead lleno");
        }
        catch (TimeoutRejectedException)
        {
            return new(default, false, "timeout");
        }
        catch (ExecutionRejectedException ex)
        {
            return new(default, false, ex.GetType().Name);
        }
        catch (HttpRequestException)
        {
            return new(default, false, "servicio inaccesible");
        }
    }
}
