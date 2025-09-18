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
