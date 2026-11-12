using Microsoft.EntityFrameworkCore;
using Npgsql;
using ProductsService.Models;
using Ucu.Andis.Common;

namespace ProductsService.Data;

public class ProductsDbContext : DbContext, IOutboxDbContext
{
    public ProductsDbContext(DbContextOptions<ProductsDbContext> options) : base(options) { }
    public DbSet<Product> Products { get; set; }
    public DbSet<StockReservation> StockReservations { get; set; }
    public DbSet<OutboxMessage> OutboxMessages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<StockReservation>().HasKey(r => r.OrderId);

    // Idempotencia: indica si la excepción es una violación de clave primaria o
    // de índice único -en PostgreSQL, el código 23505-. EF Core envuelve el
    // error en una DbUpdateException.
    public static bool IsDuplicateKey(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                return true;
        return false;
    }
}
