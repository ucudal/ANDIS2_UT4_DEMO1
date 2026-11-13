using System.Globalization;
using Ucu.Andis.DemoTests.Deployability;
using Ucu.Andis.DemoTests.Support;

namespace Ucu.Andis.DemoTests.EnergyEfficiency;

// La base de los tests de las demos de eficiencia energética, de la 35 a la 37.
// El worker de Orders corre en el clúster y usa los servicios de Docker
// Compose: cada test lo prepara con energy_setup y lo deja como estaba con
// energy_cleanup.
public abstract class EnergyTest : KubernetesTest
{
    [TearDown]
    public Task CleanupAsync() => KubernetesAsync("energy_cleanup");

    protected static async Task<int> ReplicasAsync() =>
        int.Parse((await KubernetesAsync("worker_replicas")).Output);

    protected static Task<int> WaitReplicasAsync(int replicas, TimeSpan? timeout = null) =>
        Eventually.GetAsync(ReplicasAsync, r => r == replicas, timeout ?? TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(2));

    // La potencia estimada del worker, en watts, según Prometheus del clúster.
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
