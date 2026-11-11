using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Ucu.Andis.OrdersService.Models;

namespace Ucu.Andis.OrdersService.Data;

public class OrdersDbContext : DbContext
{
    public OrdersDbContext(DbContextOptions<OrdersDbContext> options) : base(options) { }
    public DbSet<Order> Orders { get; set; }
    public DbSet<OrderEvent> OrderEvents { get; set; }
    public DbSet<OutboxMessage> OutboxMessages { get; set; }
    public DbSet<OrderDetails> OrderDetails { get; set; }
    public DbSet<IdempotencyKey> IdempotencyKeys { get; set; }

    // Código de error de MySQL para una clave primaria o un índice único
    // repetido: ER_DUP_ENTRY.
    private const int DuplicateEntry = 1062;

    // Idempotencia: indica si la excepción es una violación de clave primaria o
    // de índice único. Quien inserta y choca con una fila igual sabe que otro ya
    // hizo esa escritura, sin haber tenido que preguntarlo antes -ver
    // OrderDetailsProjector.cs-. EF Core envuelve el error de MySQL en una
    // DbUpdateException; el SQL crudo lo lanza directamente.
    public static bool IsDuplicateKey(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is MySqlException { Number: DuplicateEntry })
                return true;
        return false;
    }
}
