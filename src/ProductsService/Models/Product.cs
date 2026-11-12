using System.ComponentModel.DataAnnotations.Schema;

namespace ProductsService.Models
{
    [Table("products")]
    public class Product
    {
        [Column("id")]
        public int Id { get; set; }

        [Column("name")]
        public string? Name { get; set; }

        [Column("price")]
        public double Price { get; set; }

        // Saga: las unidades disponibles. Lo cambian las reservas y las
        // compensaciones -ver StockReservations.cs- y PUT /products/{id}/stock.
        [Column("stock")]
        public int Stock { get; set; }
    }
}
