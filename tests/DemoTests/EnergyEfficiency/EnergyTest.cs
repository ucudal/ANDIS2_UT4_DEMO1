using System.Globalization;
using Ucu.Andis.DemoTests.Deployability;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.EnergyEfficiency;

/// <summary>
/// Representa la base de los tests de las demos de eficiencia energética, de la 35
/// a la 37.
/// </summary>
/// <remarks>
/// El worker de Orders corre en el clúster y usa los servicios de Docker Compose:
/// cada test lo prepara con <c>energy_setup</c> y lo deja como estaba con
/// <c>energy_cleanup</c>.
/// </remarks>
public abstract class EnergyTest : KubernetesTest
{
    [TearDown]
    public Task CleanupAsync() => KubernetesAsync("energy_cleanup");

    protected static async Task<int> ReplicasAsync() =>
        int.Parse((await KubernetesAsync("worker_replicas")).Output);

    protected static Task<int> WaitReplicasAsync(int replicas, TimeSpan? timeout = null) =>
        Eventually.GetAsync(ReplicasAsync, r => r == replicas, timeout ?? TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(2));

    /// <summary>
    /// Devuelve la potencia estimada del worker, en watts, según Prometheus del
    /// clúster.
    /// </summary>
    protected static async Task<double> WattsAsync() =>
        double.Parse((await KubernetesAsync("worker_watts")).Output, CultureInfo.InvariantCulture);

    protected static async Task<List<int>> CreateOrdersAsync(int n)
    {
        var ids = new List<int>();
        for (var i = 0; i < n; i++)
            ids.Add(await Api.CreateOrderAsync(DemoData.User2, DemoData.Shoes.Id));
        return ids;
    }
}
