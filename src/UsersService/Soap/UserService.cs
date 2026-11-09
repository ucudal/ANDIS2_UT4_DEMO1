using System.ServiceModel;
using Microsoft.EntityFrameworkCore;
using UsersService.Models;

using Ucu.Andis.Common;
using Ucu.Andis.Users.Data;

namespace Ucu.Andis.Users.Soap;

public class UserService : IUserService
{
    private readonly UsersDbContext _db;
    private readonly RabbitMq _rabbitMq;
    private readonly Release _release;
    private readonly Experiment _experiment;
    private readonly ILogger<UserService> _logger;

    public UserService(UsersDbContext db, RabbitMq rabbitMq, Release release, Experiment experiment,
        ILogger<UserService> logger)
    {
        _db = db;
        _rabbitMq = rabbitMq;
        _release = release;
        _experiment = experiment;
        _logger = logger;
    }

    public async Task<User?> GetUser(int id)
    {
        var user = await _db.Users.FindAsync(id);
        // A/B Testing: solo la versión 2 trae el experimento -ver Experiment.cs-.
        if (user is not null && _release.Version >= 2)
            await _experiment.ApplyAsync(user);
        return user;
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

    public async Task<User> UpdateUser(int id, string name, string email)
    {
        var user = await _db.Users.FindAsync(id)
            ?? throw new FaultException($"No existe el User {id}");

        if (await _db.Users.AnyAsync(u => u.Email == email && u.Id != id))
            throw new FaultException($"Ya existe otro User con el email {email}");

        user.Name = name;
        user.Email = email;
        await _db.SaveChangesAsync();

        // Publisher/Subscriber: se avisa del cambio a quien le interese, sin
        // saber quién es; hoy, a la vista materializada de Orders.
        //
        // Se publica después de guardar y sin outbox: si RabbitMQ no está, el
        // cambio queda guardado pero el evento se pierde, y la vista de Orders
        // sigue con el nombre anterior. Es la falla que el Transactional Outbox
        // evita en Orders -ver OutboxRelay.cs-.
        try
        {
            await _rabbitMq.PublishEventAsync(Events.UserUpdated, new UserUpdated(user.Id, user.Name));
            _logger.LogInformation("[PUB/SUB] Publicado {RoutingKey} del User {UserId}", Events.UserUpdated, user.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PUB/SUB] No se pudo publicar {RoutingKey} del User {UserId}; el evento se pierde", Events.UserUpdated, user.Id);
        }

        return user;
    }
}
