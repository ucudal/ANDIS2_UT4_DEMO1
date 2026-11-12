using System.Net;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Modifiability;

// Demo 24: los servicios leen sus valores de Consul mientras corren: un límite,
// un feature flag, un valor de otro servicio y una URL cambian sin reiniciar
// nada, y si Consul no responde, siguen con los últimos valores.
public class Demo24ExternalConfigurationStoreTests : DemoTest
{
    [TearDown]
    public async Task ClearStoreAsync()
    {
        await Shell.ComposeAsync("unpause consul");
        using (await Api.Http.DeleteAsync($"{DemoData.Consul}/v1/kv/andis/?recurse=true")) { }
        await Task.Delay(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task ALimitChangesWithoutRestarting()
    {
        var started = await StartedAtAsync();

        Assert.That(await OrderStatusAsync(10), Is.EqualTo(HttpStatusCode.Accepted), "sin valor, no hay límite");
        await Api.ConfigSetAsync("Orders/MaxQuantity", "3");
        Assert.That(await OrderStatusAsync(10), Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(await OrderStatusAsync(2), Is.EqualTo(HttpStatusCode.Accepted));

        Assert.That(await StartedAtAsync(), Is.EqualTo(started), "ningún contenedor se reinició");
    }

    [Test]
    public async Task AFeatureFlagShowsPrices()
    {
        var without = await Api.DetailsAsync(DemoData.User1);
        await Api.ConfigSetAsync("Features/ProductPrices", "true");
        var with = await Api.DetailsAsync(DemoData.User1);

        Assert.That(without.Rows.Select(r => r!.AsObject().ContainsKey("unit_price")), Has.All.False);
        Assert.That(with.Rows.Select(r => r!.AsObject().ContainsKey("unit_price")), Has.All.True);
    }

    [Test]
    public async Task AnotherServiceReadsItsValue()
    {
        await Api.ConfigSetAsync("Products/CacheTtlSeconds", "5");
        await Shell.ComposeAsync("exec -T redis redis-cli del products:all");
        using (await Api.Http.GetAsync($"{DemoData.Gateway}/products/")) { }

        var ttl = int.Parse((await Shell.ComposeAsync("exec -T redis redis-cli ttl products:all")).Output);

        Assert.That(ttl, Is.InRange(1, 5));
    }

    [Test]
    public async Task AUrlChangesWithoutRedeploying()
    {
        await Api.OnlyPatternAsync(null);
        await Api.ConfigSetAsync("ProductsService/BaseUrl", "http://products-service:8999/products/");
        var broken = await Api.DetailsAsync(DemoData.User1);
        await Api.ConfigDeleteAsync("ProductsService/BaseUrl");
        var restored = await Api.DetailsAsync(DemoData.User1);

        Assert.That(broken.Rows.Select(r => r!["product_name"]!.GetValue<string>()), Has.All.StartsWith("no disponible"));
        Assert.That(restored.Rows.Select(r => r!["product_name"]!.GetValue<string>()), Has.None.StartsWith("no disponible"));
    }

    [Test]
    [Category(TestEnvironment.Disruptive)]
    public async Task WithoutTheStoreServicesKeepTheLastValues()
    {
        await Api.ConfigSetAsync("Orders/MaxQuantity", "3");
        await Shell.ComposeAsync("pause consul");
        await Task.Delay(TimeSpan.FromSeconds(5));

        Assert.That(await OrderStatusAsync(10), Is.EqualTo(HttpStatusCode.BadRequest));
    }

    private static async Task<HttpStatusCode> OrderStatusAsync(int quantity)
    {
        using var response = await Api.PostOrderAsync(DemoData.OrdersDirect, DemoData.User2, DemoData.Trousers.Id, quantity);
        return response.StatusCode;
    }

    private static async Task<string> StartedAtAsync() =>
        (await Shell.RunAsync("docker inspect -f '{{.Name}} {{.State.StartedAt}}' $(docker compose ps -q orders-service orders-worker products-service)")).Output;
}
