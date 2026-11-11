using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Performance;

// Demo 10: Products guarda en Redis los Products que lee de su base. La primera
// lectura es lenta y las siguientes, rápidas; un cambio borra la clave; y sin
// Redis, Products sigue respondiendo desde la base.
public class Demo10CacheAsideTests : DemoTest
{
    private static readonly DemoData.Product Product = DemoData.Shirt;

    [SetUp]
    public async Task SlowDatabaseAsync()
    {
        using var response = await Api.Http.PutAsJsonAsync($"{DemoData.Gateway}/products/cache",
            new { enabled = true, dbDelayMs = 200 });
        response.EnsureSuccessStatusCode();
        await Shell.ComposeAsync($"exec -T redis redis-cli DEL product:{Product.Id}");
    }

    [TearDown]
    public async Task RestoreAsync()
    {
        await Shell.ComposeAsync("start redis");
        using (await Api.Http.DeleteAsync($"{DemoData.Gateway}/products/cache")) { }
        await Api.UpdateProductAsync(Product.Id, Product.Name, Product.Price);
    }

    [Test]
    public async Task OnlyTheFirstReadGoesToTheDatabase()
    {
        var first = await ReadAsync();
        var second = await ReadAsync();
        var third = await ReadAsync();

        Assert.That(first.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(200)), "un miss lee de la base, que demora 200 ms");
        Assert.That(second.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(200)), "un hit no lee de la base");
        Assert.That(third.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(200)), "un hit no lee de la base");
    }

    [Test]
    public async Task AnUpdateInvalidatesTheKey()
    {
        await ReadAsync();
        await Api.UpdateProductAsync(Product.Id, "Camisa de lino", Product.Price);

        var read = await ReadAsync();

        Assert.That(read.Name, Is.EqualTo("Camisa de lino"));
        Assert.That(read.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(200)), "después del cambio, la lectura es un miss");
    }

    [Test]
    [Category(TestEnvironment.Disruptive)]
    public async Task ProductsAnswersWithoutRedis()
    {
        await Shell.ComposeAsync("stop redis");

        var read = await ReadAsync();

        Assert.That(read.Status, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(read.Name, Is.EqualTo(Product.Name));
    }

    private static async Task<(HttpStatusCode Status, string? Name, TimeSpan Elapsed)> ReadAsync()
    {
        var watch = Stopwatch.StartNew();
        using var response = await Api.Http.GetAsync($"{DemoData.Gateway}/products/{Product.Id}");
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
        watch.Stop();
        return (response.StatusCode, body?["name"]?.GetValue<string>(), watch.Elapsed);
    }
}
