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
    }
}
