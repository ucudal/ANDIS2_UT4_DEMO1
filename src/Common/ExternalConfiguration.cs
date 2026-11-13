using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ucu.Andis.Common;

/// <summary>
/// Proporciona los métodos que agregan el External Configuration Store a la
/// configuración de un proceso.
/// </summary>
/// <remarks>
/// Los valores que cambian entre entornos o con el tiempo -URL, límites,
/// feature flags- están en un almacén central, Consul KV, en lugar de estar en
/// la imagen o en las variables de entorno de cada contenedor. Cada proceso los
/// lee al arrancar y los vuelve a leer cada 2 segundos: cambiar un valor no
/// obliga a redesplegar ni a reiniciar.
///
/// El almacén es una fuente más de <c>IConfiguration</c>, la última que se
/// agrega: gana sobre <c>appsettings.json</c> y las variables de entorno, que
/// quedan como valores por defecto. El código que lee la configuración no sabe
/// de dónde viene el valor. Tiene que leerla en cada uso, y no guardarla al
/// arrancar.
///
/// La clave <c>andis/Orders/MaxQuantity</c> de Consul es
/// <c>Orders:MaxQuantity</c> en <c>IConfiguration</c>.
/// </remarks>
public static class ExternalConfigurationExtensions
{
    public const string MeterName = "Ucu.Andis.ExternalConfiguration";

    /// <summary>
    /// Agrega Consul como fuente de configuración.
    /// </summary>
    /// <remarks>
    /// Sin <c>Consul:Url</c> el proceso usa solo su configuración local.
    /// </remarks>
    public static WebApplicationBuilder AddExternalConfiguration(this WebApplicationBuilder builder)
    {
        if (builder.Configuration["Consul:Url"] is not { Length: > 0 } url)
            return builder;

        var source = new ConsulConfigurationSource(url);
        ((IConfigurationBuilder)builder.Configuration).Add(source);
        builder.Services.AddSingleton(source);
        builder.Services.AddHostedService<ConsulConfigurationPoller>();
        return builder;
    }
}

public class ConsulConfigurationSource : IConfigurationSource
{
    public ConsulConfigurationProvider Provider { get; }

    public ConsulConfigurationSource(string url) => Provider = new ConsulConfigurationProvider(url);

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

public class ConsulConfigurationProvider : ConfigurationProvider, IDisposable
{
    private const string Prefix = "andis/";

    private readonly HttpClient _client;

    public ConsulConfigurationProvider(string url)
    {
        _client = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(2) };
    }

    /// <summary>
    /// Libera el cliente HTTP con el que se lee Consul.
    /// </summary>
    public void Dispose()
    {
        _client.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Lee el almacén por primera vez, al arrancar.
    /// </summary>
    /// <remarks>
    /// Si Consul no responde, el proceso arranca igual, con sus valores por
    /// defecto: el almacén no puede ser una dependencia sin la cual nada
    /// arranca.
    /// </remarks>
    public override void Load()
    {
        try
        {
            Data = ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // El ConsulConfigurationPoller avisa en el log y sigue intentando.
        }
    }

    /// <summary>
    /// Devuelve los valores que son números o booleanos, para publicarlos como
    /// métrica.
    /// </summary>
    /// <remarks>
    /// Los demás, como una URL, no se pueden graficar.
    /// </remarks>
    public IEnumerable<KeyValuePair<string, double>> NumericValues()
    {
        foreach (var (key, value) in Data)
        {
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                yield return KeyValuePair.Create(key, number);
            else if (bool.TryParse(value, out var flag))
                yield return KeyValuePair.Create(key, flag ? 1d : 0d);
        }
    }

    /// <summary>
    /// Vuelve a leer el almacén y devuelve las claves que cambiaron.
    /// </summary>
    /// <remarks>
    /// Si Consul no responde, lanza una excepción y <c>Data</c> no se toca: se
    /// siguen usando los últimos valores leídos.
    /// </remarks>
    public async Task<IReadOnlyList<string>> RefreshAsync(CancellationToken cancellationToken)
    {
        var current = await ReadAsync(cancellationToken);
        var changed = current.Keys.Union(Data.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(key => !current.TryGetValue(key, out var value) || !Data.TryGetValue(key, out var old) || value != old)
            .ToList();

        if (changed.Count > 0)
        {
            Data = current;
            OnReload();
        }
        return changed;
    }

    private async Task<IDictionary<string, string?>> ReadAsync(CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        using var response = await _client.GetAsync($"/v1/kv/{Prefix}?recurse=true", cancellationToken);
        // Consul responde 404 si no hay ninguna clave con ese prefijo.
        if (response.StatusCode == HttpStatusCode.NotFound)
            return data;
        response.EnsureSuccessStatusCode();

        var entries = await response.Content.ReadFromJsonAsync<ConsulEntry[]>(cancellationToken) ?? [];
        foreach (var entry in entries.Where(e => e.Value is not null))
        {
            var key = entry.Key[Prefix.Length..].Replace('/', ':');
            data[key] = Encoding.UTF8.GetString(Convert.FromBase64String(entry.Value!));
        }
        return data;
    }

    // Consul entrega cada valor en base64.
    private sealed record ConsulEntry([property: JsonPropertyName("Key")] string Key, [property: JsonPropertyName("Value")] string? Value);
}

/// <summary>
/// Representa el servicio en segundo plano que vuelve a leer el almacén cada 2
/// segundos.
/// </summary>
/// <remarks>
/// Es polling: alcanza para una demo y es fácil de seguir. Consul también
/// ofrece consultas bloqueantes, que responden apenas cambia un valor.
/// </remarks>
public class ConsulConfigurationPoller : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly Meter Meter = new(ExternalConfigurationExtensions.MeterName);

    private readonly ConsulConfigurationProvider _provider;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ConsulConfigurationPoller> _logger;
    private bool _storeDown;

    public ConsulConfigurationPoller(ConsulConfigurationSource source, IConfiguration configuration, ILogger<ConsulConfigurationPoller> logger)
    {
        _provider = source.Provider;
        _configuration = configuration;
        _logger = logger;

        // El valor vigente en este proceso: en Grafana se ve cuándo le llegó el
        // cambio a cada uno.
        Meter.CreateObservableGauge("config.value", () => _provider.NumericValues()
            .Select(v => new Measurement<double>(v.Value, new KeyValuePair<string, object?>("key", v.Key))),
            description: "Valor vigente de cada clave numérica del almacén de configuración");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var changed = await _provider.RefreshAsync(stoppingToken);
                if (_storeDown)
                    _logger.LogInformation("[EXTERNAL CONFIG] Consul volvió");
                _storeDown = false;

                foreach (var key in changed)
                    _logger.LogInformation("[EXTERNAL CONFIG] {Key} = {Value}", key, _configuration[key] ?? "(valor por defecto)");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !stoppingToken.IsCancellationRequested)
            {
                if (!_storeDown)
                    _logger.LogWarning("[EXTERNAL CONFIG] Consul no responde; se siguen usando los últimos valores");
                _storeDown = true;
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
