using System.Net;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Deployability;

// Demo 33: un Deployment de Kubernetes reemplaza los pods de a uno, pero
// despliega igual una versión con errores; un Rollout de Argo, con análisis, la
// detecta y vuelve atrás solo.
public class Demo33KubernetesRollingUpgradeTests : KubernetesTest
{
    [TearDown]
    public Task DeleteAsync() => KubernetesAsync(
        "kubectl -n andis delete deployment users --ignore-not-found --wait=false; strategy_delete rolling-upgrade");

    [Test]
    public async Task ADeploymentReplacesPodsWithoutErrors()
    {
        await ApplyDeploymentAsync();

        List<Api.UserResponse> during;
        await using (var probe = await Probe.StartAsync(DemoData.KubernetesDeploy))
        {
            await KubernetesAsync("kubectl -n andis set image deployment/users users=users-service:v2; " +
                "kubectl -n andis rollout status deployment/users --timeout=180s");
            during = await probe.StopAsync();
        }

        Assert.That(Probe.Failures(during), Is.Empty);
        Assert.That((await Api.VersionsAsync(DemoData.KubernetesDeploy, 10)).Select(r => r.Version), Has.All.EqualTo("2"));
    }

    [Test]
    public async Task ADeploymentDeploysAFaultyVersionEverywhere()
    {
        await ApplyDeploymentAsync();

        await KubernetesAsync("kubectl -n andis set image deployment/users users=users-service:v3-bad; " +
            "kubectl -n andis rollout status deployment/users --timeout=180s");
        var responses = await Api.VersionsAsync(DemoData.KubernetesDeploy, 30);

        Assert.That(responses.Select(r => r.Status), Has.Some.Not.EqualTo(HttpStatusCode.OK),
            "Kubernetes no mide errores: la versión con fallas llega a todos los pods");
    }

    [Test]
    public async Task AnArgoAnalysisRollsBackAFaultyVersion()
    {
        await KubernetesAsync("kubectl apply -f k8s/rolling-upgrade/service.yaml -f k8s/rolling-upgrade/rollout.yaml; " +
            "rollouts status users --timeout 180s");
        await KubernetesAsync("rollouts set image users users=users-service:v2; rollouts status users --timeout 180s");

        await using (var probe = await Probe.StartAsync(DemoData.KubernetesDeploy))
        {
            await KubernetesAsync("rollouts set image users users=users-service:v3-bad");
            Assert.That(await WaitPhaseAsync("Degraded"), Is.EqualTo("Degraded"), "el análisis falla y Argo aborta");
            await probe.StopAsync();
        }

        await Task.Delay(TimeSpan.FromSeconds(6));
        var after = await Api.VersionsAsync(DemoData.KubernetesDeploy, 10);
        Assert.That(Probe.Failures(after), Is.Empty);
        Assert.That(after.Select(r => r.Version), Has.All.EqualTo("2"), "Argo volvió a la versión v2");
    }

    private static async Task ApplyDeploymentAsync()
    {
        await KubernetesAsync("kubectl apply -f k8s/rolling-upgrade/service.yaml -f k8s/rolling-upgrade/deployment.yaml; " +
            "kubectl -n andis rollout status deployment/users --timeout=180s");
        await Eventually.GetAsync(() => Api.GetUserAsync(DemoData.KubernetesDeploy, DemoData.User1),
            r => r.Status == HttpStatusCode.OK, TimeSpan.FromMinutes(2));
    }
}
