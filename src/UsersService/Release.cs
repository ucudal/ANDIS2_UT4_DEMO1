using System.Globalization;

namespace Ucu.Andis.Users;

/// <summary>
/// Representa la versión de Users que corre en este contenedor, configurada con
/// <c>Release:Version</c> -por variable de entorno, <c>RELEASE__VERSION</c>-.
/// </summary>
/// <remarks>
/// Sin valor, es la 1. Las demos de facilidad de despliegue, de la 28 a la 31,
/// publican la versión 2 en <c>users-service-v2</c> y la comparan con la 1, en
/// <c>users-service</c>.
///
/// Con Docker Compose es la misma imagen con otra configuración: así no hay que
/// construir otra imagen para tener una versión nueva. Las demos de Kubernetes, de
/// la 32 a la 34, construyen una imagen por versión, como en producción, con el
/// argumento <c>RELEASE_VERSION</c> del Dockerfile.
/// </remarks>
public class Release
{
    public const string VersionHeader = "X-Version";

    public int Version { get; }

    public Release(IConfiguration configuration)
    {
        Version = configuration.GetValue("Release:Version", 1);
    }
}

public static class ReleaseExtensions
{
    /// <summary>
    /// Agrega a cada respuesta qué versión la atendió, en el encabezado
    /// <c>X-Version</c>.
    /// </summary>
    /// <remarks>
    /// Así un cliente, un script o k6 pueden contar cuántas respuestas dio cada versión
    /// durante un despliegue, sin preguntarle al gateway.
    /// </remarks>
    public static IApplicationBuilder UseReleaseHeader(this IApplicationBuilder app)
    {
        var version = app.ApplicationServices.GetRequiredService<Release>().Version.ToString(CultureInfo.InvariantCulture);
        return app.Use((context, next) =>
        {
            context.Response.Headers[Release.VersionHeader] = version;
            return next();
        });
    }
}
