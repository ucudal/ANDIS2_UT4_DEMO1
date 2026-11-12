using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ucu.Andis.DemoTests.Support;

// Las llamadas HTTP de demos/common.sh: chaos, patrones, Orders, Products,
// Users por SOAP y eventos publicados directamente en RabbitMQ.
public static class Api
{
    // Sin redirecciones automáticas, para ver los 302 y 303, y sin
    // descompresión, para ver qué comprime el gateway.
    public static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public const string UsersNamespace = "http://ucu.edu.uy/andis/users";

    // Chaos y patrones

    public static async Task ChaosAsync(string service, double failureRate, int delayMs)
    {
        using var response = await Http.PutAsJsonAsync($"{DemoData.Gateway}/{service}/chaos",
            new { failureRate, delayMs });
        response.EnsureSuccessStatusCode();
    }

    public static async Task ResetChaosAsync()
    {
        foreach (var service in new[] { "users", "products", "orders" })
            using (await Http.DeleteAsync($"{DemoData.Gateway}/{service}/chaos")) { }
    }

    // Deja prendido en Orders solo ese patrón: retry, circuitBreaker, bulkhead o
    // throttling. Con null, los apaga todos.
    public static async Task OnlyPatternAsync(string? pattern)
    {
        var settings = new[] { "retry", "circuitBreaker", "bulkhead", "throttling" }
            .ToDictionary(p => p, p => p == pattern);
        using var response = await Http.PutAsJsonAsync($"{DemoData.Gateway}/orders/patterns", settings);
        response.EnsureSuccessStatusCode();
    }

    public static async Task ResetPatternsAsync()
    {
        using (await Http.DeleteAsync($"{DemoData.Gateway}/orders/patterns")) { }
    }

    // Orders

    public record DetailsResponse(HttpStatusCode Status, JsonArray Rows, TimeSpan Elapsed);

    public static async Task<DetailsResponse> DetailsAsync(string userId, string path = "details", string baseUrl = DemoData.Gateway)
    {
        var watch = Stopwatch.StartNew();
        using var response = await Http.GetAsync($"{baseUrl}/orders/user/{userId}/{path}");
        var body = await response.Content.ReadAsStringAsync();
        watch.Stop();
        var rows = response.IsSuccessStatusCode && JsonNode.Parse(body) is JsonArray array ? array : [];
        return new(response.StatusCode, rows, watch.Elapsed);
    }

