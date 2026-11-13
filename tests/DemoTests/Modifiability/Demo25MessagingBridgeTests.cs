using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Modifiability;

/// <summary>
/// Contiene las pruebas de la demo 25: una Order confirmada llega al ERP por el
/// puente entre RabbitMQ y ActiveMQ, y su factura vuelve. Si el broker del ERP no
/// está, el pedido espera y nada se pierde.
/// </summary>
public class Demo25MessagingBridgeTests : DemoTest
{
    private static readonly string[] InvoicedEvents = ["OrderCreated", "OrderConfirmed", "OrderInvoiced"];

    [TearDown]
    public Task RestoreAsync() => Shell.ComposeAsync("start legacy-broker");

    [Test]
    public async Task TheInvoiceComesBackFromTheErp()
    {
        var id = await Api.CreateOrderAsync(DemoData.User2, DemoData.Trousers.Id);
        await Api.WaitProcessedAsync([id]);

        var types = await Eventually.GetAsync(() => Api.EventTypesAsync(id), t => t.Contains("OrderInvoiced"));

        Assert.That(types, Is.EqualTo(InvoicedEvents));
    }

    [Test]
    [Category(TestEnvironment.Disruptive)]
    public async Task NothingIsLostWhileTheErpBrokerIsDown()
    {
        await Shell.ComposeAsync("stop legacy-broker");
        var id = await Api.CreateOrderAsync(DemoData.User2, DemoData.Trousers.Id);
        var statuses = await Api.WaitProcessedAsync([id]);
        await Task.Delay(TimeSpan.FromSeconds(2));

        Assert.That(statuses[id], Is.EqualTo("Confirmed"), "la aplicación no depende del ERP");
        Assert.That(await Api.EventTypesAsync(id), Does.Not.Contain("OrderInvoiced"));

        await Shell.ComposeAsync("start legacy-broker");
        var types = await Eventually.GetAsync(() => Api.EventTypesAsync(id), t => t.Contains("OrderInvoiced"), TimeSpan.FromSeconds(90));

        Assert.That(types.Count(t => t == "OrderInvoiced"), Is.EqualTo(1));
    }
}
