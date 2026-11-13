[assembly: NonParallelizable]
[assembly: LevelOfParallelism(1)]

namespace Ucu.Andis.DemoTests.Support;

/// <summary>
/// Proporciona las categorías de los tests.
/// </summary>
/// <remarks>
/// Las demos comparten el estado de la aplicación -el chaos, los patrones, las
/// rutas del gateway-, así que los tests corren de a uno. NUnit los ordena por
/// nombre completo: primero los de disponibilidad, después los de facilidad de
/// despliegue, modificación, rendimiento y seguridad.
///
/// Los tests no levantan los contenedores: hay que hacerlo antes, con <c>docker
/// compose up -d --build --wait</c>. Las demos de Kubernetes necesitan, en
/// cambio, el clúster de <c>./demos/k8s-up.sh</c>.
///
/// Los tests sin categoría corren con <c>docker compose up</c> y modifican
/// datos, pero no los contenedores.
/// </remarks>
public static class TestEnvironment
{
    /// <summary>
    /// Categoría de los tests que detienen, escalan o recrean contenedores.
    /// </summary>
    public const string Disruptive = "Disruptive";

    /// <summary>
    /// Categoría de los tests que levantan el perfil <c>feature-flags</c>.
    /// </summary>
    public const string FeatureFlags = "FeatureFlags";

    /// <summary>
    /// Categoría de los tests que recrean los servicios con
    /// <c>docker-compose.gatekeeper.yaml</c>.
    /// </summary>
    public const string Gatekeeper = "Gatekeeper";

    /// <summary>
    /// Categoría de los tests que necesitan el clúster de <c>./demos/k8s-up.sh</c>.
    /// </summary>
    public const string Kubernetes = "Kubernetes";
}
