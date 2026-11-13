using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Availability;

/// <summary>
/// Contiene las pruebas de la demo 06: el endpoint de salud de Products refleja el
/// estado de su base.
/// </summary>
[Category(TestEnvironment.Disruptive)]
public class Demo06HealthTests : DemoTest
{
    [Test]
    public async Task HealthReflectsTheDatabase()
    {
        var healthy = await HealthAsync("products");
        Assert.That(healthy.Status, Is.EqualTo(HttpStatusCode.OK));

        try
        {
            await Shell.ComposeAsync("stop products-db");
            var down = await Eventually.GetAsync(() => HealthAsync("products"),
                h => h.Status == HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(30));

            Assert.That(down.Status, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
            Assert.That(down.Body?["status"]?.GetValue<string>(), Is.EqualTo("Unhealthy"));
        }
        finally
        {
            await Shell.ComposeAsync("start products-db");
        }

        var back = await Eventually.GetAsync(() => HealthAsync("products"),
            h => h.Status == HttpStatusCode.OK, TimeSpan.FromSeconds(90));
        Assert.That(back.Status, Is.EqualTo(HttpStatusCode.OK));
    }

    internal static async Task<(HttpStatusCode Status, JsonObject? Body)> HealthAsync(string service)
    {
        using var response = await Api.Http.GetAsync($"{DemoData.Gateway}/health/{service}");
        JsonObject? body = null;
        try
        {
            body = await response.Content.ReadFromJsonAsync<JsonObject>();
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return (response.StatusCode, body);
    }
}
