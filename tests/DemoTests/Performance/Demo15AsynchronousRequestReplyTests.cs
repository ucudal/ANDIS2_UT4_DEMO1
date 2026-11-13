using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Performance;

/// <summary>
/// Contiene las pruebas de la demo 15: <c>POST /orders/</c> responde 202 con
/// <c>Location</c> y <c>Retry-After</c>; el estado responde 200 mientras la Order
/// está <c>OrdersService.Models.OrderStatus.Pending</c> y 303 cuando se procesó. Con <c>Idempotency-Key</c>, un reintento no
/// crea otra Order.
/// </summary>
public class Demo15AsynchronousRequestReplyTests : DemoTest
{
    [Test]
    public async Task StatusRedirectsWhenTheOrderIsProcessed()
    {
        using var created = await Api.PostOrderAsync(DemoData.Gateway, DemoData.User2, DemoData.Shirt.Id);

        Assert.That(created.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(created.Headers.RetryAfter, Is.Not.Null);
        var location = created.Headers.Location!.ToString();
        Assert.That(location, Does.Match(@"^/orders/\d+/status$"));

        var status = await Eventually.GetAsync(async () =>
        {
            using var response = await Api.Http.GetAsync($"{DemoData.Gateway}{location}");
            return (response.StatusCode, Location: response.Headers.Location?.ToString());
        }, s => s.StatusCode == HttpStatusCode.SeeOther);

        Assert.That(status.StatusCode, Is.EqualTo(HttpStatusCode.SeeOther));
        var order = await Api.Http.GetFromJsonAsync<JsonObject>($"{DemoData.Gateway}{status.Location}");
        Assert.That(order!["status"]!.GetValue<string>(), Is.EqualTo("Confirmed"));
    }

    [Test]
    public async Task IdempotencyKeyReturnsTheSameOrder()
    {
        var key = Guid.NewGuid().ToString();
        var body = new { userId = DemoData.User2, productId = DemoData.Shirt.Id, quantity = 1 };

        using var first = await PostWithKeyAsync(key, body);
        using var retry = await PostWithKeyAsync(key, body);
        using var other = await PostWithKeyAsync(key, body with { quantity = 3 });

        var firstId = (await first.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<int>();
        var retryId = (await retry.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<int>();
        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(retryId, Is.EqualTo(firstId));
        Assert.That(Api.Header(retry, "Idempotent-Replayed"), Is.EqualTo("true"));
        Assert.That(other.StatusCode, Is.EqualTo(HttpStatusCode.UnprocessableEntity));
    }

    private static async Task<HttpResponseMessage> PostWithKeyAsync(string key, object body)
    {
        // El gateway admite 1 POST por segundo con una ráfaga de 5.
        await Task.Delay(TimeSpan.FromSeconds(1));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{DemoData.Gateway}/orders/")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", key);
        return await Api.Http.SendAsync(request);
    }
}
