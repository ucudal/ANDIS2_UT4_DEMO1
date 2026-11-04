using Microsoft.AspNetCore.Mvc;

namespace Ucu.Andis.OrdersService;

// Federated Identity: quién hace la solicitud. Lo autentica Keycloak, el
// proveedor de identidad; por la entrada HTTPS, el gateway valida el token y le
// pasa a Orders sus claims en encabezados -ver ApiGateway/nginxconf/nginx.conf-:
//
//   X-User-Name   el nombre de usuario en Keycloak, por ejemplo alice
//   X-User-Id     el id del User de la aplicación, si el usuario es cliente
//   X-User-Roles  los roles, separados por coma, por ejemplo customer o admin
//
// Orders no valida tokens ni conoce contraseñas: confía en esos encabezados,
// porque el gateway los arma con el token y reemplaza los que mande el cliente.
// Esa confianza depende de que nadie pueda llegar a Orders sin pasar por el
// gateway -ver Gatekeeper en PATTERNS.md-.
//
// Por la entrada HTTP, sin los patrones de seguridad, no llegan esos
// encabezados: la solicitud es anónima y Orders se comporta como en las demos
// anteriores.
public record CallerIdentity(string Name, int? UserId, IReadOnlyList<string> Roles)
{
    public const string NameHeader = "X-User-Name";
    public const string UserIdHeader = "X-User-Id";
    public const string RolesHeader = "X-User-Roles";

    public bool IsAdmin => Roles.Contains("admin");

    // Puede ver las Orders de un User el propio User, o un admin.
    public bool CanAccess(int userId) => IsAdmin || UserId == userId;

    // null si la solicitud es anónima.
    public static CallerIdentity? From(HttpRequest request)
    {
        var name = request.Headers[NameHeader].ToString();
        if (string.IsNullOrEmpty(name))
            return null;

        int? userId = int.TryParse(request.Headers[UserIdHeader], out var id) ? id : null;
        var roles = request.Headers[RolesHeader].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries);
        return new CallerIdentity(name, userId, roles);
    }
}

public static class CallerIdentityExtensions
{
    // 403 Forbidden con el motivo, que también queda en el log.
    public static ObjectResult Forbidden(this ControllerBase controller, ILogger logger, string detail)
    {
        logger.LogWarning("[FEDERATED IDENTITY] 403: {Detail}", detail);
        return controller.StatusCode(StatusCodes.Status403Forbidden, new { detail });
    }
}
