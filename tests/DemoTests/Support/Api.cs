using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ucu.Andis.DemoTests.Support;

/// <summary>
/// Proporciona las llamadas HTTP de <c>demos/common.sh</c>: chaos, patrones,
/// Orders, Products, Users por SOAP y eventos publicados directamente en RabbitMQ.
/// </summary>
public static class Api
{
    private static readonly string[] Patterns = ["retry", "circuitBreaker", "bulkhead", "throttling"];

    /// <summary>
    /// Cliente HTTP sin redirecciones automáticas, para ver los 302 y 303, y sin
    /// descompresión, para ver qué comprime el gateway.
    /// </summary>
    public static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Namespace XML del contrato SOAP de Users.
    /// </summary>
    public const string UsersNamespace = "http://ucu.edu.uy/andis/users";

    // Chaos y patrones

    /// <summary>
    /// Hace que un servicio falle o responda lento a propósito, con
    /// <c>PUT {servicio}/chaos</c>.
    /// </summary>
    /// <param name="service">El prefijo del servicio en el gateway: <c>users</c>,
    /// <c>products</c> u <c>orders</c>.</param>
    /// <param name="failureRate">La fracción de solicitudes que responden 503, entre 0
    /// y 1.</param>
    /// <param name="delayMs">La demora agregada a cada solicitud, en milisegundos.</param>
    public static async Task ChaosAsync(string service, double failureRate, int delayMs)
    {
        using var response = await Http.PutAsJsonAsync($"{DemoData.Gateway}/{service}/chaos",
            new { failureRate, delayMs });
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Deja Users, Products y Orders sin fallas simuladas.
    /// </summary>
    public static async Task ResetChaosAsync()
    {
        foreach (var service in new[] { "users", "products", "orders" })
            using (await Http.DeleteAsync($"{DemoData.Gateway}/{service}/chaos")) { }
    }

    /// <summary>
    /// Deja prendido en Orders solo ese patrón: <c>retry</c>, <c>circuitBreaker</c>,
    /// <c>bulkhead</c> o <c>throttling</c>.
    /// </summary>
    /// <remarks>
    /// Con <c>null</c>, los apaga todos.
    /// </remarks>
    public static async Task OnlyPatternAsync(string? pattern)
    {
        var settings = Patterns
            .ToDictionary(p => p, p => p == pattern);
        using var response = await Http.PutAsJsonAsync($"{DemoData.Gateway}/orders/patterns", settings);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Vuelve a prender todos los patrones de resiliencia de Orders.
    /// </summary>
    public static async Task ResetPatternsAsync()
    {
        using (await Http.DeleteAsync($"{DemoData.Gateway}/orders/patterns")) { }
    }

    // Orders

    /// <summary>
    /// Representa la respuesta de una consulta de detalle de las Orders de un
    /// User: el código, las filas y cuánto tardó.
    /// </summary>
    public record DetailsResponse(HttpStatusCode Status, JsonArray Rows, TimeSpan Elapsed);

    /// <summary>
    /// Consulta el detalle de las Orders de un User y mide cuánto tarda.
    /// </summary>
    /// <remarks>
    /// Con <c>details</c>, la consulta llama a Users y a Products; con
    /// <c>details/view</c>, lee la vista materializada. Si la respuesta no es exitosa,
    /// no tiene filas.
    /// </remarks>
    public static async Task<DetailsResponse> DetailsAsync(string userId, string path = "details", string baseUrl = DemoData.Gateway)
    {
        var watch = Stopwatch.StartNew();
        using var response = await Http.GetAsync($"{baseUrl}/orders/user/{userId}/{path}");
        var body = await response.Content.ReadAsStringAsync();
        watch.Stop();
        var rows = response.IsSuccessStatusCode && JsonNode.Parse(body) is JsonArray array ? array : [];
        return new(response.StatusCode, rows, watch.Elapsed);
    }

    /// <summary>
    /// Crea una Order directamente en <c>orders-service</c>, sin pasar por el límite
    /// de POST del gateway, y devuelve su id.
    /// </summary>
    public static async Task<int> CreateOrderAsync(string userId, int productId, int quantity = 1)
    {
        using var response = await PostOrderAsync(DemoData.OrdersDirect, userId, productId, quantity);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), "POST /orders/");
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<int>();
    }

