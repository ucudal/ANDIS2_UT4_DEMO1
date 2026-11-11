using System.Text.Json.Nodes;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Performance;

// Demo 13: cada suscriptor tiene su propia cola en el exchange andis.events y
// recibe solo los eventos a los que se suscribió.
public class Demo13PublisherSubscriberTests : DemoTest
{
    [TearDown]
    public async Task RestoreAsync()
    {
        await Api.UpdateProductAsync(DemoData.Shoes.Id, DemoData.Shoes.Name, DemoData.Shoes.Price);
        await Api.UpdateUserAsync(DemoData.User2, DemoData.User2Name, DemoData.User2Email);
    }

    [Test]
    public async Task EachQueueReceivesItsEvents()
    {
        var bindings = await Api.RabbitMqAsync("exchanges/%2F/andis.events/bindings/source");
        var keys = bindings
            .GroupBy(b => b!["destination"]!.GetValue<string>())
            .ToDictionary(g => g.Key, g => g.Select(b => b!["routing_key"]!.GetValue<string>()).ToList());

        Assert.That(keys["orders.process"], Is.EquivalentTo(new[] { "order.created" }));
        // Los proyectores reciben todos los eventos de las Orders, con un comodín.
        Assert.That(keys["orders.details-view"], Is.EquivalentTo(new[] { "order.*", "product.updated", "user.updated" }));
    }

    [Test]
    public async Task ProjectorsReceiveProductAndUserEvents()
    {
        var id = await Api.CreateOrderAsync(DemoData.User2, DemoData.Shoes.Id);
        await Api.WaitProcessedAsync([id]);

        await Api.UpdateProductAsync(DemoData.Shoes.Id, "Zapatillas", DemoData.Shoes.Price);
        await Api.UpdateUserAsync(DemoData.User2, "roberto", DemoData.User2Email);

        var row = await Eventually.GetAsync(() => ViewRowAsync(id),
            r => r?["product_name"]?.GetValue<string>() == "Zapatillas" && r?["customer_name"]?.GetValue<string>() == "roberto");

        Assert.That(row?["product_name"]?.GetValue<string>(), Is.EqualTo("Zapatillas"));
        Assert.That(row?["customer_name"]?.GetValue<string>(), Is.EqualTo("roberto"));
    }

    internal static async Task<JsonNode?> ViewRowAsync(int orderId)
    {
        var view = await Api.DetailsAsync(DemoData.User2, "details/view");
        return view.Rows.FirstOrDefault(r => r!["order_id"]!.GetValue<int>() == orderId);
    }
}
