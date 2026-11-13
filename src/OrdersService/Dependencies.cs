using System.Net;
using System.Text;
using System.Xml.Linq;
using Polly;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using Ucu.Andis.Common;

namespace Ucu.Andis.OrdersService;

/// <summary>
/// Representa el resultado de llamar a Usuarios o Productos.
/// </summary>
/// <remarks>
/// Distingue tres casos:
/// <ul>
/// <li><c>Value</c>: la dependencia respondió con el recurso.</li>
/// <li><c>NotFound</c>: la dependencia respondió 404, o 400 si el id no es válido;
/// en Users, que es SOAP, la respuesta no trae <c>GetUserResult</c>.</li>
/// <li><c>Failure</c>: la dependencia no está disponible; dice por qué, para que se
/// vea qué patrón actuó: circuito abierto, bulkhead lleno, timeout, error
/// HTTP.</li>
/// </ul>
/// </remarks>
public record CallResult<T>(T? Value, bool NotFound, string? Failure)
{
    public bool Available => Failure is null;
}

public static class Dependencies
{
    public static Task<CallResult<T>> GetAsync<T>(HttpClient client, string path, CancellationToken cancellationToken = default) =>
        CallAsync(async () =>
        {
            using var response = await client.GetAsync(path, cancellationToken);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                return new CallResult<T>(default, true, null);

            if (!response.IsSuccessStatusCode)
                return new CallResult<T>(default, false, $"error HTTP {(int)response.StatusCode}");

            var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken);
            return new CallResult<T>(value, false, null);
        });

    /// <summary>
    /// Obtiene un User de Users, por SOAP.
    /// </summary>
    /// <remarks>
    /// Users expone SOAP: se arma el sobre a mano y se envía con el mismo
    /// <c>HttpClient</c>, para que la llamada pase por el pipeline de resiliencia. Un
    /// cliente WCF generado con <c>dotnet-svcutil</c> usaría su propio canal HTTP y
    /// dejaría afuera el Bulkhead, el Retry y el Circuit Breaker.
    /// </remarks>
    public static Task<CallResult<UserDto>> GetUserAsync(HttpClient client, string userId, CancellationToken cancellationToken = default) =>
        CallAsync(async () =>
        {
            var envelope = new XDocument(
                new XElement(Soap + "Envelope",
                    new XAttribute(XNamespace.Xmlns + "soap", Soap),
                    new XElement(Soap + "Body",
                        new XElement(Users + "GetUser",
                            new XElement(Users + "id", userId)))));

            using var request = new HttpRequestMessage(HttpMethod.Post, "")
            {
                Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
            };
            request.Headers.Add("SOAPAction", $"\"{Users.NamespaceName}/IUserService/GetUser\"");

            using var response = await client.SendAsync(request, cancellationToken);

            // Un SOAP Fault también llega como HTTP 500, igual que una falla del
            // servicio: por eso Users no responde con un Fault si el User no existe.
            if (!response.IsSuccessStatusCode)
                return new CallResult<UserDto>(default, false, $"error HTTP {(int)response.StatusCode}");

            var body = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var result = body.Descendants(Users + "GetUserResult").SingleOrDefault();

            // El User no existe: SoapCore omite GetUserResult; otro servidor SOAP podría
            // mandarlo con xsi:nil="true".
            if (result is null || (bool?)result.Attribute(Xsi + "nil") == true)
                return new CallResult<UserDto>(default, true, null);

            var user = new UserDto
            {
                Id = (string?)result.Element(Users + "Id"),
                Name = (string?)result.Element(Users + "Name")
            };
            return new CallResult<UserDto>(user, false, null);
        });

    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Users = "http://ucu.edu.uy/andis/users";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    // Convierte las excepciones del pipeline de resiliencia en el motivo de la
    // falla, igual para REST y para SOAP.
    private static async Task<CallResult<T>> CallAsync<T>(Func<Task<CallResult<T>>> call)
    {
        try
        {
            return await call();
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
