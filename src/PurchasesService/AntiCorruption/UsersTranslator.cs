using System.Text;
using System.Xml.Linq;

namespace Ucu.Andis.PurchasesService.AntiCorruption;

// Traduce el SOAP de Users. Purchases solo necesita el nombre del comprador; el
// sobre, la acción y los espacios de nombres quedan acá.
public class UsersTranslator
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Users = "http://ucu.edu.uy/andis/users";

    private readonly HttpClient _client;

    public UsersTranslator(HttpClient client) => _client = client;

    // El nombre del User, o un texto que lo explica si Users no lo sabe o no responde.
    public async Task<string> NameAsync(string userId, CancellationToken cancellationToken)
    {
        var envelope = new XDocument(
            new XElement(Soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soap", Soap),
                new XElement(Soap + "Body",
                    new XElement(Users + "GetUser",
                        new XElement(Users + "id", userId)))));

        using var request = new HttpRequestMessage(HttpMethod.Post, "")
        {
            Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
        };
        request.Headers.Add("SOAPAction", $"\"{Users.NamespaceName}/IUserService/GetUser\"");

        try
        {
            using var response = await _client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return "no disponible";

            var body = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return (string?)body.Descendants(Users + "GetUserResult").Elements(Users + "Name").SingleOrDefault()
                ?? "desconocido";
        }
        catch (HttpRequestException)
        {
            return "no disponible";
        }
    }
}
