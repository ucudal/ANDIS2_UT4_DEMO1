using System.Net;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Availability;

// Demo 05: el gateway acepta 1 POST /orders/ por segundo por cliente, con una
// ráfaga de 5. Un cliente que regula su ritmo no recibe ningún 429.
public class Demo05RateLimitingTests : DemoTest
{
    [Test]
    public async Task UnregulatedBurstIsRejected()
    {
        await WaitForBurstAsync();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 15; i++)
            statuses.Add(await PostAsync());

        Assert.That(statuses, Has.Some.EqualTo(HttpStatusCode.TooManyRequests));
    }

    [Test]
    public async Task RegulatedClientIsNotRejected()
    {
        await WaitForBurstAsync();
        var statuses = new List<HttpStatusCode>();
        for (var i = 1; i <= 8; i++)
        {
            if (i > 5)
                await Task.Delay(TimeSpan.FromSeconds(1.1));
            statuses.Add(await PostAsync());
        }

        Assert.That(statuses, Has.All.EqualTo(HttpStatusCode.Accepted));
    }

    private static async Task<HttpStatusCode> PostAsync()
    {
        using var response = await Api.PostOrderAsync(DemoData.Gateway, DemoData.User2, DemoData.Shirt.Id);
        return response.StatusCode;
    }

    // Espera a que el gateway vuelva a admitir una ráfaga completa.
    private static Task WaitForBurstAsync() => Task.Delay(TimeSpan.FromSeconds(6));
}
