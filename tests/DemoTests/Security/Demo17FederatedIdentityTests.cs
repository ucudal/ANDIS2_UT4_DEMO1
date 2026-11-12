using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Security;

// Demo 17: por HTTPS, el User sale del token de Keycloak y no del cuerpo; cada
// uno ve solo sus Orders, el rol admin ve todas, y un token alterado no entra.
public class Demo17FederatedIdentityTests : DemoTest
{
    [Test]
    public async Task TheOrderBelongsToTheTokenUser()
    {
        var https = await Identity.HttpsAsync();
        var bob = await Identity.TokenAsync("bob");
        Assert.That(Identity.Claims(bob)["user_id"]!.ToString(), Is.EqualTo(DemoData.User2.ToString()));

        // Sin userId en el cuerpo: Orders lo toma del token.
        using var response = await https.SendAsync(Identity.Request(HttpMethod.Post, $"{DemoData.HttpsGateway}/orders/", bob,
            new { productId = DemoData.Shirt.Id, quantity = 1 }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var id = (await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<int>();
        Assert.That((await Api.GetOrderAsync(id))["user_id"]!.ToString(), Is.EqualTo(DemoData.User2.ToString()));
    }

    [Test]
    public async Task NobodyCreatesOrdersForSomeoneElse()
    {
        var https = await Identity.HttpsAsync();
        var bob = await Identity.TokenAsync("bob");

        using var response = await https.SendAsync(Identity.Request(HttpMethod.Post, $"{DemoData.HttpsGateway}/orders/", bob,
            new { userId = DemoData.User1, productId = DemoData.Shirt.Id, quantity = 1 }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task EachUserSeesOnlyTheirOrders()
    {
        var bob = await Identity.TokenAsync("bob");
        var alice = await Identity.TokenAsync("alice");
        var carol = await Identity.TokenAsync("carol");

        Assert.That(await ViewStatusAsync(bob, DemoData.User2), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await ViewStatusAsync(bob, DemoData.User1), Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await ViewStatusAsync(alice, DemoData.User1), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(await ViewStatusAsync(alice, DemoData.User2), Is.EqualTo(HttpStatusCode.Forbidden));
        Assert.That(await ViewStatusAsync(carol, DemoData.User2), Is.EqualTo(HttpStatusCode.OK), "carol tiene el rol admin");
    }

    [Test]
    public async Task AnAdminIsNotACustomer()
    {
        var https = await Identity.HttpsAsync();
        var carol = await Identity.TokenAsync("carol");

        using var response = await https.SendAsync(Identity.Request(HttpMethod.Post, $"{DemoData.HttpsGateway}/orders/", carol,
            new { userId = DemoData.User2, productId = DemoData.Shirt.Id, quantity = 1 }));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
    }

    [Test]
    public async Task WithoutAValidTokenNobodyGetsIn()
    {
        var bob = await Identity.TokenAsync("bob");
        var tampered = Identity.Tamper(bob, c => c["user_id"] = DemoData.User1);

        Assert.That(await ViewStatusAsync(null, DemoData.User1), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await ViewStatusAsync(tampered, DemoData.User1), Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    internal static async Task<HttpStatusCode> ViewStatusAsync(string? token, string userId)
    {
        var https = await Identity.HttpsAsync();
        using var response = await https.SendAsync(Identity.Request(HttpMethod.Get,
            $"{DemoData.HttpsGateway}/orders/user/{userId}/details/view", token));
        return response.StatusCode;
    }
}
