using Microsoft.AspNetCore.Mvc;

namespace Ucu.Andis.OrdersService;

/// <summary>
/// Representa quién hace la solicitud.
/// </summary>
/// <remarks>
/// Federated Identity: lo autentica Keycloak, el proveedor de identidad; por la
/// entrada HTTPS, el gateway valida el token y le pasa a Orders sus claims en
/// encabezados -ver <c>ApiGateway/nginxconf/nginx.conf</c>-:
/// <ul>
/// <li><c>X-User-Name</c>: el nombre de usuario en Keycloak, por ejemplo alice</li>
/// <li><c>X-User-Id</c>: el id del User de la aplicación, si el usuario es cliente</li>
/// <li><c>X-User-Roles</c>: los roles, separados por coma, por ejemplo customer o admin</li>
/// </ul>
///
/// Orders no valida tokens ni conoce contraseñas: confía en esos encabezados,
/// porque el gateway los arma con el token y reemplaza los que mande el cliente.
/// Esa confianza depende de que nadie pueda llegar a Orders sin pasar por el
/// gateway -ver Gatekeeper en <c>PATTERNS.md</c>-.
///
/// Por la entrada HTTP, sin los patrones de seguridad, no llegan esos encabezados:
/// la solicitud es anónima y Orders se comporta como en las demos anteriores.
/// </remarks>
public record CallerIdentity(string Name, string? UserId, IReadOnlyList<string> Roles)
{
    public const string NameHeader = "X-User-Name";
    public const string UserIdHeader = "X-User-Id";
    public const string RolesHeader = "X-User-Roles";

    public bool IsAdmin => Roles.Contains("admin");

    /// <summary>
    /// Indica si puede ver las Orders de un User: puede el propio User, o un admin.
    /// </summary>
    public bool CanAccess(string? userId) => IsAdmin || UserId == userId;

    /// <summary>
    /// Obtiene la identidad de la solicitud a partir de sus encabezados.
    /// </summary>
    /// <remarks>
    /// Devuelve <c>null</c> si la solicitud es anónima.
    /// </remarks>
    public static CallerIdentity? From(HttpRequest request)
    {
        var name = request.Headers[NameHeader].ToString();
        if (string.IsNullOrEmpty(name))
            return null;

        var userId = request.Headers[UserIdHeader].ToString();
        var roles = request.Headers[RolesHeader].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
        return new CallerIdentity(name, string.IsNullOrEmpty(userId) ? null : userId, roles);
    }
}

public static class CallerIdentityExtensions
{
    /// <summary>
    /// Responde 403 Forbidden con el motivo, que también queda en el log.
    /// </summary>
    public static ObjectResult Forbidden(this ControllerBase controller, ILogger logger, string detail)
    {
        logger.LogWarning("[FEDERATED IDENTITY] 403: {Detail}", detail);
        return controller.StatusCode(StatusCodes.Status403Forbidden, new { detail });
    }
}
