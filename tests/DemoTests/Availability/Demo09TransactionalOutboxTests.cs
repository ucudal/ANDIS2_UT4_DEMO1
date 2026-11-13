using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Availability;

/// <summary>
/// Contiene las pruebas de la demo 09: con RabbitMQ detenido, Orders sigue
/// aceptando Orders: los mensajes esperan en el outbox y se publican cuando vuelve.
/// Un mensaje publicado dos veces produce una sola confirmación.
/// </summary>
[Category(TestEnvironment.Disruptive)]
public class Demo09TransactionalOutboxTests : DemoTest
{
    [Test]
    public async Task MessagesWaitInTheOutboxUntilRabbitMqReturns()
    {
        var ids = new List<int>();
        try
        {
            await Shell.ComposeAsync("stop rabbitmq");

            for (var i = 0; i < 5; i++)
                ids.Add(await Api.CreateOrderAsync(DemoData.User2, DemoData.Shirt.Id));

            var health = await Demo06HealthTests.HealthAsync("orders");
            Assert.That(health.Body?["status"]?.GetValue<string>(), Is.EqualTo("Degraded"));
            Assert.That(await PendingAsync(), Is.GreaterThanOrEqualTo(5));
        }
        finally
        {
            await Shell.ComposeAsync("start rabbitmq");
        }

        var statuses = await Api.WaitProcessedAsync(ids, TimeSpan.FromSeconds(180));
        Assert.That(statuses.Values, Has.All.EqualTo("Confirmed"));

        var pending = await Eventually.GetAsync(PendingAsync, p => p == 0);
        Assert.That(pending, Is.EqualTo(0));
    }

    /// <summary>
    /// Verifica que un mensaje duplicado confirme la Order una sola vez.
    /// </summary>
    /// <remarks>
    /// Con Products en pausa, la Order queda <c>OrdersService.Models.OrderStatus.Pending</c>, y las dos copias de
    /// <c>stock.reserved</c> llegan a la vez a dos workers antes que la reserva real.
    /// </remarks>
    [Test]
    public async Task ADuplicatedMessageConfirmsTheOrderOnce()
    {
        int id;
        try
        {
            await Shell.ComposeAsync("pause products-service");
            id = await Api.CreateOrderAsync(DemoData.User2, DemoData.Shirt.Id);
            for (var i = 0; i < 2; i++)
                await Api.PublishEventAsync("stock.reserved", new { OrderId = id, ProductId = DemoData.Shirt.Id, Quantity = 1 });

            var statuses = await Api.WaitProcessedAsync([id]);
            Assert.That(statuses[id], Is.EqualTo("Confirmed"));
        }
        finally
        {
            await Shell.ComposeAsync("unpause products-service");
        }
        await Eventually.GetAsync(PendingAsync, p => p == 0);

        var types = await Api.EventTypesAsync(id);
        Assert.That(types.Count(t => t == "OrderConfirmed"), Is.EqualTo(1));
        var confirmedMessages = await Shell.OrdersSqlAsync(
            $"SELECT COUNT(*) FROM outbox_messages WHERE routing_key = 'order.confirmed' AND JSON_EXTRACT(payload, '$.OrderId') = {id}");
        Assert.That(confirmedMessages, Is.EqualTo("1"));
    }

    private static async Task<int> PendingAsync() =>
        int.Parse(await Shell.OrdersSqlAsync("SELECT COUNT(*) FROM outbox_messages WHERE published_at IS NULL"));
}
