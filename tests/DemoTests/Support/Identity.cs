using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace Ucu.Andis.DemoTests.Support;

/// <summary>
/// Proporciona la entrada HTTPS del gateway y los tokens de Keycloak, como
/// <c>load_gateway_cert</c>, <c>token</c> y <c>tamper</c> en
/// <c>demos/common.sh</c>.
/// </summary>
public static class Identity
{
    private static HttpClient? _https;

    /// <summary>
    /// Crea un cliente que confía solo en el certificado autofirmado del gateway.
    /// </summary>
    /// <remarks>
    /// Lo lee del contenedor, como hacen las demos, en lugar de aceptar cualquiera.
    /// </remarks>
    public static async Task<HttpClient> HttpsAsync()
    {
        if (_https is not null)
            return _https;
        var pem = (await Shell.RunAsync("docker compose exec -T api-gateway cat /etc/nginx/tls/gateway.crt")).Output;
        var gatewayCert = X509Certificate2.CreateFromPem(pem);
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert is not null && cert.RawData.AsSpan().SequenceEqual(gatewayCert.RawData),
        };
        return _https = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Inicia sesión en Keycloak con el usuario y la contraseña, y devuelve el token de
    /// acceso.
    /// </summary>
    /// <remarks>
    /// Los usuarios son alice, bob y carol.
    /// </remarks>
    public static async Task<string> TokenAsync(string user)
    {
        using var response = await Api.Http.PostAsync($"{DemoData.Keycloak}/realms/andis/protocol/openid-connect/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "andis-cli",
                ["scope"] = "openid",
                ["username"] = user,
                ["password"] = "Pa55w0rd",
            }));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["access_token"]!.GetValue<string>();
    }

    public static JsonObject Claims(string token) =>
        JsonNode.Parse(Base64UrlDecode(token.Split('.')[1]))!.AsObject();

    /// <summary>
    /// Cambia los claims del token sin volver a firmarlo, como haría alguien que quiere
    /// hacerse pasar por otro.
    /// </summary>
    public static string Tamper(string token, Action<JsonObject> change)
    {
        var parts = token.Split('.');
        var claims = Claims(token);
        change(claims);
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(claims.ToJsonString()))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{parts[0]}.{payload}.{parts[2]}";
    }

    public static HttpRequestMessage Request(HttpMethod method, string url, string? token, object? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null)
            request.Headers.Authorization = new("Bearer", token);
        if (json is not null)
            request.Content = JsonContent.Create(json);
        return request;
    }

    private static string Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }
}
