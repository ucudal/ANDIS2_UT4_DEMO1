using System.Net;
using System.Text.Json.Nodes;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Modifiability;

// Demo 26: la web y la app móvil tienen cada una su backend, con la respuesta a
// su medida, y redesplegar uno no afecta al otro.
public class Demo26BackendsForFrontendsTests : DemoTest
{
    [Test]
    public async Task EachClientGetsItsOwnResponse()
    {
        var webBody = await Api.Http.GetStringAsync($"{DemoData.Gateway}/web/orders/user/{DemoData.User1}");
        var mobileBody = await Api.Http.GetStringAsync($"{DemoData.Gateway}/mobile/orders/user/{DemoData.User1}");
        var web = JsonNode.Parse(webBody)!;
        var mobile = JsonNode.Parse(mobileBody)!;

        var orders = web["orders"]!.AsArray();
        Assert.That(orders.Select(o => o!["unitPrice"]), Has.All.Not.Null);
        Assert.That(web["total"]!.GetValue<decimal>(), Is.EqualTo(orders.Sum(o => o!["total"]!.GetValue<decimal>())));
        Assert.That(mobile["orders"]!.AsArray().Select(o => o!.AsObject().Select(p => p.Key)),
            Has.All.EquivalentTo(new[] { "product", "status" }));
        Assert.That(mobileBody.Length, Is.LessThan(webBody.Length));
    }

    [Test]
    [Category(TestEnvironment.Disruptive)]
    public async Task RedeployingOneBackendDoesNotAffectTheOther()
    {
        var webStarted = await StartedAtAsync("bff-web");
        using var running = new CancellationTokenSource();
        var probe = ProbeAsync("web", running.Token);

        await Shell.ComposeAsync("up -d --no-deps --force-recreate --wait bff-mobile");
        await Task.Delay(TimeSpan.FromSeconds(2));
        running.Cancel();
        var webStatuses = await probe;

        Assert.That(webStatuses, Is.Not.Empty);
        Assert.That(webStatuses, Has.All.EqualTo(HttpStatusCode.OK));
        Assert.That(await StartedAtAsync("bff-web"), Is.EqualTo(webStarted), "bff-web no se reinició");
    }

    private static async Task<List<HttpStatusCode>> ProbeAsync(string client, CancellationToken token)
    {
        var statuses = new List<HttpStatusCode>();
        while (!token.IsCancellationRequested)
        {
            using var response = await Api.Http.GetAsync($"{DemoData.Gateway}/{client}/orders/user/{DemoData.User1}");
            statuses.Add(response.StatusCode);
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        return statuses;
    }

    private static async Task<string> StartedAtAsync(string service) =>
        (await Shell.RunAsync($"docker inspect -f '{{{{.State.StartedAt}}}}' $(docker compose ps -q {service})")).Output;
}
