using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using Ucu.Andis.Common;
using Ucu.Andis.OrdersService.Models;

namespace Ucu.Andis.OrdersService.Data;

public class OrdersDbContext : DbContext, IOutboxDbContext
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

    /// <summary>
    /// Indica si la excepción es una violación de clave primaria o de índice único.
    /// </summary>
    /// <remarks>
    /// Idempotencia: quien inserta y choca con una fila igual sabe que otro ya hizo esa
    /// escritura, sin haber tenido que preguntarlo antes -ver
    /// <c>Messaging.OrderDetailsProjector</c>-. EF Core envuelve el error de MySQL en una
    /// <c>DbUpdateException</c>; el SQL crudo lo lanza directamente.
    /// </remarks>
    public static bool IsDuplicateKey(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is MySqlException { Number: DuplicateEntry })
                return true;
        return false;
    }
}
