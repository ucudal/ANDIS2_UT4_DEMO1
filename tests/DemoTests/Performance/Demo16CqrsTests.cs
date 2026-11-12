using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Performance;

// Demo 16: la consulta de detalle llama a Users y a Products; la de la vista
// lee solo su tabla. Responden lo mismo, pero la vista no depende de la demora
// de los demás, a cambio de consistencia eventual.
public class Demo16CqrsTests : DemoTest
{
    [Test]
    public async Task BothSidesAnswerTheSame()
    {
        var details = await Api.DetailsAsync(DemoData.User1);
        var view = await Api.DetailsAsync(DemoData.User1, "details/view");

        Assert.That(Ids(view), Is.EqualTo(Ids(details)));
        Assert.That(Names(view), Is.EqualTo(Names(details)));
    }

    [Test]
    public async Task TheViewDoesNotWaitForProducts()
    {
        await Api.ChaosAsync("products", 0, 300);

        var details = await Api.DetailsAsync(DemoData.User1);
        var view = await Api.DetailsAsync(DemoData.User1, "details/view");

        Assert.That(details.Elapsed, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(300)));
        Assert.That(view.Elapsed, Is.LessThan(TimeSpan.FromMilliseconds(300)));
    }

    [Test]
    public async Task TheViewIsEventuallyConsistent()
    {
        var id = await Api.CreateOrderAsync(DemoData.User2, DemoData.Shirt.Id);

        var details = await Api.DetailsAsync(DemoData.User2);
        Assert.That(Ids(details), Does.Contain(id), "details lee la tabla orders, que ya tiene la Order");

        var view = await Eventually.GetAsync(() => Api.DetailsAsync(DemoData.User2, "details/view"),
            v => Ids(v).Contains(id), interval: TimeSpan.FromMilliseconds(500));
        Assert.That(Ids(view), Does.Contain(id));
    }

    private static List<int> Ids(Api.DetailsResponse response) =>
        response.Rows.Select(r => r!["order_id"]!.GetValue<int>()).ToList();

    private static List<string?> Names(Api.DetailsResponse response) =>
        response.Rows.Select(r => r!["product_name"]?.GetValue<string>()).ToList();
}
