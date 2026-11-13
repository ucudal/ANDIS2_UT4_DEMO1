using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Ucu.Andis.ProductsService.Data;
using Ucu.Andis.ProductsService.Models;
using Ucu.Andis.Common;

namespace Ucu.Andis.ProductsService.Controllers;

[ApiController]
[Route("products")]
public class ProductsController : ControllerBase
{
    /// <summary>
    /// Clave de Redis de la lista completa de Products.
    /// </summary>
    /// <remarks>
    /// Cache-Aside: la lista completa y cada Product se guardan por separado
    /// -ver <c>ProductsController.ProductKey</c>-. Crear un Product invalida esta clave, y cambiar
    /// uno invalida las dos. Las usa también la Saga, que cambia el stock -ver
    /// <c>StockReservations</c>-.
    /// </remarks>
    public const string AllProductsKey = "products:all";

    /// <summary>
    /// Devuelve la clave de Redis de un Product.
    /// </summary>
    /// <remarks>
    /// Cache-Aside: cada Product se guarda por separado de la lista completa
    /// -ver <c>ProductsController.AllProductsKey</c>-. Cambiar un Product invalida las dos claves.
    /// </remarks>
    public static string ProductKey(int id) => $"product:{id}";

    private readonly ProductsDbContext _db;
    private readonly ProductCache _cache;
    private readonly ProductImages _images;
    private readonly RabbitMq _rabbitMq;
    private readonly ILogger<ProductsController> _logger;

    public ProductsController(ProductsDbContext db, ProductCache cache, ProductImages images,
        RabbitMq rabbitMq, ILogger<ProductsController> logger)
    {
        _db = db;
        _cache = cache;
        _images = images;
        _rabbitMq = rabbitMq;
        _logger = logger;
    }

    /// <summary>
    /// Crea un Product.
    /// </summary>
    /// <remarks>
    /// El id lo asigna la base: si el cuerpo trae uno, se ignora.
    /// </remarks>
    [HttpPost]
    public async Task<ActionResult<object>> CreateProduct([FromBody] Product product)
    {
        var created = new Product { Name = product.Name, Price = product.Price, Stock = Math.Max(product.Stock, 0) };
        _db.Products.Add(created);
        await _db.SaveChangesAsync();
        await _cache.InvalidateAsync(AllProductsKey);
        return Ok(new { id = created.Id, msg = "Product created" });
    }

    [HttpGet]
    public async Task<ActionResult<List<Product>>> ListProducts()
    {
        var products = await _cache.GetOrLoadAsync(AllProductsKey,
            async () => await _db.Products.AsNoTracking().OrderBy(p => p.Id).ToListAsync());
        return Ok(products);
    }

    /// <summary>
    /// Devuelve un Product.
    /// </summary>
    /// <remarks>
    /// Un id que no es un número responde 400: lo rechaza ASP.NET Core al no poder
    /// convertirlo, antes de llegar acá.
    /// </remarks>
    [HttpGet("{productId}")]
    public async Task<ActionResult<Product>> GetProduct(int productId)
    {
        var product = await _cache.GetOrLoadAsync(ProductKey(productId),
            async () => await _db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId));
        if (product == null)
            return NotFound(new { detail = "Product not found" });

