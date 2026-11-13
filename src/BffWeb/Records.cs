using System.Text.Json.Serialization;

namespace Ucu.Andis.BffWeb;

sealed record OrderRecord(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("product_id")] int ProductId,
    [property: JsonPropertyName("quantity")] int Quantity,
    [property: JsonPropertyName("status")] string Status);

sealed record ProductRecord(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("price")] double Price);
