using System.Net;
using System.Net.Http.Json;
using System.Text;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Security;

// Demo 20: por HTTPS, el gateway rechaza las rutas de administración, los ids
// mal formados, los métodos y cuerpos que no corresponden y los encabezados de
// identidad falsos, sin llegar a los servicios.
public class Demo20GatekeeperTests : DemoTest
{
    [TestCase("/orders/chaos", HttpStatusCode.OK)]
    [TestCase("/orders/patterns", HttpStatusCode.OK)]
    [TestCase("/products/cache", HttpStatusCode.OK)]
    [TestCase("/orders/user/1%20OR%201=1/details", HttpStatusCode.BadRequest)]
    [TestCase("/products/no-es-un-id", HttpStatusCode.BadRequest)]
    public async Task UnknownRoutesAreRejectedOnlyOverHttps(string path, HttpStatusCode overHttp)
    {
        var alice = await Identity.TokenAsync("alice");
        var https = await Identity.HttpsAsync();

        using var http = await Api.Http.GetAsync($"{DemoData.Gateway}{path}");
        using var secure = await https.SendAsync(Identity.Request(HttpMethod.Get, $"{DemoData.HttpsGateway}{path}", alice));

        Assert.That(http.StatusCode, Is.EqualTo(overHttp));
        Assert.That(secure.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    [Test]
    public async Task MethodsAndBodiesAreChecked()
    {
        var alice = await Identity.TokenAsync("alice");

        Assert.That(await HttpsStatusAsync(alice, HttpMethod.Delete, "/orders/1"), Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await HttpsStatusAsync(alice, HttpMethod.Post, "/orders/", new StringContent("hola", Encoding.UTF8, "text/plain")),
            Is.EqualTo(HttpStatusCode.UnsupportedMediaType));
        Assert.That(await HttpsStatusAsync(alice, HttpMethod.Post, "/orders/",
            new StringContent(new string('a', 100_000), Encoding.UTF8, "application/json")), Is.EqualTo(HttpStatusCode.RequestEntityTooLarge));
    }

    [Test]
    public async Task FakeIdentityHeadersAreReplaced()
    {
        var alice = await Identity.TokenAsync("alice");
        var https = await Identity.HttpsAsync();
        using var request = Identity.Request(HttpMethod.Get, $"{DemoData.HttpsGateway}/orders/user/{DemoData.User2}/details/view", alice);
        request.Headers.Add("X-User-Id", DemoData.User2.ToString());
        request.Headers.Add("X-User-Name", DemoData.User2Name);

        using var response = await https.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden), "para Orders, sigue siendo alice");
    }

    [Test]
    public async Task WithoutTheGatewayAnyoneCanImpersonate()
    {
        using var chaos = await Api.Http.GetAsync($"{DemoData.OrdersDirect}/orders/chaos");
        Assert.That(chaos.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{DemoData.OrdersDirect}/orders/")
        {
            Content = JsonContent.Create(new { productId = DemoData.Shirt.Id, quantity = 1 }),
        };
        request.Headers.Add("X-User-Id", DemoData.User2.ToString());
        request.Headers.Add("X-User-Name", DemoData.User2Name);
        request.Headers.Add("X-User-Roles", "customer");
        using var response = await Api.Http.SendAsync(request);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted), "sin el gateway, nadie reemplaza los encabezados");
    }

    [Test]
    [Category(TestEnvironment.Gatekeeper)]
    public async Task UnpublishedPortsLeaveTheGatewayAsTheOnlyEntrance()
    {
        try
        {
            await Shell.ComposeAsync("-f docker-compose.yaml -f docker-compose.gatekeeper.yaml up -d --wait", TimeSpan.FromMinutes(10));

            Assert.That(async () => await Api.Http.GetAsync($"{DemoData.OrdersDirect}/orders/chaos"), Throws.InstanceOf<HttpRequestException>());
            var alice = await Identity.TokenAsync("alice");
            Assert.That(await Demo18FederatedIdentityTests.ViewStatusAsync(alice, DemoData.User1), Is.EqualTo(HttpStatusCode.OK));
        }
        finally
        {
            await Shell.ComposeAsync("up -d --wait", TimeSpan.FromMinutes(10));
        }
    }

    private static async Task<HttpStatusCode> HttpsStatusAsync(string token, HttpMethod method, string path, HttpContent? content = null)
    {
        var https = await Identity.HttpsAsync();
        using var request = Identity.Request(method, $"{DemoData.HttpsGateway}{path}", token);
        request.Content = content;
        using var response = await https.SendAsync(request);
        return response.StatusCode;
    }
}
