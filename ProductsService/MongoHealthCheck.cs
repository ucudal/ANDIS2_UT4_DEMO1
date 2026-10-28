using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Driver;
using ProductsService.Models;

namespace ProductsService;

// Health check de MongoDB: envía un "ping" a la base de datos.
public class MongoHealthCheck : IHealthCheck
{
    private readonly IMongoDatabase _database;

    public MongoHealthCheck(IMongoCollection<Product> productsCollection)
    {
        _database = productsCollection.Database;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
            return HealthCheckResult.Healthy("MongoDB responde");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MongoDB no responde", ex);
        }
    }
}
