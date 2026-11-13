using System.ServiceModel;
using MongoDB.Bson;
using MongoDB.Driver;
using Ucu.Andis.Users.Models;

using Ucu.Andis.Common;

namespace Ucu.Andis.Users.Soap;

public class UserService : IUserService
{
    private readonly IMongoCollection<User> _users;
    private readonly RabbitMq _rabbitMq;
    private readonly Release _release;
    private readonly Experiment _experiment;
    private readonly ILogger<UserService> _logger;

    public UserService(IMongoCollection<User> users, RabbitMq rabbitMq, Release release, Experiment experiment,
        ILogger<UserService> logger)
    {
        _users = users;
        _rabbitMq = rabbitMq;
        _release = release;
        _experiment = experiment;
        _logger = logger;
    }

    public async Task<User?> GetUser(string id)
    {
        if (!ObjectId.TryParse(id, out _))
            return null;

        var user = await _users.Find(u => u.Id == id).FirstOrDefaultAsync();
        // A/B Testing: solo la versión 2 trae el experimento -ver Experiment.cs-.
        if (user is not null && _release.Version >= 2)
            await _experiment.ApplyAsync(user);
        return user;
    }

    public async Task<User[]> ListUsers()
    {
        return (await _users.Find(_ => true).ToListAsync()).ToArray();
    }

    public async Task<string> CreateUser(string name, string email)
    {
        // El email es único en la base: si ya existe, se responde un Fault con
        // el motivo en lugar de un error sin explicación.
        if (await _users.Find(u => u.Email == email).AnyAsync())
            throw new FaultException($"Ya existe un User con el email {email}");

        // MongoDB le asigna el ObjectId al insertarlo.
        var user = new User { Name = name, Email = email };
        await _users.InsertOneAsync(user);
        return user.Id!;
    }

    public async Task<User> UpdateUser(string id, string name, string email)
    {
        if (!ObjectId.TryParse(id, out _) || !await _users.Find(u => u.Id == id).AnyAsync())
            throw new FaultException($"No existe el User {id}");

        if (await _users.Find(u => u.Email == email && u.Id != id).AnyAsync())
            throw new FaultException($"Ya existe otro User con el email {email}");

        var user = await _users.FindOneAndUpdateAsync(u => u.Id == id,
            Builders<User>.Update.Set(u => u.Name, name).Set(u => u.Email, email),
            new FindOneAndUpdateOptions<User> { ReturnDocument = ReturnDocument.After });
        // Fecha del cambio: los proyectores la usan para no pisar un nombre más nuevo
        // con uno más viejo -ver Events.cs-.
        var changedAt = DateTime.UtcNow;

        // Publisher/Subscriber: se avisa del cambio a quien le interese, sin
        // saber quién es; hoy, a la vista materializada de Orders.
        //
        // Se publica después de guardar y sin outbox: si RabbitMQ no está, el
        // cambio queda guardado pero el evento se pierde, y la vista de Orders
        // sigue con el nombre anterior. Es la falla que el Transactional Outbox
        // evita en Orders -ver OutboxRelay.cs-.
        try
        {
            await _rabbitMq.PublishEventAsync(Events.UserUpdated, new UserUpdated(user.Id!, user.Name, changedAt));
            _logger.LogInformation("[PUB/SUB] Publicado {RoutingKey} del User {UserId}", Events.UserUpdated, user.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PUB/SUB] No se pudo publicar {RoutingKey} del User {UserId}; el evento se pierde", Events.UserUpdated, user.Id);
        }

        return user;
    }
}
