using Microsoft.EntityFrameworkCore;
using UsersService.Models;

namespace Ucu.Andis.Users.Data;

public class UsersDbContext : DbContext
{
    public UsersDbContext(DbContextOptions<UsersDbContext> options) : base(options) { }
    public DbSet<User> Users { get; set; }
}
