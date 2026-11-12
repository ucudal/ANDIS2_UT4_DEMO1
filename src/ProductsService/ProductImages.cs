using System.Diagnostics.Metrics;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;

namespace ProductsService;

// Valet Key: las imágenes de los Products se guardan en un almacenamiento de
// blobs -Azurite, el emulador de Azure Storage-. Products tiene la clave de la
// cuenta, pero no la entrega: le da al cliente una URL firmada -una SAS, Shared
// Access Signature- que permite una sola operación, sobre un solo blob, durante
// 2 minutos como máximo. Con esa URL, el cliente sube o baja la imagen
// directamente del almacenamiento, y los bytes no pasan por Products ni por el
// gateway.
//
// Para comparar, Products también acepta la imagen él mismo y la sube con su
// clave: es la forma sin Valet Key.
public class ProductImages
{
    public const string MeterName = "Ucu.Andis.ProductImages";
    public static readonly TimeSpan MaxKeyLifetime = TimeSpan.FromMinutes(2);

    private const string ContainerName = "product-images";

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> ReceivedBytes = Meter.CreateCounter<long>("products.images.received", "By",
        "Bytes de imágenes que recibió Products para subirlas él mismo, sin Valet Key");
    private static readonly Counter<long> IssuedKeys = Meter.CreateCounter<long>("products.images.valet_keys", "{key}",
        "Claves de acceso a las imágenes que entregó Products, por permiso: write o read");

    private readonly BlobContainerClient _container;
    private readonly string _internalEndpoint;
    private readonly string _publicEndpoint;
    private readonly ILogger<ProductImages> _logger;
    private bool _containerExists;

    public ProductImages(IConfiguration config, ILogger<ProductImages> logger)
    {
        // Sin configuración, por ejemplo con dotnet run fuera de Docker, se usa
        // Azurite en localhost:10000, con su cuenta y su clave de desarrollo.
        var service = new BlobServiceClient(config["Storage:ConnectionString"] ?? "UseDevelopmentStorage=true");
        _container = service.GetBlobContainerClient(ContainerName);
        // Products llega a Azurite por la red de Docker, como azurite:10000; el
        // cliente, como localhost:10000. La firma no incluye el host: se
        // cambia la dirección de la URL y la firma sigue siendo válida.
        _internalEndpoint = service.Uri.ToString().TrimEnd('/');
        _publicEndpoint = (config["Storage:PublicBlobEndpoint"] ?? _internalEndpoint).TrimEnd('/');
        _logger = logger;
    }

    // Las métricas arrancan en 0: si aparecieran recién con la primera imagen,
    // Prometheus no vería ese primer aumento. Se llama cuando ya arrancó la
    // aplicación: antes, OpenTelemetry todavía no registra las mediciones.
    public static void StartMetrics()
    {
        ReceivedBytes.Add(0);
        IssuedKeys.Add(0, new KeyValuePair<string, object?>("permission", "write"));
        IssuedKeys.Add(0, new KeyValuePair<string, object?>("permission", "read"));
    }

    // Sin Valet Key: Products recibe todos los bytes de la imagen y los sube
    // él mismo. Los guarda en memoria mientras tanto.
    public async Task<long> UploadAsync(int productId, Stream content, string contentType, CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        await _container.GetBlobClient(productId.ToString()).UploadAsync(buffer,
            new Azure.Storage.Blobs.Models.BlobUploadOptions
            {
                HttpHeaders = new Azure.Storage.Blobs.Models.BlobHttpHeaders { ContentType = contentType }
            }, cancellationToken);

        ReceivedBytes.Add(buffer.Length);
        _logger.LogInformation("[VALET KEY] Sin Valet Key: Products recibió {Bytes} bytes de la imagen del Product {ProductId} y los subió al almacenamiento",
            buffer.Length, productId);
        return buffer.Length;
    }

    // Con Valet Key: una URL para crear o reemplazar la imagen del Product, y
    // nada más. No sirve para leerla, ni para otro blob, ni después de vencer.
    // El cliente puede pedir que venza antes de los 2 minutos, nunca después.
    public async Task<(Uri Url, DateTimeOffset ExpiresOn)> GetUploadKeyAsync(int productId, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);
        return IssueKey(productId, BlobSasPermissions.Create | BlobSasPermissions.Write, "write", lifetime);
    }

    // Con Valet Key: una URL para leer la imagen del Product, o null si no tiene.
    public async Task<(Uri Url, DateTimeOffset ExpiresOn)?> GetDownloadKeyAsync(int productId, CancellationToken cancellationToken)
    {
        await EnsureContainerAsync(cancellationToken);
        if (!await _container.GetBlobClient(productId.ToString()).ExistsAsync(cancellationToken))
            return null;
        return IssueKey(productId, BlobSasPermissions.Read, "read", MaxKeyLifetime);
    }

    private (Uri Url, DateTimeOffset ExpiresOn) IssueKey(int productId, BlobSasPermissions permissions, string permission, TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > MaxKeyLifetime)
            lifetime = MaxKeyLifetime;
        var expiresOn = DateTimeOffset.UtcNow.Add(lifetime);
        // La firma se calcula con la clave de la cuenta, sin llamar a Azurite.
        var sas = _container.GetBlobClient(productId.ToString()).GenerateSasUri(permissions, expiresOn);
        var url = new Uri(sas.ToString().Replace(_internalEndpoint, _publicEndpoint));

        IssuedKeys.Add(1, new KeyValuePair<string, object?>("permission", permission));
        _logger.LogInformation("[VALET KEY] Clave de {Permission} para la imagen del Product {ProductId}, válida hasta {ExpiresOn:HH:mm:ss} UTC",
            permission == "write" ? "escritura" : "lectura", productId, expiresOn);
        return (url, expiresOn);
    }

    // El contenedor se crea la primera vez que se usa: Azurite arranca vacío y
    // puede no estar listo cuando arranca Products.
    private async Task EnsureContainerAsync(CancellationToken cancellationToken)
    {
        if (_containerExists)
            return;
        await _container.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
        _containerExists = true;
    }
}
