using System.Text.Json.Serialization;

namespace Ucu.Andis.BffMobile;

sealed record DetailRecord(
    [property: JsonPropertyName("product_name")] string ProductName,
    [property: JsonPropertyName("status")] string Status);
