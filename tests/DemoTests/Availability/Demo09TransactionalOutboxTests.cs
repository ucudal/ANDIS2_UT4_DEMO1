using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Availability;

// Demo 09: con RabbitMQ detenido, Orders sigue aceptando Orders: los mensajes
// esperan en el outbox y se publican cuando vuelve. Un mensaje publicado dos
// veces produce una sola confirmación.
[Category(TestEnvironment.Disruptive)]
public class Demo09TransactionalOutboxTests : DemoTest
{
    [Test]
    public async Task MessagesWaitInTheOutboxAndArePublishedOnce()
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

            // El relay publica dos veces el order.created de la última Order.
            await Shell.OrdersSqlAsync(
                "INSERT INTO outbox_messages (routing_key, payload, created_at) " +
                "SELECT routing_key, payload, NOW(6) FROM outbox_messages " +
                $"WHERE routing_key = 'order.created' AND published_at IS NULL AND JSON_EXTRACT(payload, '$.OrderId') = {ids[^1]}");
        }
        finally
        {
            await Shell.ComposeAsync("start rabbitmq");
        }

        var statuses = await Api.WaitProcessedAsync(ids, TimeSpan.FromSeconds(180));
        Assert.That(statuses.Values, Has.All.EqualTo("Confirmed"));

        var pending = await Eventually.GetAsync(PendingAsync, p => p == 0);
        Assert.That(pending, Is.EqualTo(0));

        var types = await Api.EventTypesAsync(ids[^1]);
        Assert.That(types.Count(t => t == "OrderConfirmed"), Is.EqualTo(1));
        var confirmedMessages = await Shell.OrdersSqlAsync(
            $"SELECT COUNT(*) FROM outbox_messages WHERE routing_key = 'order.confirmed' AND JSON_EXTRACT(payload, '$.OrderId') = {ids[^1]}");
        Assert.That(confirmedMessages, Is.EqualTo("1"));
    }

    private static async Task<int> PendingAsync() =>
        int.Parse(await Shell.OrdersSqlAsync("SELECT COUNT(*) FROM outbox_messages WHERE published_at IS NULL"));
}
