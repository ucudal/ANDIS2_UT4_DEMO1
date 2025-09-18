using Microsoft.EntityFrameworkCore;
using Ucu.Andis.OrdersService.Models;

namespace Ucu.Andis.OrdersService.Data;

public class OrdersDbContext : DbContext
{
    public OrdersDbContext(DbContextOptions<OrdersDbContext> options) : base(options) { }
    public DbSet<Order> Orders { get; set; }
}
