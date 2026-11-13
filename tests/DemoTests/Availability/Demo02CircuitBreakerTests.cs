using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Availability;

/// <summary>
/// Contiene las pruebas de la demo 02: con Products fallando siempre, el Circuit
/// Breaker deja de llamarlo y responde de inmediato con una respuesta degradada.
/// </summary>
public class Demo02CircuitBreakerTests : DemoTest
{
    [Test]
    public async Task CircuitBreakerStopsCallingAFailingService()
    {
        await Api.ChaosAsync("products", 1, 0);

        await Api.OnlyPatternAsync(null);
        var (callsWithout, _) = await ConsultAsync(6);

        await Api.OnlyPatternAsync("circuitBreaker");
        var (callsWith, names) = await ConsultAsync(6);

        Assert.That(callsWith, Is.LessThan(callsWithout), "con el circuito abierto, Products tendría que recibir menos llamadas");
        Assert.That(names, Has.Some.Contains("circuito abierto"));
    }

    // Consulta n veces el detalle y devuelve cuántas llamadas recibió Products
    // y los nombres de los Products de la última consulta.
    private static async Task<(double Calls, List<string> Names)> ConsultAsync(int n)
    {
        var before = await Api.ProductsCallsAsync();
        Api.DetailsResponse? details = null;
        for (var i = 0; i < n; i++)
            details = await Api.DetailsAsync(DemoData.User1);
        await Task.Delay(TimeSpan.FromSeconds(1));
        var calls = await Api.ProductsCallsAsync() - before;
        return (calls, details!.Rows.Select(r => r!["product_name"]!.GetValue<string>()).ToList());
    }
}
