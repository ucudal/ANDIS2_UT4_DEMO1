using System.Net;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Availability;

// Demo 04: Orders atiende como máximo 20 consultas de detalle a la vez; las
// demás reciben 503 con Retry-After.
public class Demo04ThrottlingTests : DemoTest
{
    [Test]
    public async Task ThrottlingRejectsRequestsBeyondTheLimit()
    {
        await Api.OnlyPatternAsync(null);
        await Demo03BulkheadTests.WarmUpAsync();

        var without = await ConcurrentStatusesAsync(40);

        await Api.OnlyPatternAsync("throttling");
        var with = await ConcurrentStatusesAsync(40);

        Assert.That(without, Has.All.EqualTo(HttpStatusCode.OK));
        Assert.That(with.Count(s => s == HttpStatusCode.ServiceUnavailable), Is.GreaterThan(0));
        Assert.That(with.Count(s => s == HttpStatusCode.OK), Is.LessThanOrEqualTo(20));
    }

    [Test]
    public async Task RejectedRequestsIncludeRetryAfter()
    {
        await Api.OnlyPatternAsync("throttling");
        await Api.ChaosAsync("orders", 0, 5000);

        var responses = await Task.WhenAll(Enumerable.Range(0, 40)
            .Select(_ => Api.Http.GetAsync($"{DemoData.Gateway}/orders/user/{DemoData.User1}/details")));
        var rejected = responses.Where(r => r.StatusCode == HttpStatusCode.ServiceUnavailable).ToList();

        Assert.That(rejected, Is.Not.Empty);
        Assert.That(rejected.Select(r => r.Headers.RetryAfter), Has.All.Not.Null);
        foreach (var response in responses)
            response.Dispose();
    }

    // Con Orders demorando 5 segundos, lanza n consultas simultáneas.
    private static async Task<List<HttpStatusCode>> ConcurrentStatusesAsync(int n)
    {
        await Api.ChaosAsync("orders", 0, 5000);
        var responses = await Task.WhenAll(Enumerable.Range(0, n).Select(_ => Api.DetailsAsync(DemoData.User1)));
        await Api.ResetChaosAsync();
        return responses.Select(r => r.Status).ToList();
    }
}
