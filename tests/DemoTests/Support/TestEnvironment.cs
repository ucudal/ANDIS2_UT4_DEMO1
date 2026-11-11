[assembly: NonParallelizable]
[assembly: LevelOfParallelism(1)]

namespace Ucu.Andis.DemoTests.Support;

// Las demos comparten el estado de la aplicación -el chaos, los patrones, las
// rutas del gateway-, así que los tests corren de a uno, en el orden de las
// demos.
//
// Los tests no levantan los contenedores: hay que hacerlo antes, con
// docker compose up -d --build --wait. Las demos de Kubernetes necesitan, en
// cambio, el clúster de ./demos/k8s-up.sh.
public static class TestEnvironment
{
    // Categorías. Los tests sin categoría corren con docker compose up y
    // modifican datos, pero no los contenedores.
    public const string Disruptive = "Disruptive";     // detienen, escalan o recrean contenedores
    public const string FeatureFlags = "FeatureFlags"; // levantan el perfil feature-flags
    public const string Gatekeeper = "Gatekeeper";     // recrean los servicios con docker-compose.gatekeeper.yaml
    public const string Kubernetes = "Kubernetes";     // necesitan el clúster de ./demos/k8s-up.sh
}
