using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using ProductsService.Data;
using ProductsService.Models;

namespace ProductsService;

// Saga: el stock de cada Product y las reservas, como métricas para Grafana.
// El stock lo cambian las reservas, las compensaciones y los ajustes, y vive en
// PostgreSQL. La métrica se lee cuando Prometheus la pide, y no puede esperar
// una consulta a la base: este servicio la lee cada 2 segundos y guarda el
// último valor.
public class StockMetrics : BackgroundService
{
    public const string MeterName = "Ucu.Andis.ProductsStock";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly Meter Meter = new(MeterName);

    // Reservas por resultado: Reserved, Rejected o Released. Released es la
    // compensación de una Order rechazada o cancelada.
    private static readonly Counter<long> Reservations = Meter.CreateCounter<long>("products.stock.reservations", "{reservation}",
        "Reservas de stock de la Saga, por resultado: Reserved, Rejected o Released");

    // Se reemplaza entera en cada lectura: Prometheus nunca ve una lista a medias.
    private volatile IReadOnlyList<(int Id, string Name, int Stock)> _stock = [];
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StockMetrics> _logger;
    private bool _failing;

    public StockMetrics(IServiceScopeFactory scopeFactory, ILogger<StockMetrics> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        // Cada resultado arranca en 0. Si no, la serie de un resultado aparece
        // recién con su primera reserva, y Prometheus no cuenta ese primer
        // incremento.
        foreach (var result in new[] { ReservationStatus.Reserved, ReservationStatus.Rejected, ReservationStatus.Released })
            Reservations.Add(0, new KeyValuePair<string, object?>("result", result));
        Meter.CreateObservableGauge("products.stock",
            () => _stock.Select(p => new Measurement<int>(p.Stock,
                new("product_id", p.Id), new("product", p.Name))),
            "{unit}", "Unidades disponibles de cada Product");
    }

    public static void CountReservation(string result) => Reservations.Add(1, new KeyValuePair<string, object?>("result", result));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ProductsDbContext>();
                var products = await db.Products.AsNoTracking()
                    .Select(p => new { p.Id, p.Name, p.Stock })
                    .ToListAsync(stoppingToken);
                _stock = products.Select(p => (p.Id, p.Name ?? "", p.Stock)).ToList();
                _failing = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Sin la base, la métrica conserva el último valor leído. Se avisa
                // una vez, no cada 2 segundos -ver la demo 06-.
                if (!_failing)
                    _logger.LogWarning("No se pudo leer el stock para las métricas: {Message}", ex.Message);
                _failing = true;
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }
}
