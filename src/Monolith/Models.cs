using System.Runtime.Serialization;

namespace Ucu.Andis.Monolith;

[DataContract(Namespace = "http://ucu.edu.uy/andis/users")]
public class User
{
    [DataMember] public string Id { get; set; } = string.Empty;
    [DataMember] public string Name { get; set; } = string.Empty;
    [DataMember] public string Email { get; set; } = string.Empty;
}

public class Product
{
    public int Id { get; set; }
    public string? Name { get; set; }
    public double Price { get; set; }
}

public class Order
{
    public int Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public string Status { get; set; } = "Confirmed";
}
