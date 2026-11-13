using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Deployability;

/// <summary>
/// Contiene las pruebas de la demo 29: un grupo de tres instancias pasa de la
/// versión v1 a la v2 de a una, y el servicio responde durante todo el reemplazo.
/// </summary>
[Category(TestEnvironment.Disruptive)]
public class Demo29RollingUpgradeTests : DemoTest
{
    [TearDown]
    public Task CleanupAsync() =>
        Shell.DemoAsync("deploy_target users-service:8000; pool_cleanup", TimeSpan.FromMinutes(5));

    [Test]
    public async Task TheServiceAnswersDuringTheUpgrade()
    {
        await Shell.DemoAsync("pool_start_v1; deploy_target users-pool:8000", TimeSpan.FromMinutes(5));
        Assert.That((await Api.VersionsAsync(DemoData.Deploy, 6)).Select(r => r.Version), Has.All.EqualTo("1"));

        List<Api.UserResponse> during;
        await using (var probe = await Probe.StartAsync(DemoData.Deploy))
        {
            for (var n = 1; n <= 3; n++)
            {
                await Shell.DemoAsync($"pool_replace {n}", TimeSpan.FromMinutes(5));
                await Task.Delay(TimeSpan.FromSeconds(6));
            }
            during = await probe.StopAsync();
        }

        Assert.That(Probe.Failures(during), Is.Empty);
        Assert.That(during.Select(r => r.Version), Does.Contain("1").And.Contain("2"), "las dos versiones respondieron a la vez");
        Assert.That((await Api.VersionsAsync(DemoData.Deploy, 6)).Select(r => r.Version), Has.All.EqualTo("2"));
    }
}
