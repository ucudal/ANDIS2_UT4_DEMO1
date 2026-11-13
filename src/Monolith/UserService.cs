using System.ServiceModel;
using Microsoft.EntityFrameworkCore;

namespace Ucu.Andis.Monolith;

/// <summary>
/// Define el mismo contrato SOAP que Users.
/// </summary>
[ServiceContract(Namespace = "http://ucu.edu.uy/andis/users")]
public interface IUserService
{
    [OperationContract] Task<User?> GetUser(string id);
    [OperationContract] Task<User[]> ListUsers();
    [OperationContract] Task<User> UpdateUser(string id, string name, string email);
}

public class UserService : IUserService
{
    private readonly MonolithDb _db;

    public UserService(MonolithDb db) => _db = db;

    public async Task<User?> GetUser(string id) => await _db.Users.FindAsync(id);

    public Task<User[]> ListUsers() => _db.Users.ToArrayAsync();

    public async Task<User> UpdateUser(string id, string name, string email)
    {
        var user = await _db.Users.FindAsync(id) ?? throw new FaultException($"No existe el User {id}");
        user.Name = name;
        user.Email = email;
        await _db.SaveChangesAsync();
        return user;
    }
}
