using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Ucu.Andis.Common;
using Ucu.Andis.OrdersService.Data;
using Ucu.Andis.OrdersService.Models;

namespace Ucu.Andis.OrdersService.Controllers;

[ApiController]
[Route("orders")]
public class OrdersController : ControllerBase
{
    private readonly OrdersDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;

    public OrdersController(OrdersDbContext db, IHttpClientFactory httpClientFactory)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
    }

    // POST /orders
    [HttpPost]
    public async Task<ActionResult<object>> CreateOrder([FromBody] Order order)
    {
        _db.Orders.Add(order);
        await _db.SaveChangesAsync();
        return Ok(new { id = order.Id, msg = "Order created" });
    }

    // GET /orders
    [HttpGet]
    public async Task<ActionResult<List<object>>> ListOrders()
    {
        var orders = await _db.Orders
            .Select(o => new
            {
                id = o.Id,
                user_id = o.UserId,
                product_id = o.ProductId,
                quantity = o.Quantity
            })
            .ToListAsync();

        return Ok(orders);
    }

    // GET /orders/user/{userId}/details
    [HttpGet("user/{userId}/details")]
    public async Task<ActionResult<List<object>>> GetOrdersForUser(int userId)
    {
        var orders = await _db.Orders
            .Where(o => o.UserId == userId)
            .ToListAsync();

        if (orders.Count == 0)
            return NotFound();

        var usersClient = _httpClientFactory.CreateClient("UsersService");
        var userResp = await usersClient.GetAsync($"{userId}");

        if (!userResp.IsSuccessStatusCode)
            return StatusCode((int)userResp.StatusCode, "User service error");
        var userJson = await userResp.Content.ReadAsStringAsync();
        var user = JsonSerializer.Deserialize<UserDto>(userJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var productsClient = _httpClientFactory.CreateClient("ProductsService");

        var result = new List<object>();
        foreach (var order in orders)
        {
            var productResp = await productsClient.GetAsync($"{order.ProductId}");
            string? productName = null;
            if (productResp.IsSuccessStatusCode)
            {
                var productJson = await productResp.Content.ReadAsStringAsync();
                var product = JsonSerializer.Deserialize<ProductDto>(productJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                productName = product?.Name;
            }

            result.Add(new
            {
                order_id = order.Id,
                customer_name = user?.Name,
                product_name = productName,
                quantity = order.Quantity
            });
        }

        return Ok(result);
    }
}








// // Controllers/OrdersController.cs
// using Microsoft.AspNetCore.Mvc;
// using MySql.Data.MySqlClient;
// using OrdersService.Models;
// using System.Collections.Generic;
// using Microsoft.Extensions.Configuration;

// [ApiController]
// [Route("orders")]
// public class OrdersController : ControllerBase
// {
//     private readonly string _connectionString;

//     public OrdersController(IConfiguration config)
//     {
//         var host = config["MYSQL_HOST"] ?? "localhost";
//         var port = config["MYSQL_PORT"] ?? "3306";
//         var db = config["MYSQL_DATABASE"];
//         var user = config["MYSQL_USER"];
//         var password = config["MYSQL_PASSWORD"];
//         _connectionString = $"Server={host};Port={port};Database={db};Uid={user};Pwd={password};";
//     }

//     [HttpPost]
//     public ActionResult<object> CreateOrder([FromBody] Order order)
//     {
//         using var conn = new MySqlConnection(_connectionString);
//         conn.Open();
//         using var cmd = new MySqlCommand(
//             "INSERT INTO orders (user_id, product_id, quantity) VALUES (@user_id, @product_id, @quantity); SELECT LAST_INSERT_ID();",
//             conn
//         );
//         cmd.Parameters.AddWithValue("@user_id", order.UserId);
//         cmd.Parameters.AddWithValue("@product_id", order.ProductId);
//         cmd.Parameters.AddWithValue("@quantity", order.Quantity);

//         var orderId = Convert.ToInt32(cmd.ExecuteScalar());
//         return Ok(new { id = orderId, msg = "Order created" });
//     }

//     [HttpGet]
//     public ActionResult<List<object>> ListOrders()
//     {
//         var orders = new List<object>();
//         using var conn = new MySqlConnection(_connectionString);
//         conn.Open();
//         using var cmd = new MySqlCommand("SELECT id, user_id, product_id, quantity FROM orders", conn);
//         using var reader = cmd.ExecuteReader();
//         while (reader.Read())
//         {
//             orders.Add(new
//             {
//                 id = reader.GetInt32("id"),
//                 user_id = reader.GetInt32("user_id"),
//                 product_id = reader.GetString("product_id"),
//                 quantity = reader.GetInt32("quantity")
//             });
//         }
//         return Ok(orders);
//     }
// }
