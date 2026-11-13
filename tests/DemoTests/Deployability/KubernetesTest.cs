using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.Deployability;

/// <summary>
/// Representa la base de los tests de las demos que corren en Kubernetes.
/// </summary>
/// <remarks>
/// Si el clúster de <c>./demos/k8s-up.sh</c> no está, los tests se ignoran en lugar
/// de fallar.
/// </remarks>
[Category(TestEnvironment.Kubernetes)]
public abstract class KubernetesTest
{
    [OneTimeSetUp]
    public async Task RequireClusterAsync()
    {
        var context = await Shell.RunAsync("kubectl config use-context kind-andis");
        if (context.ExitCode != 0)
            Assert.Ignore("No está el clúster: hay que crearlo con ./demos/k8s-up.sh");
    }

    protected static Task<Shell.Result> KubernetesAsync(string commands) =>
        Shell.KubernetesAsync(commands, TimeSpan.FromMinutes(10));

    /// <summary>
    /// Aplica una estrategia de <c>k8s/</c> y espera a que la versión v1 responda de
    /// forma estable.
    /// </summary>
    /// <remarks>
    /// Los pods de un test anterior pueden seguir terminando.
    /// </remarks>
    protected static async Task ApplyStrategyAsync(string strategy)
    {
        await KubernetesAsync($"strategy_apply {strategy}");
        await Eventually.GetAsync(() => Api.VersionsAsync(DemoData.KubernetesDeploy, 5),
            responses => responses.All(r => r.Version == "1"), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(2));
    }

    protected static async Task<string> PhaseAsync() =>
        (await Shell.RunAsync("kubectl -n andis get rollout users -o jsonpath='{.status.phase}'")).Output;

    protected static Task<string> WaitPhaseAsync(params string[] phases) =>
        Eventually.GetAsync(PhaseAsync, phases.Contains, TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2));
}
