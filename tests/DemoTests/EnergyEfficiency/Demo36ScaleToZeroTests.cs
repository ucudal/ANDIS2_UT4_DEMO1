using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.EnergyEfficiency;

/// <summary>
/// Contiene las pruebas de la demo 36: con la cola vacía, KEDA deja el worker en 0
/// réplicas; cuando llegan Orders, lo enciende, las procesa y lo vuelve a apagar.
/// </summary>
public class Demo36ScaleToZeroTests : EnergyTest
{
    [Test]
    public async Task WorkerScalesFromZeroAndBack()
    {
        await KubernetesAsync("energy_setup queue");
        Assert.That(await WaitReplicasAsync(0), Is.EqualTo(0), "con la cola vacía no hay réplicas");

        var ids = await CreateOrdersAsync(10);
        Assert.That(await Eventually.GetAsync(ReplicasAsync, r => r > 0, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(2)),
            Is.GreaterThan(0), "KEDA enciende el worker cuando hay mensajes");

        var statuses = await Api.WaitProcessedAsync(ids, TimeSpan.FromMinutes(2));
        Assert.That(statuses.Values, Has.None.EqualTo("Pending"));

        Assert.That(await WaitReplicasAsync(0), Is.EqualTo(0), "con la cola vacía, KEDA vuelve a apagar el worker");
    }
}