    /// <summary>
    /// Crea una Order en <c>baseUrl</c> y devuelve la respuesta sin verificarla.
    /// </summary>
    /// <remarks>
    /// Con el gateway, la solicitud pasa por su límite de POST; directamente en
    /// <c>orders-service</c>, no.
    /// </remarks>
    public static Task<HttpResponseMessage> PostOrderAsync(string baseUrl, string userId, int productId, int quantity = 1) =>
        Http.PostAsJsonAsync($"{baseUrl}/orders/", new { userId, productId, quantity });

    /// <summary>
    /// Devuelve una Order, leída directamente de <c>orders-service</c>.
    /// </summary>
    public static async Task<JsonObject> GetOrderAsync(int id) =>
        (await Http.GetFromJsonAsync<JsonObject>($"{DemoData.OrdersDirect}/orders/{id}"))!;

    /// <summary>
    /// Devuelve el estado de una Order.
    /// </summary>
    public static async Task<string> OrderStatusAsync(int id) =>
        (await GetOrderAsync(id))["status"]!.GetValue<string>();

    /// <summary>
    /// Espera a que un worker confirme o rechace cada Order, y devuelve sus estados.
    /// </summary>
    public static async Task<Dictionary<int, string>> WaitProcessedAsync(IEnumerable<int> ids, TimeSpan? timeout = null)
    {
        var statuses = new Dictionary<int, string>();
        foreach (var id in ids)
            statuses[id] = await Eventually.GetAsync(() => OrderStatusAsync(id), s => s != "Pending", timeout ?? TimeSpan.FromSeconds(120));
        return statuses;
    }

    /// <summary>
    /// Espera a que no quede ninguna Order <c>OrdersService.Models.OrderStatus.Pending</c>, para comparar dos lecturas sin que
    /// un worker cambie algo entre medio.
    /// </summary>
    public static Task WaitNoPendingAsync() =>
        Eventually.GetAsync(() => Http.GetStringAsync($"{DemoData.Gateway}/orders/"),
            orders => !orders.Contains("\"Pending\""), TimeSpan.FromSeconds(120));

    /// <summary>
    /// Devuelve el historial de eventos de una Order y el estado que resulta de
    /// reproducirlos.
    /// </summary>
    /// <remarks>
    /// Event Sourcing.
    /// </remarks>
    public static async Task<JsonObject> EventsAsync(int id) =>
        (await Http.GetFromJsonAsync<JsonObject>($"{DemoData.OrdersDirect}/orders/{id}/events"))!;

    /// <summary>
    /// Devuelve los tipos de los eventos de una Order, en orden.
    /// </summary>
    public static async Task<List<string>> EventTypesAsync(int id) =>
        (await EventsAsync(id))["events"]!.AsArray().Select(e => e!["type"]!.GetValue<string>()).ToList();

    // Products

    /// <summary>
    /// Cambia el nombre y el precio de un Product.
    /// </summary>
    public static async Task UpdateProductAsync(int id, string name, decimal price)
    {
        using var response = await Http.PutAsJsonAsync($"{DemoData.Gateway}/products/{id}", new { name, price });
        await EnsureSuccessAsync(response);
    }

    /// <summary>
    /// Verifica que la respuesta sea exitosa, como <c>EnsureSuccessStatusCode</c>, pero
    /// el error incluye el cuerpo de la respuesta, para saber qué respondió el
    /// servicio.
    /// </summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// Devuelve el stock de un Product.
    /// </summary>
    /// <remarks>
    /// Saga: el stock lo cambian las reservas y las compensaciones.
    /// </remarks>
    public static async Task<int> StockAsync(int id) =>
        (await GetProductAsync(id))["stock"]!.GetValue<int>();

