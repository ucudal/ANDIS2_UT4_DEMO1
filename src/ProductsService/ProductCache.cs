using System.Diagnostics.Metrics;
using System.Text.Json;
using StackExchange.Redis;

namespace ProductsService;

// Ajustes de la caché para las demos. Se cambian en tiempo de ejecución, sin
// reiniciar, igual que el chaos:
//
//   PUT    /products/cache  {"enabled": false, "dbDelayMs": 200}
//   GET    /products/cache
//   DELETE /products/cache  -vuelve a los valores por defecto-
public class CacheSettings
{
    // Si es false, toda lectura va a MongoDB: sirve para comparar con y sin
    // caché bajo la misma carga.
    public bool Enabled { get; set; } = true;

    // Demora agregada a cada lectura de MongoDB, en milisegundos. MongoDB en la
    // misma máquina responde en 1 o 2 ms y la diferencia con Redis no se nota;
    // con una base más lenta, como suele ser una real, sí.
    public int DbDelayMs { get; set; }
}

// Cache-Aside: Products busca primero en Redis; si no está -miss-, lee MongoDB
// y guarda el resultado en Redis por 60 segundos para la próxima. Al cambiar un
// Product, Products borra de Redis las claves afectadas: la próxima lectura es
// un miss y trae el dato nuevo.
//
// La aplicación es la que maneja la caché; Redis no sabe nada de MongoDB. Si
// Redis no responde, se lee MongoDB directamente: la caché acelera, pero
// Products funciona sin ella.
public class ProductCache
{
    public const string MeterName = "Ucu.Andis.ProductsCache";

    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> Lookups = Meter.CreateCounter<long>("products.cache", "{lookup}",
        "Lecturas de la caché de Products: hit, miss, error o disabled");

    private readonly IConnectionMultiplexer _redis;
    private readonly ILogger<ProductCache> _logger;

    public CacheSettings Settings { get; } = new();

    public ProductCache(IConnectionMultiplexer redis, ILogger<ProductCache> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<T?> GetOrLoadAsync<T>(string key, Func<Task<T?>> load)
    {
        if (!Settings.Enabled)
        {
            Count("disabled");
            return await LoadFromDbAsync(load);
        }

        // Si ya se sabe que Redis no está, se va directo a MongoDB, sin esperar
        // el timeout en cada lectura. El cliente se reconecta solo cuando vuelve.
        if (!_redis.IsConnected)
        {
            Count("error");
            _logger.LogWarning("[CACHE-ASIDE] Redis no está conectado, se lee MongoDB");
            return await LoadFromDbAsync(load);
        }

        var db = _redis.GetDatabase();
        try
        {
            var cached = await db.StringGetAsync(key);
            if (cached.HasValue)
            {
                Count("hit");
                _logger.LogInformation("[CACHE-ASIDE] hit {Key}", key);
                return JsonSerializer.Deserialize<T>(cached.ToString());
            }
        }
        catch (RedisException ex)
        {
            Count("error");
            _logger.LogWarning("[CACHE-ASIDE] Redis no responde, se lee MongoDB: {Error}", ex.GetType().Name);
            return await LoadFromDbAsync(load);
        }

        Count("miss");
        _logger.LogInformation("[CACHE-ASIDE] miss {Key}, se lee MongoDB", key);
        var value = await LoadFromDbAsync(load);

        // Lo que no existe no se guarda: si se creara después, la caché seguiría
        // diciendo que no existe hasta que venciera.
        if (value is not null)
        {
            try
            {
                await db.StringSetAsync(key, JsonSerializer.Serialize(value), Ttl);
            }
            catch (RedisException ex)
            {
                _logger.LogWarning("[CACHE-ASIDE] No se pudo guardar {Key} en Redis: {Error}", key, ex.GetType().Name);
            }
        }

        return value;
    }

    // Se borra en lugar de escribir el valor nuevo: así la caché nunca guarda un
    // valor que MongoDB no tiene, aunque dos escrituras se crucen.
    public async Task InvalidateAsync(params string[] keys)
    {
        try
        {
            await _redis.GetDatabase().KeyDeleteAsync(keys.Select(k => (RedisKey)k).ToArray());
            _logger.LogInformation("[CACHE-ASIDE] Invalidadas {Keys}", string.Join(", ", keys));
        }
        catch (RedisException ex)
        {
            // Si Redis no está, tampoco tiene la clave vieja que haya que borrar,
            // salvo que vuelva antes de que venza: el TTL acota cuánto puede durar
            // un dato desactualizado.
            _logger.LogWarning("[CACHE-ASIDE] No se pudo invalidar {Keys}: {Error}", string.Join(", ", keys), ex.GetType().Name);
        }
    }

    private async Task<T?> LoadFromDbAsync<T>(Func<Task<T?>> load)
    {
        if (Settings.DbDelayMs > 0)
            await Task.Delay(Settings.DbDelayMs);
        return await load();
    }

    private static void Count(string result) => Lookups.Add(1, new KeyValuePair<string, object?>("result", result));
}
