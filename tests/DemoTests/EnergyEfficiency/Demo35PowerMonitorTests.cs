using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.EnergyEfficiency;

/// <summary>
/// Contiene las pruebas de la demo 35: Prometheus estima la potencia de los pods
/// del worker. Con 3 réplicas ociosas, la potencia es al menos la de la CPU
/// reservada: 3 × 0,25 vCPU × 0,78 W.
/// </summary>
public class Demo35PowerMonitorTests : EnergyTest
{
    private const double ReservedWatts = 3 * 0.25 * 0.78;

    [Test]
    public async Task IdleReplicasConsumeTheirReservedCpu()
    {
        await KubernetesAsync("energy_setup");
        Assert.That(await WaitReplicasAsync(3), Is.EqualTo(3));

        var watts = await Eventually.GetAsync(WattsAsync, w => w >= ReservedWatts, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(5));
        Assert.That(watts, Is.GreaterThanOrEqualTo(ReservedWatts).Within(0.01),
            "las 3 réplicas ociosas consumen al menos la CPU que reservan");
    }
}