    /// <summary>
    /// Fija el stock de un Product, como un ajuste de inventario.
    /// </summary>
    /// <remarks>
    /// Saga: sirve para ver qué pasa cuando falta stock.
    /// </remarks>
    public static async Task SetStockAsync(int id, int stock)
    {
        using var response = await Http.PutAsJsonAsync($"{DemoData.Gateway}/products/{id}/stock", new { stock });
        await EnsureSuccessAsync(response);
    }

    /// <summary>
    /// Devuelve un Product, a través del gateway.
    /// </summary>
    public static async Task<JsonObject> GetProductAsync(int id) =>
        (await Http.GetFromJsonAsync<JsonObject>($"{DemoData.Gateway}/products/{id}"))!;

    /// <summary>
    /// Devuelve cuántas consultas de un Product recibió Products desde que arrancó,
    /// según sus métricas, como <c>products_calls</c> en <c>demos/common.sh</c>.
    /// </summary>
    public static async Task<double> ProductsCallsAsync()
    {
        var metrics = await Http.GetStringAsync($"{DemoData.ProductsDirect}/metrics");
        return metrics.Split('\n')
            .Where(l => l.StartsWith("http_server_request_duration_seconds_count{", StringComparison.Ordinal)
                && l.Contains("http_request_method=\"GET\"")
                && l.Contains("http_route=\"products/{productId}\""))
            .Sum(l => double.Parse(l[(l.LastIndexOf(' ') + 1)..], System.Globalization.CultureInfo.InvariantCulture));
    }

    // Users, por SOAP

    /// <summary>
    /// Arma el sobre SOAP de una operación de Users.
    /// </summary>
    /// <param name="operation">El nombre de la operación, por ejemplo
    /// <c>Users.Soap.IUserService.GetUser</c>.</param>
    /// <param name="parameters">Los parámetros de la operación, ya en XML.</param>
    public static string Envelope(string operation, string parameters) =>
        $"<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><{operation} xmlns=\"{UsersNamespace}\">{parameters}</{operation}></soap:Body></soap:Envelope>";

