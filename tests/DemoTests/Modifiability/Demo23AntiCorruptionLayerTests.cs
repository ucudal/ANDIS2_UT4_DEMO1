using System.Text.Json.Nodes;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Modifiability;

// Demo 23: Purchases ve las Orders con su propio modelo -comprador, unidades y
// si está confirmada-, y un cambio en el modelo de Orders no lo afecta.
public class Demo23AntiCorruptionLayerTests : DemoTest
{
    private static readonly DemoData.Product Product = DemoData.Shoes;

    [TearDown]
    public Task RestoreAsync() => Api.ConfigDeleteAsync("Features/OrderDates");

    [Test]
    public async Task PurchasesHaveTheirOwnModel()
    {
        var id = await Api.CreateOrderAsync(DemoData.User2, Product.Id);
        await Api.WaitProcessedAsync([id]);

        var purchases = await PurchasesAsync();
        var purchase = purchases.Single(p => p!["number"]!.GetValue<int>() == id)!.AsObject();

        Assert.That(purchase.Select(p => p.Key), Is.EquivalentTo(new[] { "number", "buyer", "units", "confirmed" }));
        Assert.That(purchase["buyer"]!.GetValue<string>(), Is.EqualTo(DemoData.User2Name));
        Assert.That(purchase["confirmed"]!.GetValue<bool>(), Is.True);
    }

    [Test]
    public async Task AChangeInOrdersDoesNotChangePurchases()
    {
        // Una Order que se confirma entre las dos lecturas cambiaría la
        // respuesta por otro motivo.
        await Api.WaitNoPendingAsync();
        var before = (await PurchasesAsync()).ToJsonString();

        await Api.ConfigSetAsync("Features/OrderDates", "true");
        var orders = await Api.Http.GetStringAsync($"{DemoData.Gateway}/orders/");
        var after = (await PurchasesAsync()).ToJsonString();

        Assert.That(orders, Does.Contain("created_at"), "Orders agrega la fecha de creación");
        Assert.That(after, Is.EqualTo(before));
    }

    private static async Task<JsonArray> PurchasesAsync() =>
        JsonNode.Parse(await Api.Http.GetStringAsync($"{DemoData.Gateway}/purchases/product/{Product.Id}"))!.AsArray();
}
