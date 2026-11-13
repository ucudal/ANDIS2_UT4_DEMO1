using System.Net;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Security;

/// <summary>
/// Contiene las pruebas de la demo 21: el sidecar de Products exige el rol admin
/// para cambiar el catálogo, verifica la firma del token y no deja pasar a quien no
/// lo tiene.
/// </summary>
public class Demo21SidecarTests : DemoTest
{
    private static readonly DemoData.Product Product = DemoData.Shoes;
    private static readonly object Body = new { name = Product.Name, price = Product.Price };

    [TearDown]
    public Task RestoreAsync() => Api.UpdateProductAsync(Product.Id, Product.Name, Product.Price);

    [Test]
    public async Task OnlyAdminsChangeTheCatalog()
    {
        var alice = await Identity.TokenAsync("alice");
        var carol = await Identity.TokenAsync("carol");
        var https = await Identity.HttpsAsync();
        var url = $"{DemoData.HttpsGateway}/products/{Product.Id}";

        using var read = await https.SendAsync(Identity.Request(HttpMethod.Get, url, alice));
        using var aliceChange = await https.SendAsync(Identity.Request(HttpMethod.Put, url, alice, Body));
        using var carolChange = await https.SendAsync(Identity.Request(HttpMethod.Put, url, carol, Body));

        Assert.That(read.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(aliceChange.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await aliceChange.Content.ReadAsStringAsync(), Does.Contain("RBAC"), "el 403 lo responde Envoy");
        Assert.That(carolChange.StatusCode, Is.EqualTo(HttpStatusCode.OK));
    }

    [Test]
    public async Task InsideTheNetworkTheSidecarStillProtects()
    {
        var alice = await Identity.TokenAsync("alice");
        var tampered = Identity.Tamper(alice, c => c["realm_access"]!["roles"] = new System.Text.Json.Nodes.JsonArray("admin"));
        var json = System.Text.Json.JsonSerializer.Serialize(Body);

        var direct = await InsideAsync($"http://products-service:8000/products/{Product.Id}", json, null);
        var withoutToken = await InsideAsync($"http://products-service:9000/products/{Product.Id}", json, null);
        var withTamperedToken = await InsideAsync($"http://products-service:9000/products/{Product.Id}", json, tampered);

        Assert.That(direct, Is.EqualTo("200"), "directamente a Products, sin sidecar, no hay control");
        Assert.That(withoutToken, Is.EqualTo("403"));
        Assert.That(withTamperedToken, Is.EqualTo("401"), "Envoy verifica la firma");
    }

    // Un PUT desde adentro de la red de Docker, con el curl del gateway, como
    // call_inside en demos/common.sh. Devuelve el código HTTP.
    private static async Task<string> InsideAsync(string url, string json, string? token)
    {
        var auth = token is null ? "" : $"-H 'Authorization: Bearer {token}'";
        var result = await Shell.ComposeAsync(
            $"exec -T api-gateway curl -s -o /dev/null -w '%{{http_code}}' -X PUT -H 'Content-Type: application/json' {auth} -d '{json}' {url}");
        return result.Output;
    }
}
