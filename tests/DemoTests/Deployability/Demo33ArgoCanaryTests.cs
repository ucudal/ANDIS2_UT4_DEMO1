using System.Net;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Deployability;

// Demo 33: Canary con Argo Rollouts. Argo sube el peso de la versión nueva
// mientras el análisis mide pocos errores, y aborta si mide demasiados.
public class Demo33ArgoCanaryTests : KubernetesTest
{
    [TearDown]
    public Task DeleteAsync() => KubernetesAsync("strategy_delete canary");

    [Test]
    public async Task AHealthyVersionIsPromotedAndAFaultyOneAborted()
    {
        await ApplyStrategyAsync("canary");
        Assert.That((await Api.VersionsAsync(DemoData.KubernetesDeploy, 5)).Select(r => r.Version), Has.All.EqualTo("1"));

        Assert.That(await DeployAsync("v2"), Is.EqualTo("Healthy"));
        Assert.That((await Api.VersionsAsync(DemoData.KubernetesDeploy, 10)).Select(r => r.Version), Has.All.EqualTo("2"));

        Assert.That(await DeployAsync("v3-bad"), Is.EqualTo("Degraded"));
        var after = await Api.VersionsAsync(DemoData.KubernetesDeploy, 10);
        Assert.That(Probe.Failures(after), Is.Empty);
        Assert.That(after.Select(r => r.Version), Has.All.EqualTo("2"), "Argo volvió todo a la versión v2");
    }

    // Despliega una versión con tráfico, para que el análisis tenga qué medir,
    // y devuelve la fase en la que termina el Rollout.
    private static async Task<string> DeployAsync(string version)
    {
        await using var probe = await Probe.StartAsync(DemoData.KubernetesDeploy);
        await KubernetesAsync($"rollouts set image users users=users-service:{version}");
        // Hasta que Argo ve la imagen nueva, el Rollout sigue Healthy con la
        // versión anterior: primero se espera a que empiece a desplegar.
        await Eventually.GetAsync(PhaseAsync, p => p != "Healthy", TimeSpan.FromSeconds(60));
        var phase = await WaitPhaseAsync("Healthy", "Degraded", "Paused");
        await Task.Delay(TimeSpan.FromSeconds(3));
        await probe.StopAsync();
        return phase;
    }
}
