using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Availability;

// Demo 07: las réplicas de orders-worker se reparten las Orders, y si una se
// detiene, las demás procesan lo que quedó.
public class Demo07CompetingConsumersTests : DemoTest
{
    [Test]
    public async Task WorkersShareTheOrders()
    {
        var ids = await CreateOrdersAsync(9);
        var statuses = await Api.WaitProcessedAsync(ids);

        Assert.That(statuses.Values, Has.All.EqualTo("Confirmed"));
        var workers = await ProcessedByAsync(ids);
        Assert.That(workers.Distinct().Count(), Is.GreaterThan(1), "las Orders tendrían que repartirse entre varias réplicas");
    }

    [Test]
    [Category(TestEnvironment.Disruptive)]
    public async Task OrdersAreProcessedWhenAWorkerStops()
    {
        var worker = (await Shell.ComposeAsync("ps --format '{{.Name}}' orders-worker")).Output.Split('\n')[0];
        var ids = await CreateOrdersAsync(9);
        await Task.Delay(TimeSpan.FromSeconds(1));
        try
        {
            await Shell.RunAsync($"docker stop {worker}");
            var statuses = await Api.WaitProcessedAsync(ids);

            Assert.That(statuses.Values, Has.All.EqualTo("Confirmed"));
        }
        finally
        {
            await Shell.RunAsync($"docker start {worker}");
        }
    }

    private static async Task<List<int>> CreateOrdersAsync(int n)
    {
        var ids = new List<int>();
        for (var i = 0; i < n; i++)
            ids.Add(await Api.CreateOrderAsync(DemoData.User2, DemoData.Shoes.Id));
        return ids;
    }

    // La réplica que procesó cada Order, según su evento OrderConfirmed.
    private static async Task<List<string>> ProcessedByAsync(IEnumerable<int> ids)
    {
        var workers = new List<string>();
        foreach (var id in ids)
        {
            var events = (await Api.EventsAsync(id))["events"]!.AsArray();
            var confirmed = events.Single(e => e!["type"]!.GetValue<string>() == "OrderConfirmed");
            workers.Add(confirmed!["data"]!["ProcessedBy"]!.GetValue<string>());
        }
        return workers;
    }
}
