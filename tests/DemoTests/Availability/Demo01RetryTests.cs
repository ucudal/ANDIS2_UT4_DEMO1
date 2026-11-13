using System.Net;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Availability;

/// <summary>
/// Contiene las pruebas de la demo 01: con Products fallando en el 30% de las
/// solicitudes, el Retry esconde las fallas transitorias.
/// </summary>
public class Demo01RetryTests : DemoTest
{
    [Test]
    public async Task RetryHidesTransientFailures()
    {
        await Api.ChaosAsync("products", 0.3, 0);

        await Api.OnlyPatternAsync(null);
        var withoutRetry = await FailedProductsAsync(8);

        await Api.OnlyPatternAsync("retry");
        var withRetry = await FailedProductsAsync(8);

        Assert.That(withoutRetry, Is.GreaterThan(0), "sin Retry, alguna llamada a Products tendría que fallar");
        Assert.That(withRetry, Is.LessThan(withoutRetry), "con Retry tendría que haber menos fallas que sin él");
    }

    // Cuántos nombres de Products quedaron "no disponible" en n consultas.
    private static async Task<int> FailedProductsAsync(int n)
    {
        var failed = 0;
        for (var i = 0; i < n; i++)
        {
            var details = await Api.DetailsAsync(DemoData.User1);
            Assert.That(details.Status, Is.EqualTo(HttpStatusCode.OK));
            failed += details.Rows.Count(r => r!["product_name"]!.GetValue<string>().StartsWith("no disponible", StringComparison.Ordinal));
        }
        return failed;
    }
}
