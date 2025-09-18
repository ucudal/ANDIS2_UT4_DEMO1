// Controllers/ProductsController.cs
using Microsoft.AspNetCore.Mvc;
using MongoDB.Driver;
using ProductsService.Models;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

[ApiController]
[Route("products")]
public class ProductsController : ControllerBase
{
    private readonly IMongoCollection<Product> _productsCollection;

    public ProductsController(IConfiguration config)
    {
        var host = config["MONGO_HOST"] ?? "localhost";
        var port = config["MONGO_PORT"] ?? "27017";
        var db = config["MONGO_DB"] ?? "productsdb";
        var user = config["MONGO_USER"] ?? "productsusr";
        var password = config["MONGO_PASSWORD"] ?? "Pa55w0rd";

        var mongoUrl = $"mongodb://{user}:{password}@{host}:{port}/{db}";
        var client = new MongoClient(mongoUrl);
        var database = client.GetDatabase(db);
        _productsCollection = database.GetCollection<Product>("products");
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
