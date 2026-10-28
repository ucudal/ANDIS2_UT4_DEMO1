using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using UsersService.Models;

using Ucu.Andis.Users.Data;

[ApiController]
[Route("users")]
public class UsersController : ControllerBase
{
    private readonly UsersDbContext _db;

    public UsersController(UsersDbContext db)
    {
        _db = db;
    }

    // POST /users
    [HttpPost]
    public async Task<IActionResult> CreateUser([FromBody] User user)
    {
        // El email es único en la base: si ya existe, se responde 409 con el
        // motivo en lugar de un 500 sin cuerpo.
        if (await _db.Users.AnyAsync(u => u.Email == user.Email))
            return Conflict(new { detail = $"Ya existe un User con el email {user.Email}" });

        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return Ok(new { msg = "User created", id = user.Id });
    }

    // GET /users
    [HttpGet]
    public async Task<ActionResult<IEnumerable<User>>> ListUsers()
    {
        var users = await _db.Users.ToListAsync();
        return Ok(users);
    }

    // GET /users/{userId}
    [HttpGet("{userId}")]
    public async Task<ActionResult<User>> GetUserById(int userId)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user == null)
            return NotFound();
        return Ok(user);
    }
}