        return Ok(product);
    }

    /// <summary>
    /// Cambia el nombre y el precio de un Product.
    /// </summary>
    /// <remarks>
    /// Cache-Aside: invalida las claves de Redis que lo contienen.
    /// Publisher/Subscriber: publica <c>Common.ProductUpdated</c> para que la vista
    /// materializada de Orders actualice el nombre.
    /// </remarks>
    [HttpPut("{productId}")]
    public async Task<ActionResult<object>> UpdateProduct(int productId, [FromBody] Product product)
    {
        var updated = await _db.Products
            .Where(p => p.Id == productId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.Name, product.Name)
                .SetProperty(p => p.Price, product.Price));
        // Fecha del cambio: los proyectores la usan para no pisar un nombre más nuevo
        // con uno más viejo -ver Events.cs-.
        var changedAt = DateTime.UtcNow;
        if (updated == 0)
            return NotFound(new { detail = "Product not found" });

        await _cache.InvalidateAsync(ProductKey(productId), AllProductsKey);

        // Se publica después de guardar y sin outbox, igual que en Users: si
        // RabbitMQ no está, el evento se pierde -ver UserService.cs-.
        try
        {
            await _rabbitMq.PublishEventAsync(Events.ProductUpdated, new ProductUpdated(productId, product.Name, changedAt));
            _logger.LogInformation("[PUB/SUB] Publicado {RoutingKey} del Product {ProductId}", Events.ProductUpdated, productId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PUB/SUB] No se pudo publicar {RoutingKey} del Product {ProductId}; el evento se pierde", Events.ProductUpdated, productId);
        }

        return Ok(new { id = productId, msg = "Product updated" });
    }

    /// <summary>
    /// Fija el stock de un Product, como un ajuste de inventario.
    /// </summary>
    /// <remarks>
    /// Saga: lo usa la demo de la Saga para ver qué pasa cuando falta stock.
    /// <c>PUT /products/{id}</c> no lo cambia: el stock lo mueven las reservas.
    /// </remarks>
    [HttpPut("{productId}/stock")]
    public async Task<ActionResult<object>> SetStock(int productId, [FromBody] StockRequest request)
    {
        if (request.Stock < 0)
            return BadRequest(new { detail = "El stock no puede ser negativo" });

        var updated = await _db.Products
            .Where(p => p.Id == productId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Stock, request.Stock));
        if (updated == 0)
            return NotFound(new { detail = "Product not found" });

        await _cache.InvalidateAsync(ProductKey(productId), AllProductsKey);
        _logger.LogInformation("[SAGA] Stock del Product {ProductId}: {Stock}", productId, request.Stock);
        return Ok(new { id = productId, stock = request.Stock });
    }

    public record StockRequest(int Stock);

    /// <summary>
    /// Recibe la imagen de un Product y la sube al almacenamiento.
    /// </summary>
    /// <remarks>
    /// Sin Valet Key: el cliente le manda la imagen a Products, que la sube al
    /// almacenamiento con su propia clave. Todos los bytes pasan por el gateway y por
    /// Products -ver <c>ProductImages</c>-.
    /// </remarks>
    [HttpPut("{productId}/image")]
    public async Task<ActionResult<object>> UploadImage(int productId, CancellationToken cancellationToken)
    {
        if (!await ProductExistsAsync(productId))
            return NotFound(new { detail = "Product not found" });

        var bytes = await _images.UploadAsync(productId, Request.Body, Request.ContentType ?? "application/octet-stream", cancellationToken);
        return Ok(new { id = productId, bytes, msg = "Image uploaded through Products" });
    }

    /// <summary>
    /// Devuelve una URL firmada para que el cliente suba la imagen de un Product
    /// directamente al almacenamiento.
    /// </summary>
    /// <remarks>
    /// Valet Key: en lugar de recibir la imagen, Products entrega la URL. El cliente la
    /// sube con un PUT y el encabezado <c>x-ms-blob-type: BlockBlob</c>. Vence a los 2
    /// minutos, o antes si el cliente lo pide con <c>?seconds=N</c>.
    /// </remarks>
    [HttpPost("{productId}/image/upload-url")]
    public async Task<ActionResult<object>> GetImageUploadUrl(int productId, [FromQuery] int? seconds, CancellationToken cancellationToken)
    {
        if (!await ProductExistsAsync(productId))
            return NotFound(new { detail = "Product not found" });

        var lifetime = seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : ProductImages.MaxKeyLifetime;
        var (url, expiresOn) = await _images.GetUploadKeyAsync(productId, lifetime, cancellationToken);
        return Ok(new { uploadUrl = url, expiresOn, method = "PUT", headers = new Dictionary<string, string> { ["x-ms-blob-type"] = "BlockBlob" } });
    }

    /// <summary>
    /// Redirige a una URL firmada para leer la imagen de un Product.
    /// </summary>
    /// <remarks>
    /// Valet Key: el cliente la baja directamente del almacenamiento.
    /// </remarks>
    [HttpGet("{productId}/image")]
    public async Task<ActionResult> GetImage(int productId, CancellationToken cancellationToken)
    {
        var key = await _images.GetDownloadKeyAsync(productId, cancellationToken);
        if (key is null)
            return NotFound(new { detail = "Product has no image" });

        return Redirect(key.Value.Url.ToString());
    }

    private Task<bool> ProductExistsAsync(int productId) =>
        _db.Products.AnyAsync(p => p.Id == productId);

    /// <summary>
    /// Devuelve los ajustes de la caché para las demos -ver
    /// <c>CacheSettings</c>-.
    /// </summary>
    [HttpGet("cache")]
    public ActionResult<CacheSettings> GetCacheSettings() => Ok(_cache.Settings);

    [HttpPut("cache")]
    public ActionResult<CacheSettings> SetCacheSettings([FromBody] CacheSettings settings)
    {
        _cache.Settings.Enabled = settings.Enabled;
        _cache.Settings.DbDelayMs = Math.Max(settings.DbDelayMs, 0);
        _logger.LogWarning("[CACHE-ASIDE] Caché: enabled={Enabled}, dbDelayMs={DbDelayMs}", _cache.Settings.Enabled, _cache.Settings.DbDelayMs);
        return Ok(_cache.Settings);
    }

    [HttpDelete("cache")]
    public ActionResult<CacheSettings> ResetCacheSettings() => SetCacheSettings(new CacheSettings());
}
