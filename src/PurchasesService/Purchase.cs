namespace Ucu.Andis.PurchasesService;

// El modelo de Purchases: quién compró un Product, cuántas unidades y si la compra
// está confirmada. Una Order rechazada no es una compra.
//
// Este archivo y Program.cs no conocen el modelo de Orders ni el SOAP de Users:
// eso queda en la carpeta AntiCorruption.
public record Purchase(int Number, string Buyer, int Units, bool Confirmed);

// De dónde salen las compras. Purchases depende de esta interfaz, y la capa
// anticorrupción la implementa.
public interface IPurchaseSource
{
    Task<IReadOnlyList<Purchase>> ForProductAsync(int productId, CancellationToken cancellationToken);
}
