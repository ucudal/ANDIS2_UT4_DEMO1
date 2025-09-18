// UsersService/Controllers/UsersController.cs

using Microsoft.AspNetCore.Mvc;
using Npgsql;
using UsersService.Models;
using System.Collections.Generic;

[ApiController]
[Route("users")]
public class UsersController : ControllerBase
{
    private readonly string _connectionString;

    public UsersController(IConfiguration config)
    {
        var host = config["POSTGRES_HOST"] ?? "localhost";
        var port = config["POSTGRES_PORT"] ?? "5432";
        var db = config["POSTGRES_NAME"] ?? "usersdb";
        var user = config["POSTGRES_USER"] ?? "usersusr";
        var password = config["POSTGRES_PASSWORD"] ?? "Pa55w0rd";
        _connectionString = $"Host={host};Port={port};Database={db};Username={user};Password={password}";
    }

    [HttpPost]
    public IActionResult CreateUser([FromBody] User user)
    {
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand("INSERT INTO users (name, email) VALUES (@name, @email)", conn);
        cmd.Parameters.AddWithValue("name", user.Username);
        cmd.Parameters.AddWithValue("email", user.Email);
        cmd.ExecuteNonQuery();
        return Ok(new { msg = "User created" });
    }

    [HttpGet]
    public ActionResult<IEnumerable<User>> ListUsers()
    {
        var users = new List<User>();
        using var conn = new NpgsqlConnection(_connectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand("SELECT name, email FROM users", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            users.Add(new User
            {
                Username = reader.GetString(0),
                Email = reader.GetString(1)
            });
        }
        return Ok(users);
    }
}
