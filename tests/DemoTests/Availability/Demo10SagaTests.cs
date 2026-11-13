using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Availability;

/// <summary>
/// Contiene las pruebas de la demo 10: Products reserva el stock de cada Order,
/// Orders verifica el User y la confirma, y el ERP la factura. Si un paso falla
/// después de la reserva, la compensación devuelve el stock. Las copias de los
/// eventos no lo cambian dos veces.
/// </summary>
public class Demo10SagaTests : DemoTest
{
    private static readonly string[] InvoicedEvents = ["OrderCreated", "OrderConfirmed", "OrderInvoiced"];
    private static readonly string[] CancelledEvents = ["OrderCreated", "OrderConfirmed", "OrderCancelled"];

    private const int Stock = 20;
    private static readonly int Product = DemoData.Shoes.Id;
    private int _initialStock;

    [SetUp]
    public async Task SetStockAsync()
    {
        _initialStock = await Api.StockAsync(Product);
        await Api.SetStockAsync(Product, Stock);
    }

    /// <summary>
    /// Espera a que termine la Saga antes del test siguiente.
    /// </summary>
    /// <remarks>
    /// Si un test termina antes que la Saga, una compensación que llega después
    /// devolvería unidades al stock del test siguiente: se espera a que no quede
    /// ninguna Order <c>OrdersService.Models.OrderStatus.Pending</c>, y a que se publiquen sus eventos.
    /// </remarks>
    [TearDown]
    public async Task RestoreStockAsync()
    {
        await Api.WaitNoPendingAsync();
        await Task.Delay(TimeSpan.FromSeconds(3));
        await Api.SetStockAsync(Product, _initialStock);
    }

    [Test]
    public async Task WithStockTheOrderIsConfirmedAndInvoiced()
    {
        var id = await Api.CreateOrderAsync(DemoData.User2, Product, 2);

        var events = await Eventually.GetAsync(() => Api.EventTypesAsync(id), e => e.Contains("OrderInvoiced"));

        Assert.That(events, Is.EqualTo(InvoicedEvents));
        Assert.That(await Api.StockAsync(Product), Is.EqualTo(Stock - 2));
    }

    [Test]
    public async Task WithoutStockTheOrderIsRejectedAndTheStockDoesNotChange()
    {
        var id = await Api.CreateOrderAsync(DemoData.User2, Product, Stock + 1);

        var statuses = await Api.WaitProcessedAsync([id]);

        Assert.That(statuses[id], Is.EqualTo("Rejected"));
        Assert.That(await RejectionAsync(id), Does.Contain("stock"));
        Assert.That(await Api.StockAsync(Product), Is.EqualTo(Stock));
    }

    [Test]
    public async Task AMissingUserReleasesTheReservedStock()
    {
        var id = await Api.CreateOrderAsync(DemoData.MissingUser, Product, 3);

        var statuses = await Api.WaitProcessedAsync([id]);

        Assert.That(statuses[id], Is.EqualTo("Rejected"));
        Assert.That(await RejectionAsync(id), Does.Contain("no existe"));
        Assert.That(await Eventually.GetAsync(() => Api.StockAsync(Product), s => s == Stock), Is.EqualTo(Stock),
            "la compensación devuelve las 3 unidades");
    }

    [Test]
    public async Task TheErpRejectionCancelsTheOrderAndReleasesTheStock()
    {
        var id = await Api.CreateOrderAsync(DemoData.User2, Product, 12);

        var status = await Eventually.GetAsync(() => Api.OrderStatusAsync(id), s => s == "Cancelled", TimeSpan.FromSeconds(60));

        Assert.That(status, Is.EqualTo("Cancelled"));
        Assert.That(await Api.EventTypesAsync(id), Is.EqualTo(CancelledEvents));
        Assert.That(await Eventually.GetAsync(() => Api.StockAsync(Product), s => s == Stock), Is.EqualTo(Stock));
    }

    [Test]
    public async Task RepeatedEventsChangeTheStockOnce()
    {
        var confirmed = await Api.CreateOrderAsync(DemoData.User2, Product, 2);
        var rejected = await Api.CreateOrderAsync(DemoData.MissingUser, Product, 3);
        await Api.WaitProcessedAsync([confirmed, rejected]);
        await Eventually.GetAsync(() => Api.StockAsync(Product), s => s == Stock - 2);

        await Api.PublishEventAsync("order.created", new { OrderId = confirmed, UserId = DemoData.User2, ProductId = Product, Quantity = 2 });
        await Api.PublishEventAsync("order.rejected", new { OrderId = rejected, Reason = "copia" });
        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.That(await Api.StockAsync(Product), Is.EqualTo(Stock - 2));
        Assert.That(await Api.EventTypesAsync(confirmed), Has.Exactly(1).EqualTo("OrderConfirmed"));
    }

    private static async Task<string> RejectionAsync(int id) =>
        (await Api.EventsAsync(id))["events"]!.AsArray()
            .Single(e => e!["type"]!.GetValue<string>() == "OrderRejected")!["data"]!["Reason"]!.GetValue<string>();
}
