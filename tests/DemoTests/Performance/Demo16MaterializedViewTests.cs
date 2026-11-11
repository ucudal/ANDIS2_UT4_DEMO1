using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Performance;

// Demo 16: la vista materializada tiene los nombres copiados, así que responde
// aunque Products falle; los proyectores toleran eventos que llegan a la vez y
// desordenados.
public class Demo16MaterializedViewTests : DemoTest
{
    [TearDown]
    public async Task RestoreAsync()
    {
        // Este TearDown corre antes que el de DemoTest: sin chaos, Products
        // acepta el cambio.
        await Api.ResetChaosAsync();
        await Shell.ComposeAsync("start orders-projector");
        await Api.UpdateProductAsync(DemoData.Shirt.Id, DemoData.Shirt.Name, DemoData.Shirt.Price);
        await Api.UpdateUserAsync(DemoData.User2, DemoData.User2Name, DemoData.User2Email);
    }

    [Test]
    public async Task TheViewHasTheNamesWhenProductsFails()
    {
        await Api.OnlyPatternAsync(null);
        await Api.ChaosAsync("products", 1, 0);

        var details = await Api.DetailsAsync(DemoData.User1);
        var view = await Api.DetailsAsync(DemoData.User1, "details/view");

        Assert.That(details.Rows.Select(r => r!["product_name"]!.GetValue<string>()), Has.All.StartsWith("no disponible"));
        Assert.That(view.Rows.Select(r => r!["product_name"]!.GetValue<string>()), Has.None.StartsWith("no disponible"));
    }

    [Test]
    [Category(TestEnvironment.Disruptive)]
    public async Task StoppedProjectorsCatchUp()
    {
        int id;
        try
        {
            await Shell.ComposeAsync("stop orders-projector");
            await Api.ChaosAsync("users", 0, 1500);
            id = await Api.CreateOrderAsync(DemoData.User2, DemoData.Shirt.Id);
            await Api.WaitProcessedAsync([id]);

            Assert.That(await Demo13PublisherSubscriberTests.ViewRowAsync(id), Is.Null, "con los proyectores detenidos, la vista no tiene la Order");
        }
        finally
        {
            await Shell.ComposeAsync("start orders-projector");
        }

        var row = await Eventually.GetAsync(() => Demo13PublisherSubscriberTests.ViewRowAsync(id),
            r => r?["status"]?.GetValue<string>() == "Confirmed" && r?["customer_name"] is not null, TimeSpan.FromSeconds(90));

        Assert.That(row?["status"]?.GetValue<string>(), Is.EqualTo("Confirmed"), "el upsert no pierde el estado final");
        Assert.That(row?["customer_name"]?.GetValue<string>(), Is.EqualTo(DemoData.User2Name));
    }

    [Test]
    public async Task AnOlderNameDoesNotOverwriteANewerOne()
    {
        var id = await Api.CreateOrderAsync(DemoData.User2, DemoData.Shirt.Id);
        await Eventually.GetAsync(() => Demo13PublisherSubscriberTests.ViewRowAsync(id), r => r?["customer_name"] is not null);

        var now = DateTime.UtcNow;
        await Api.PublishEventAsync("user.updated", new { UserId = DemoData.User2, Name = "Roberto", OccurredAt = now });
        await Task.Delay(TimeSpan.FromSeconds(1));
        await Api.PublishEventAsync("user.updated", new { UserId = DemoData.User2, Name = "Bob", OccurredAt = now.AddSeconds(-10) });
        await Task.Delay(TimeSpan.FromSeconds(2));

        var row = await Demo13PublisherSubscriberTests.ViewRowAsync(id);
        Assert.That(row?["customer_name"]?.GetValue<string>(), Is.EqualTo("Roberto"));
    }
}
