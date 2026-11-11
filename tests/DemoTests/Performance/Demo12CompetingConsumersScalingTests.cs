using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Performance;

// Demo 12: con el doble de réplicas de orders-worker, las mismas Orders se
// procesan en menos tiempo, sin cambiar el código.
[Category(TestEnvironment.Disruptive)]
public class Demo12CompetingConsumersScalingTests : DemoTest
{
    [TearDown]
    public Task ScaleBackAsync() =>
        Shell.ComposeAsync("up -d --scale orders-worker=3 --wait orders-worker");

    [Test]
    public async Task MoreWorkersProcessFaster()
    {
        var withThree = await BurstAsync(30);

        await Shell.ComposeAsync("up -d --scale orders-worker=6 --wait orders-worker");
        var (_, consumers) = await Eventually.GetAsync(() => Shell.QueueInfoAsync("orders.process"), q => q.Consumers == 6);
        Assert.That(consumers, Is.EqualTo(6));

        var withSix = await BurstAsync(30);

        Assert.That(withSix, Is.LessThan(withThree), "con 6 réplicas, las Orders tendrían que procesarse en menos tiempo");
    }

    private static async Task<TimeSpan> BurstAsync(int n)
    {
        var watch = Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(0, n)
            .Select(_ => Api.PostOrderAsync(DemoData.OrdersDirect, DemoData.User2, DemoData.Shoes.Id)));
        var ids = new List<int>();
        foreach (var response in responses)
        {
            ids.Add((await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<int>());
            response.Dispose();
        }
        await Api.WaitProcessedAsync(ids, TimeSpan.FromSeconds(180));
        return watch.Elapsed;
    }
}
