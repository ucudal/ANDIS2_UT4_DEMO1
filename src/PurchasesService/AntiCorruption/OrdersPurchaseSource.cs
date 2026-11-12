namespace Ucu.Andis.PurchasesService.AntiCorruption;

// La fachada de la capa: arma las compras de un Product con las Orders traducidas
// y el nombre de cada comprador. Es lo único de la capa que Purchases usa, a través
// de IPurchaseSource.
public class OrdersPurchaseSource : IPurchaseSource
{
    private readonly OrdersTranslator _orders;
    private readonly UsersTranslator _users;

    public OrdersPurchaseSource(OrdersTranslator orders, UsersTranslator users)
    {
        _orders = orders;
        _users = users;
    }

    public async Task<IReadOnlyList<Purchase>> ForProductAsync(int productId, CancellationToken cancellationToken)
    {
        var orders = await _orders.ForProductAsync(productId, cancellationToken);

        // Un comprador puede tener varias compras: se le pregunta a Users una sola
        // vez por cada uno.
        var buyers = new Dictionary<string, string>();
        foreach (var userId in orders.Select(o => o.UserId).Distinct())
            buyers[userId] = await _users.NameAsync(userId, cancellationToken);

        return orders
            .Select(o => new Purchase(o.Number, buyers[o.UserId], o.Units, o.Confirmed))
            .ToList();
    }
}
