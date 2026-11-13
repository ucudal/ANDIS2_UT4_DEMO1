using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Deployability;

/// <summary>
/// Contiene las pruebas de la demo 32: Blue/Green con Argo Rollouts. La versión v2
/// se despliega al lado, se prueba por el Service de preview, y promote y undo
/// conmutan sin errores.
/// </summary>
public class Demo32ArgoBlueGreenTests : KubernetesTest
{
    [TearDown]
    public Task DeleteAsync() => KubernetesAsync("strategy_delete blue-green");

    [Test]
    public async Task PromoteAndUndoSwitchWithoutErrors()
    {
        await ApplyStrategyAsync("blue-green");
        Assert.That((await Api.VersionsAsync(DemoData.KubernetesDeploy, 5)).Select(r => r.Version), Has.All.EqualTo("1"));

        await KubernetesAsync("rollouts set image users users=users-service:v2");
        Assert.That(await WaitPhaseAsync("Paused"), Is.EqualTo("Paused"));
        Assert.That((await Api.VersionsAsync(DemoData.KubernetesDeploy, 5)).Select(r => r.Version), Has.All.EqualTo("1"),
            "los clientes siguen en la versión v1");
        Assert.That((await Api.GetUserAsync(DemoData.KubernetesPreview, DemoData.User1)).Version, Is.EqualTo("2"),
            "la versión v2 se prueba por el Service de preview");

        var promote = await WhileProbingAsync("rollouts promote users");
        Assert.That(Probe.Failures(promote), Is.Empty);
        Assert.That((await Api.VersionsAsync(DemoData.KubernetesDeploy, 5)).Select(r => r.Version), Has.All.EqualTo("2"));

        var undo = await WhileProbingAsync("rollouts undo users");
        Assert.That(Probe.Failures(undo), Is.Empty);
        Assert.That((await Api.VersionsAsync(DemoData.KubernetesDeploy, 5)).Select(r => r.Version), Has.All.EqualTo("1"));
    }

    private static async Task<List<Api.UserResponse>> WhileProbingAsync(string command)
    {
        await using var probe = await Probe.StartAsync(DemoData.KubernetesDeploy);
        await KubernetesAsync(command);
        await Task.Delay(TimeSpan.FromSeconds(2));
        return await probe.StopAsync();
    }
}
