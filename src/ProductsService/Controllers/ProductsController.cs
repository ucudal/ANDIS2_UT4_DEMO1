using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using ProductsService;
using ProductsService.Models;
using Ucu.Andis.Common;

[ApiController]
[Route("products")]
public class ProductsController : ControllerBase
{
    // Cache-Aside: claves de Redis. La lista completa y cada Product se guardan
    // por separado, y un cambio invalida las dos.
    private const string AllProductsKey = "products:all";
    private static string ProductKey(string id) => $"product:{id}";

    private readonly IMongoCollection<Product> _productsCollection;
    private readonly ProductCache _cache;
    private readonly RabbitMq _rabbitMq;
    private readonly ILogger<ProductsController> _logger;

    public ProductsController(IMongoCollection<Product> productsCollection, ProductCache cache, RabbitMq rabbitMq, ILogger<ProductsController> logger)
    {
        _productsCollection = productsCollection;
        _cache = cache;
        _rabbitMq = rabbitMq;
        _logger = logger;
    }

    [HttpPost]
    public async Task<ActionResult<object>> CreateProduct([FromBody] Product product)
    {
        await _productsCollection.InsertOneAsync(product);
        await _cache.InvalidateAsync(AllProductsKey);
        return Ok(new { id = product.Id, msg = "Product created" });
    }

    [HttpGet]
    public async Task<ActionResult<List<Product>>> ListProducts()
    {
        var products = await _cache.GetOrLoadAsync(AllProductsKey,
            async () => await _productsCollection.Find(_ => true).ToListAsync());
        return Ok(products);
    }

    [HttpGet("{productId}")]
    public async Task<ActionResult<Product>> GetProduct(string productId)
    {
        if (!MongoDB.Bson.ObjectId.TryParse(productId, out _))
            return BadRequest(new { detail = "Invalid product id format" });

        var product = await _cache.GetOrLoadAsync(ProductKey(productId),
            async () => await _productsCollection.Find(p => p.Id == productId).FirstOrDefaultAsync());
        if (product == null)
            return NotFound(new { detail = "Product not found" });

        return Ok(product);
    }

    // Cambia el nombre y el precio de un Product. Cache-Aside: invalida las
    // claves de Redis que lo contienen. Publisher/Subscriber: publica
    // ProductUpdated para que la vista materializada de Orders actualice el
    // nombre.
    [HttpPut("{productId}")]
    public async Task<ActionResult<object>> UpdateProduct(string productId, [FromBody] Product product)
    {
        if (!MongoDB.Bson.ObjectId.TryParse(productId, out _))
            return BadRequest(new { detail = "Invalid product id format" });

        var update = Builders<Product>.Update
            .Set(p => p.Name, product.Name)
            .Set(p => p.Price, product.Price);
        var result = await _productsCollection.UpdateOneAsync(p => p.Id == productId, update);
        if (result.MatchedCount == 0)
            return NotFound(new { detail = "Product not found" });

        await _cache.InvalidateAsync(ProductKey(productId), AllProductsKey);

        // Se publica después de guardar y sin outbox, igual que en Users: si
        // RabbitMQ no está, el evento se pierde -ver UserService.cs-.
        try
        {
            await _rabbitMq.PublishEventAsync(Events.ProductUpdated, new ProductUpdated(productId, product.Name));
            _logger.LogInformation("[PUB/SUB] Publicado {RoutingKey} del Product {ProductId}", Events.ProductUpdated, productId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PUB/SUB] No se pudo publicar {RoutingKey} del Product {ProductId}; el evento se pierde", Events.ProductUpdated, productId);
        }

        return Ok(new { id = productId, msg = "Product updated" });
    }

    // Ajustes de la caché para las demos -ver CacheSettings en ProductCache.cs-.
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
