using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Availability;

/// <summary>
/// Contiene las pruebas de la demo 03: con Products lento, el Bulkhead limita
/// cuántas llamadas le hace Orders a la vez y rechaza las que exceden su cupo.
/// </summary>
public class Demo03BulkheadTests : DemoTest
{
    [Test]
    public async Task BulkheadRejectsCallsBeyondItsQuota()
    {
        await Api.OnlyPatternAsync(null);
        await WarmUpAsync();
        await Api.ChaosAsync("products", 0, 1500);

        var without = await ConcurrentProductNamesAsync(15);

        await Task.Delay(TimeSpan.FromSeconds(2));
        await Api.OnlyPatternAsync("bulkhead");
        var with = await ConcurrentProductNamesAsync(15);

        Assert.That(without, Has.None.Contains("bulkhead lleno"));
        Assert.That(with, Has.Some.Contains("bulkhead lleno"));
    }

    private static async Task<List<string>> ConcurrentProductNamesAsync(int n)
    {
        var responses = await Task.WhenAll(Enumerable.Range(0, n).Select(_ => Api.DetailsAsync(DemoData.User1)));
        return responses.SelectMany(r => r.Rows).Select(r => r!["product_name"]!.GetValue<string>()).ToList();
    }

    /// <summary>
    /// Abre las conexiones antes de la prueba, como <c>warm_up</c> en
    /// <c>demos/common.sh</c>.
    /// </summary>
    internal static async Task WarmUpAsync()
    {
        for (var i = 0; i < 3; i++)
            await Task.WhenAll(Enumerable.Range(0, 15).Select(_ => Api.DetailsAsync(DemoData.User1)));
    }
}
