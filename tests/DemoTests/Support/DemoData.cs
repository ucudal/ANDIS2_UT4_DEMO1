namespace Ucu.Andis.DemoTests.Support;

// Direcciones y datos de ejemplo que usan las demos. Si cambian los ids de los
// Users o de los Products, se cambian solo acá.
public static class DemoData
{
    public const string Gateway = "http://localhost";
    public const string HttpsGateway = "https://localhost";
    public const string OrdersDirect = "http://localhost:8000";
    public const string ProductsDirect = "http://localhost:8001";
    public const string Keycloak = "http://localhost:8080";
    public const string Consul = "http://localhost:8500";
    public const string Facade = "http://localhost:8090";
    public const string Deploy = "http://localhost:8091";
    public const string KubernetesDeploy = "http://localhost:8092";
    public const string KubernetesPreview = "http://preview.localhost:8092";
    public const string RabbitMqApi = "http://localhost:15672";

    // alice. Sus Orders sostienen las demos 01 a 04: los tests no le crean
    // Orders nuevas.
    public const int User1 = 1;

    // bob. Los tests le crean las Orders que necesitan.
    public const int User2 = 2;

    public const string User2Name = "bob";
    public const string User2Email = "bob@example.com";

    public record Product(string Id, string Name, decimal Price);

    public static readonly Product Shirt = new("60f7c2b5e1d3c2a1b8e4d123", "Camisa", 1200.50m);
    public static readonly Product Trousers = new("60f7c2b5e1d3c2a1b8e4d124", "Pantalón", 2300.00m);
    public static readonly Product Shoes = new("60f7c2b5e1d3c2a1b8e4d125", "Zapatos", 3500.75m);

    // Un User que no existe, para las Orders que se rechazan.
    public const int MissingUser = 999999;
}
