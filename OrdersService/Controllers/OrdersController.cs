// Controllers/OrdersController.cs
using Microsoft.AspNetCore.Mvc;
using MySql.Data.MySqlClient;
using OrdersService.Models;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

[ApiController]
[Route("orders")]
public class OrdersController : ControllerBase
{
    private readonly string _connectionString;

    public OrdersController(IConfiguration config)
    {
        var host = config["MYSQL_HOST"] ?? "localhost";
        var port = config["MYSQL_PORT"] ?? "3306";
        var db = config["MYSQL_DATABASE"];
        var user = config["MYSQL_USER"];
        var password = config["MYSQL_PASSWORD"];
        _connectionString = $"Server={host};Port={port};Database={db};Uid={user};Pwd={password};";
    }

    [HttpPost]
    public ActionResult<object> CreateOrder([FromBody] Order order)
    {
        using var conn = new MySqlConnection(_connectionString);
        conn.Open();
        using var cmd = new MySqlCommand(
            "INSERT INTO orders (user_id, product_id, quantity) VALUES (@user_id, @product_id, @quantity); SELECT LAST_INSERT_ID();",
            conn
        );
        cmd.Parameters.AddWithValue("@user_id", order.UserId);
        cmd.Parameters.AddWithValue("@product_id", order.ProductId);
        cmd.Parameters.AddWithValue("@quantity", order.Quantity);

        var orderId = Convert.ToInt32(cmd.ExecuteScalar());
        return Ok(new { id = orderId, msg = "Order created" });
    }

    [HttpGet]
    public ActionResult<List<object>> ListOrders()
    {
        var orders = new List<object>();
        using var conn = new MySqlConnection(_connectionString);
        conn.Open();
        using var cmd = new MySqlCommand("SELECT id, user_id, product_id, quantity FROM orders", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            orders.Add(new
            {
                id = reader.GetInt32("id"),
                user_id = reader.GetInt32("user_id"),
                product_id = reader.GetString("product_id"),
                quantity = reader.GetInt32("quantity")
            });
        }
        return Ok(orders);
    }
}
