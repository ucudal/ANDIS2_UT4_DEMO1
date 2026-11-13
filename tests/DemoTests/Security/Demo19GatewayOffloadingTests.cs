using System.Net;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Security;

/// <summary>
/// Contiene las pruebas de la demo 19: el gateway termina TLS, valida los tokens y
/// comprime las respuestas; los servicios solo hablan HTTP.
/// </summary>
public class Demo19GatewayOffloadingTests : DemoTest
{
    [Test]
    public async Task ServicesOnlySpeakHttp()
    {
        using var http = await Api.Http.GetAsync($"{DemoData.OrdersDirect}/orders/");
        Assert.That(http.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var https = await Identity.HttpsAsync();
        Assert.That(async () => await https.GetAsync("https://localhost:8000/orders/"), Throws.InstanceOf<HttpRequestException>());
    }

    [Test]
    public async Task TheGatewayTerminatesTlsAndValidatesTokens()
    {
        var https = await Identity.HttpsAsync();
        using var health = await https.GetAsync($"{DemoData.HttpsGateway}/health/orders");

        Assert.That(health.StatusCode, Is.EqualTo(HttpStatusCode.OK), "el certificado es el del gateway");
        Assert.That(await Demo18FederatedIdentityTests.ViewStatusAsync(null, DemoData.User1), Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task TheGatewayCompressesOverHttps()
    {
        var carol = await Identity.TokenAsync("carol");
        var https = await Identity.HttpsAsync();

        using var plainRequest = new HttpRequestMessage(HttpMethod.Get, $"{DemoData.Gateway}/orders/");
        plainRequest.Headers.AcceptEncoding.ParseAdd("gzip");
        using var plain = await Api.Http.SendAsync(plainRequest);

        using var compressedRequest = Identity.Request(HttpMethod.Get, $"{DemoData.HttpsGateway}/orders/", carol);
        compressedRequest.Headers.AcceptEncoding.ParseAdd("gzip");
        using var compressed = await https.SendAsync(compressedRequest);

        Assert.That(plain.Content.Headers.ContentEncoding, Is.Empty);
        Assert.That(compressed.Content.Headers.ContentEncoding, Does.Contain("gzip"));
    }
}
