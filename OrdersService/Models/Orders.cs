// Models/Order.cs
namespace OrdersService.Models
{
    public class Order
    {
        public int UserId { get; set; }
        public string ProductId { get; set; }
        public int Quantity { get; set; }
    }
}
