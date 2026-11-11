using System.Net;
using System.Text.Json.Nodes;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Availability;

// Demo 08: los eventos de una Order son la fuente de verdad; la tabla orders se
// borra y se reconstruye a partir de ellos.
public class Demo08EventSourcingTests : DemoTest
{
    [Test]
    public async Task EventsReplayToTheCurrentState()
    {
        var id = await Api.CreateOrderAsync(DemoData.User2, DemoData.Trousers.Id, 3);
        await Api.WaitProcessedAsync([id]);

        var events = await Api.EventsAsync(id);
        var types = events["events"]!.AsArray().Select(e => e!["type"]!.GetValue<string>());

        Assert.That(types, Is.EqualTo(new[] { "OrderCreated", "OrderConfirmed" }));
        Assert.That(events["replayed_state"]!["status"]!.GetValue<string>(), Is.EqualTo("Confirmed"));
        Assert.That(events["replayed_state"]!["quantity"]!.GetValue<int>(), Is.EqualTo(3));
    }

    [Test]
    public async Task ProjectionIsRebuiltFromTheEvents()
    {
        var id = await Api.CreateOrderAsync(DemoData.User2, DemoData.Trousers.Id, 3);
        await Api.WaitNoPendingAsync();
        var before = await Api.Http.GetStringAsync($"{DemoData.Gateway}/orders/");

        await Shell.OrdersSqlAsync("DELETE FROM orders");
        var empty = JsonNode.Parse(await Api.Http.GetStringAsync($"{DemoData.Gateway}/orders/"))!.AsArray();
        Assert.That(empty, Is.Empty);

        using var rebuild = await Api.Http.PostAsync($"{DemoData.Gateway}/orders/projections/rebuild", null);
        Assert.That(rebuild.StatusCode, Is.EqualTo(HttpStatusCode.OK));

        var after = await Api.Http.GetStringAsync($"{DemoData.Gateway}/orders/");
        Assert.That(JsonNode.DeepEquals(JsonNode.Parse(after), JsonNode.Parse(before)), Is.True,
            "la tabla reconstruida tendría que ser igual a la original");
        Assert.That(await Api.OrderStatusAsync(id), Is.EqualTo("Confirmed"));
    }
}
