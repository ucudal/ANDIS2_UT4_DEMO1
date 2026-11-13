namespace Ucu.Andis.PurchasesService;

/// <summary>
/// Representa una compra en el modelo de Purchases: quién compró un Product,
/// cuántas unidades y si la compra está confirmada.
/// </summary>
/// <remarks>
/// Una Order rechazada no es una compra.
///
/// Este archivo y <c>Program.cs</c> no conocen el modelo de Orders ni el SOAP de
/// Users: eso queda en la carpeta <c>AntiCorruption</c>.
/// </remarks>
public record Purchase(int Number, string Buyer, int Units, bool Confirmed);

/// <summary>
/// Define de dónde salen las compras.
/// </summary>
/// <remarks>
/// Purchases depende de esta interfaz, y la capa anticorrupción la implementa.
/// </remarks>
public interface IPurchaseSource
{
    Task<IReadOnlyList<Purchase>> ForProductAsync(int productId, CancellationToken cancellationToken);
}
