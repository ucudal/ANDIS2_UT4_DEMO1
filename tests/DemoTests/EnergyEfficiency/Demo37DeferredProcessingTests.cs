using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.EnergyEfficiency;

/// <summary>
/// Contiene las pruebas de la demo 37: fuera de la ventana, el worker está apagado
/// y las Orders esperan en la cola; cuando se abre la ventana, se procesan.
/// </summary>
public class Demo37DeferredProcessingTests : EnergyTest
{
    [Test]
    public async Task OrdersWaitUntilTheWindowOpens()
    {
        await KubernetesAsync("energy_setup window; window_set closed; wait_replicas 0");
        Assert.That(await WaitReplicasAsync(0), Is.EqualTo(0), "fuera de la ventana no hay réplicas");

        var ids = await CreateOrdersAsync(3);
        await Task.Delay(TimeSpan.FromSeconds(5));
        foreach (var id in ids)
            Assert.That(await Api.OrderStatusAsync(id), Is.EqualTo("Pending"), "fuera de la ventana, nadie procesa las Orders");

        await KubernetesAsync("window_set open");
        var statuses = await Api.WaitProcessedAsync(ids, TimeSpan.FromMinutes(2));
        Assert.That(statuses.Values, Has.None.EqualTo("Pending"), "dentro de la ventana, el worker procesa las Orders");
    }
}
