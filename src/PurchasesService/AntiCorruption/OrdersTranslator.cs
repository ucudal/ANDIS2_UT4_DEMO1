using System.Text.Json.Serialization;

namespace Ucu.Andis.PurchasesService.AntiCorruption;

/// <summary>
/// Representa una Order ya traducida: lo que Purchases necesita de ella, todavía
/// con el id del User, que se traduce aparte.
/// </summary>
public record TranslatedOrder(int Number, string UserId, int Units, bool Confirmed);

/// <summary>
/// Representa el traductor del modelo de Orders.
/// </summary>
/// <remarks>
/// Orders habla de <c>user_id</c>, <c>product_id</c> y estados <c>OrdersService.Models.OrderStatus.Pending</c>, <c>OrdersService.Models.OrderStatus.Confirmed</c>,
/// <c>OrdersService.Models.OrderStatus.Rejected</c> y <c>OrdersService.Models.OrderStatus.Cancelled</c>; Purchases no conoce nada de eso.
/// </remarks>
public class OrdersTranslator
{
    private readonly HttpClient _client;

    public OrdersTranslator(HttpClient client) => _client = client;

    public async Task<IReadOnlyList<TranslatedOrder>> ForProductAsync(int productId, CancellationToken cancellationToken)
    {
        // Orders no tiene una consulta por Product: se piden todas y se filtran.
        var orders = await _client.GetFromJsonAsync<List<OrderRecord>>("", cancellationToken) ?? [];

        return orders
            .Where(o => o.ProductId == productId && o.Status is not ("Rejected" or "Cancelled"))
            .Select(o => new TranslatedOrder(o.Id, o.UserId, o.Quantity, o.Status == "Confirmed"))
            .ToList();
    }

    // Una Order tal como la devuelve Orders. Solo se leen los campos que la
    // traducción usa: si Orders agrega campos, la capa no cambia.
    private sealed record OrderRecord(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("user_id")] string UserId,
        [property: JsonPropertyName("product_id")] int ProductId,
        [property: JsonPropertyName("quantity")] int Quantity,
        [property: JsonPropertyName("status")] string Status);
}
