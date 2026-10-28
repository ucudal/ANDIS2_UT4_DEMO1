using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using ProductsService.Models;

[ApiController]
[Route("products")]
public class ProductsController : ControllerBase
{
    private readonly IMongoCollection<Product> _productsCollection;

    public ProductsController(IMongoCollection<Product> productsCollection)
    {
        _productsCollection = productsCollection;
    }

    [HttpPost]
    public ActionResult<object> CreateProduct([FromBody] Product product)
    {
        _productsCollection.InsertOne(product);
        return Ok(new { id = product.Id, msg = "Product created" });
    }

    [HttpGet]
    public ActionResult<List<Product>> ListProducts()
    {
        var products = _productsCollection.Find(_ => true).ToList();
        return Ok(products);
    }

    [HttpGet("{productId}")]
    public ActionResult<Product> GetProduct(string productId)
    {
        if (!MongoDB.Bson.ObjectId.TryParse(productId, out var objectId))
            return BadRequest(new { detail = "Invalid product id format" });

        var product = _productsCollection.Find(p => p.Id == productId).FirstOrDefault();
        if (product == null)
            return NotFound(new { detail = "Product not found" });

        return Ok(product);
    }
}
