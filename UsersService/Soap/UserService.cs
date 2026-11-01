using System.ServiceModel;
using Microsoft.EntityFrameworkCore;
using UsersService.Models;

using Ucu.Andis.Users.Data;

namespace Ucu.Andis.Users.Soap;

public class UserService : IUserService
{
    private readonly UsersDbContext _db;

    public UserService(UsersDbContext db)
    {
        _db = db;
    }

    public async Task<User?> GetUser(int id)
    {
        return await _db.Users.FindAsync(id);
    }

    public async Task<User[]> ListUsers()
    {
        return await _db.Users.ToArrayAsync();
    }

    public async Task<int> CreateUser(string name, string email)
    {
        // El email es único en la base: si ya existe, se responde un Fault con
        // el motivo en lugar de un error sin explicación.
        if (await _db.Users.AnyAsync(u => u.Email == email))
            throw new FaultException($"Ya existe un User con el email {email}");

        var user = new User { Name = name, Email = email };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user.Id;
    }
}
