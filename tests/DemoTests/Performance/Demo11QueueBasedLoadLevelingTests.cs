using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Performance;

// Demo 11: un pico de 30 Orders a la vez se acepta enseguida; la cola lo
// absorbe y los workers la vacían a su ritmo.
public class Demo11QueueBasedLoadLevelingTests : DemoTest
{
    [Test]
    public async Task TheQueueAbsorbsAPeak()
    {
        var watch = Stopwatch.StartNew();
        var responses = await Task.WhenAll(Enumerable.Range(0, 30)
            .Select(_ => Api.PostOrderAsync(DemoData.OrdersDirect, DemoData.User2, DemoData.Trousers.Id)));
        var accepted = watch.Elapsed;

        Assert.That(responses.Select(r => r.StatusCode), Has.All.EqualTo(HttpStatusCode.Accepted));
        Assert.That(accepted, Is.LessThan(TimeSpan.FromSeconds(10)), "Orders acepta el pico sin esperar a procesarlo");

        var ids = new List<int>();
        foreach (var response in responses)
        {
            ids.Add((await response.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<int>());
            response.Dispose();
        }
        var statuses = await Api.WaitProcessedAsync(ids, TimeSpan.FromSeconds(180));

        Assert.That(statuses.Values, Has.All.EqualTo("Confirmed"));
        var (messages, _) = await Eventually.GetAsync(() => Shell.QueueInfoAsync("orders.process"), q => q.Messages == 0);
        Assert.That(messages, Is.EqualTo(0));
    }
}
