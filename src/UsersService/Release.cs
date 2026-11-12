namespace Ucu.Andis.Users;

// La versión de Users que corre en este contenedor, configurada con
// Release:Version -por variable de entorno, RELEASE__VERSION-. Sin valor, es la
// 1. Las demos de facilidad de despliegue, de la 28 a la 31, publican la
// versión 2 en users-service-v2 y la comparan con la 1, en users-service.
//
// Con Docker Compose es la misma imagen con otra configuración: así no hay que
// construir otra imagen para tener una versión nueva. Las demos de Kubernetes,
// de la 32 a la 34, construyen una imagen por versión, como en producción, con
// el argumento RELEASE_VERSION del Dockerfile.
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
    // Cada respuesta dice qué versión la atendió, en el encabezado X-Version.
    // Así un cliente, un script o k6 pueden contar cuántas respuestas dio cada
    // versión durante un despliegue, sin preguntarle al gateway.
    public static IApplicationBuilder UseReleaseHeader(this IApplicationBuilder app)
    {
        var version = app.ApplicationServices.GetRequiredService<Release>().Version.ToString();
        return app.Use((context, next) =>
        {
            context.Response.Headers[Release.VersionHeader] = version;
            return next();
        });
    }
}