    /// <summary>
    /// Arma una solicitud SOAP a Users, con el sobre y el encabezado
    /// <c>SOAPAction</c>.
    /// </summary>
    public static HttpRequestMessage SoapRequest(string url, string operation, string parameters)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(Envelope(operation, parameters), Encoding.UTF8, "text/xml"),
        };
        request.Headers.Add("SOAPAction", $"\"{UsersNamespace}/IUserService/{operation}\"");
        return request;
    }

    /// <summary>
    /// Cambia el nombre y el email de un User, por SOAP.
    /// </summary>
    /// <remarks>
    /// Users publica el evento <c>Common.UserUpdated</c>.
    /// </remarks>
    public static async Task UpdateUserAsync(string id, string name, string email)
    {
        using var request = SoapRequest($"{DemoData.Gateway}/users/UserService.svc", "UpdateUser",
            $"<id>{id}</id><name>{name}</name><email>{email}</email>");
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Representa la respuesta de <c>Users.Soap.IUserService.GetUser</c>: el código, la versión y la variante
    /// que la atendieron, y el cuerpo.
    /// </summary>
    /// <remarks>
    /// Si la solicitud falla, el código es 0 y el cuerpo es el error.
    /// </remarks>
    public record UserResponse(HttpStatusCode Status, string? Version, string? Variant, string Body);

    /// <summary>
    /// Llama a <c>Users.Soap.IUserService.GetUser</c> del User <c>id</c> en <c>baseUrl</c>.
    /// </summary>
    /// <remarks>
    /// Lo usan las demos de facilidad de despliegue: <c>X-Version</c> dice qué versión
    /// respondió y <c>X-Variant</c>, qué variante del experimento.
    /// </remarks>
    public static async Task<UserResponse> GetUserAsync(string baseUrl, string id, string? segment = null)
    {
        using var request = SoapRequest($"{baseUrl}/users/UserService.svc", "GetUser", $"<id>{id}</id>");
        if (segment is not null)
            request.Headers.Add("X-Segment", segment);
        // Una conexión nueva por solicitud, como curl en las demos. Si se
        // reutilizara, durante una conmutación podría quedar colgada una
        // conexión hacia un destino que ya no existe, y se mediría el cliente
        // en lugar del despliegue.
        request.Headers.ConnectionClose = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            using var response = await Http.SendAsync(request, cts.Token);
            return new(response.StatusCode, Header(response, "X-Version"), Header(response, "X-Variant"),
                await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(0, null, null, ex.Message);
        }
    }

    /// <summary>
    /// Hace <c>n</c> solicitudes de <c>Users.Soap.IUserService.GetUser</c> de alice, como <c>versions</c> en
    /// <c>demos/common.sh</c>.
    /// </summary>
    public static async Task<List<UserResponse>> VersionsAsync(string baseUrl, int n)
    {
        var responses = new List<UserResponse>();
        for (var i = 0; i < n; i++)
            responses.Add(await GetUserAsync(baseUrl, DemoData.User1));
        return responses;
    }

    /// <summary>
    /// Devuelve el valor de un encabezado de la respuesta, o <c>null</c> si no está.
    /// </summary>
    public static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    // RabbitMQ

    /// <summary>
    /// Publica un evento en el exchange <c>andis.events</c> con la API de la consola,
    /// como si lo hubiera publicado un servicio.
    /// </summary>
    public static async Task PublishEventAsync(string routingKey, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{DemoData.RabbitMqApi}/api/exchanges/%2F/andis.events/publish")
        {
            Content = JsonContent.Create(new
            {
                properties = new { },
                routing_key = routingKey,
                payload = JsonSerializer.Serialize(payload),
                payload_encoding = "string",
            }),
        };
        request.Headers.Authorization = new("Basic", Convert.ToBase64String("guest:guest"u8.ToArray()));
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Consulta la API de la consola de RabbitMQ, por ejemplo las colas o los
    /// consumidores.
    /// </summary>
    public static async Task<JsonArray> RabbitMqAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{DemoData.RabbitMqApi}/api/{path}");
        request.Headers.Authorization = new("Basic", Convert.ToBase64String("guest:guest"u8.ToArray()));
        using var response = await Http.SendAsync(request);
        return (await response.Content.ReadFromJsonAsync<JsonArray>())!;
    }

    // Consul

    /// <summary>
    /// Cambia un valor del almacén de configuración, Consul KV, y espera a que los
    /// servicios lo lean.
    /// </summary>
    /// <remarks>
    /// External Configuration Store: los servicios leen el almacén cada 2 segundos.
    /// </remarks>
    public static async Task ConfigSetAsync(string key, string value)
    {
        using var response = await Http.PutAsync($"{DemoData.Consul}/v1/kv/andis/{key}", new StringContent(value));
        response.EnsureSuccessStatusCode();
        // Los servicios leen el almacén cada 2 segundos.
        await Task.Delay(TimeSpan.FromSeconds(3));
    }

    /// <summary>
    /// Borra un valor del almacén de configuración y espera a que los servicios lo
    /// lean.
    /// </summary>
    /// <remarks>
    /// External Configuration Store: sin el valor, cada servicio vuelve a su valor por
    /// defecto.
    /// </remarks>
    public static async Task ConfigDeleteAsync(string key)
    {
        using (await Http.DeleteAsync($"{DemoData.Consul}/v1/kv/andis/{key}")) { }
        await Task.Delay(TimeSpan.FromSeconds(3));
    }
}