    // Crea una Order directamente en orders-service, sin pasar por el límite de
    // POST del gateway, y devuelve su id.
    public static async Task<int> CreateOrderAsync(string userId, int productId, int quantity = 1)
    {
        using var response = await PostOrderAsync(DemoData.OrdersDirect, userId, productId, quantity);
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), "POST /orders/");
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<int>();
    }

    public static Task<HttpResponseMessage> PostOrderAsync(string baseUrl, string userId, int productId, int quantity = 1) =>
        Http.PostAsJsonAsync($"{baseUrl}/orders/", new { userId, productId, quantity });

    public static async Task<JsonObject> GetOrderAsync(int id) =>
        (await Http.GetFromJsonAsync<JsonObject>($"{DemoData.OrdersDirect}/orders/{id}"))!;

    public static async Task<string> OrderStatusAsync(int id) =>
        (await GetOrderAsync(id))["status"]!.GetValue<string>();

    // Espera a que un worker confirme o rechace cada Order, y devuelve sus estados.
    public static async Task<Dictionary<int, string>> WaitProcessedAsync(IEnumerable<int> ids, TimeSpan? timeout = null)
    {
        var statuses = new Dictionary<int, string>();
        foreach (var id in ids)
            statuses[id] = await Eventually.GetAsync(() => OrderStatusAsync(id), s => s != "Pending", timeout ?? TimeSpan.FromSeconds(120));
        return statuses;
    }

    // Espera a que no quede ninguna Order Pending, para comparar dos lecturas sin
    // que un worker cambie algo entre medio.
    public static Task WaitNoPendingAsync() =>
        Eventually.GetAsync(() => Http.GetStringAsync($"{DemoData.Gateway}/orders/"),
            orders => !orders.Contains("\"Pending\""), TimeSpan.FromSeconds(120));

    public static async Task<JsonObject> EventsAsync(int id) =>
        (await Http.GetFromJsonAsync<JsonObject>($"{DemoData.OrdersDirect}/orders/{id}/events"))!;

    public static async Task<List<string>> EventTypesAsync(int id) =>
        (await EventsAsync(id))["events"]!.AsArray().Select(e => e!["type"]!.GetValue<string>()).ToList();

    // Products

    public static async Task UpdateProductAsync(int id, string name, decimal price)
    {
        using var response = await Http.PutAsJsonAsync($"{DemoData.Gateway}/products/{id}", new { name, price });
        await EnsureSuccessAsync(response);
    }

    // Como EnsureSuccessStatusCode, pero el error incluye el cuerpo de la
    // respuesta, para saber qué respondió el servicio.
    public static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"{(int)response.StatusCode} {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}: {await response.Content.ReadAsStringAsync()}");
    }

    public static async Task<JsonObject> GetProductAsync(int id) =>
        (await Http.GetFromJsonAsync<JsonObject>($"{DemoData.Gateway}/products/{id}"))!;

    // Cuántas consultas de un Product recibió Products desde que arrancó, según
    // sus métricas, como products_calls en demos/common.sh.
    public static async Task<double> ProductsCallsAsync()
    {
        var metrics = await Http.GetStringAsync($"{DemoData.ProductsDirect}/metrics");
        return metrics.Split('\n')
            .Where(l => l.StartsWith("http_server_request_duration_seconds_count{")
                && l.Contains("http_request_method=\"GET\"")
                && l.Contains("http_route=\"products/{productId}\""))
            .Sum(l => double.Parse(l[(l.LastIndexOf(' ') + 1)..], System.Globalization.CultureInfo.InvariantCulture));
    }

    // Users, por SOAP

    public static string Envelope(string operation, string parameters) =>
        $"<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><{operation} xmlns=\"{UsersNamespace}\">{parameters}</{operation}></soap:Body></soap:Envelope>";

    public static HttpRequestMessage SoapRequest(string url, string operation, string parameters)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(Envelope(operation, parameters), Encoding.UTF8, "text/xml"),
        };
        request.Headers.Add("SOAPAction", $"\"{UsersNamespace}/IUserService/{operation}\"");
        return request;
    }

    public static async Task UpdateUserAsync(string id, string name, string email)
    {
        using var request = SoapRequest($"{DemoData.Gateway}/users/UserService.svc", "UpdateUser",
            $"<id>{id}</id><name>{name}</name><email>{email}</email>");
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    public record UserResponse(HttpStatusCode Status, string? Version, string? Variant, string Body);

    // GetUser del User id en baseUrl. Lo usan las demos de facilidad de
    // despliegue: X-Version dice qué versión respondió y X-Variant, qué variante
    // del experimento.
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

    // n solicitudes de GetUser de alice, como versions en demos/common.sh.
    public static async Task<List<UserResponse>> VersionsAsync(string baseUrl, int n)
    {
        var responses = new List<UserResponse>();
        for (var i = 0; i < n; i++)
            responses.Add(await GetUserAsync(baseUrl, DemoData.User1));
        return responses;
    }

    public static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    // RabbitMQ

    // Publica un evento en el exchange andis.events con la API de la consola,
    // como si lo hubiera publicado un servicio.
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

    public static async Task<JsonArray> RabbitMqAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{DemoData.RabbitMqApi}/api/{path}");
        request.Headers.Authorization = new("Basic", Convert.ToBase64String("guest:guest"u8.ToArray()));
        using var response = await Http.SendAsync(request);
        return (await response.Content.ReadFromJsonAsync<JsonArray>())!;
    }

    // Consul

    public static async Task ConfigSetAsync(string key, string value)
    {
        using var response = await Http.PutAsync($"{DemoData.Consul}/v1/kv/andis/{key}", new StringContent(value));
        response.EnsureSuccessStatusCode();
        // Los servicios leen el almacén cada 2 segundos.
        await Task.Delay(TimeSpan.FromSeconds(3));
    }

    public static async Task ConfigDeleteAsync(string key)
    {
        using (await Http.DeleteAsync($"{DemoData.Consul}/v1/kv/andis/{key}")) { }
        await Task.Delay(TimeSpan.FromSeconds(3));
    }
}
