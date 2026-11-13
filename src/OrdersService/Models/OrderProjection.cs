using System.Text.Json;

namespace Ucu.Andis.OrdersService.Models;

/// <summary>
/// Proporciona la reconstrucción del estado de una orden reproduciendo sus eventos
/// en orden.
/// </summary>
/// <remarks>
/// Es lo que permite recuperar la tabla <c>orders</c> si se pierde o se corrompe.
/// </remarks>
public static class OrderProjection
{
    public static Order? Replay(IEnumerable<OrderEvent> events)
    {
        Order? order = null;

        foreach (var e in events.OrderBy(e => e.Id))
        {
            switch (e.Type)
            {
                case OrderEventTypes.OrderCreated:
                    var created = JsonSerializer.Deserialize<OrderCreatedData>(e.Data)!;
                    order = new Order
                    {
                        Id = e.OrderId,
                        UserId = created.UserId,
                        ProductId = created.ProductId,
                        Quantity = created.Quantity,
                        Status = OrderStatus.Pending
                    };
                    break;
                case OrderEventTypes.OrderConfirmed when order is not null:
                    order.Status = OrderStatus.Confirmed;
                    break;
                case OrderEventTypes.OrderRejected when order is not null:
                    order.Status = OrderStatus.Rejected;
                    break;
                case OrderEventTypes.OrderCancelled when order is not null:
                    order.Status = OrderStatus.Cancelled;
                    break;
            }
        }

        return order;
    }
}
