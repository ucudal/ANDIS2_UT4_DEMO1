#!/usr/bin/env bash
# Saga: una Order pasa por tres servicios -Products reserva el stock, Orders
# verifica el User y confirma, el ERP factura-, cada uno con su propia base y
# sin una transacción que los abarque. Si un paso falla, los pasos anteriores se
# deshacen con compensaciones: Products libera el stock que había reservado.
# Nadie coordina: cada servicio reacciona a los eventos de los otros.
source "$(dirname "$0")/common.sh"

PRODUCT=3
initial_stock=$(curl -s "$GATEWAY/products/$PRODUCT" | jq -r .stock)
set_stock() {
    curl -s -X PUT "$GATEWAY/products/$PRODUCT/stock" -H 'Content-Type: application/json' -d "{\"stock\": $1}" > /dev/null
}
# Al terminar, el Product vuelve a su stock aunque el script se interrumpa.
trap 'set_stock "$initial_stock"' EXIT

stock() { echo "  Stock del Product $PRODUCT: $(curl -s "$GATEWAY/products/$PRODUCT" | jq -r .stock)"; }
events() {
    curl -s "$ORDERS_DIRECT/orders/$1/events" \
        | jq -r '.events[] | "    \(.type)\(if .data.Reason then ": \(.data.Reason)" elif .data.InvoiceNumber then ": \(.data.InvoiceNumber)" else "" end)"'
}
reservation() {
    products_sql "SELECT order_id, quantity, status, reason FROM stock_reservations WHERE order_id = $1"
}
# wait_event <id> <tipo>: espera a que la Order tenga ese evento en su historial.
wait_event() {
    until curl -s "$ORDERS_DIRECT/orders/$1/events" | jq -e --arg t "$2" 'any(.events[]; .type == $t)' > /dev/null; do sleep 1; done
}

title "Saga"

step "Se fija el stock del Product $PRODUCT en 20 unidades"
set_stock 20
stock

step "1. bob pide 2 unidades: todos los pasos salen bien"
ok=$(create_order "$BOB" $PRODUCT 2)
note "Products reserva el stock, Orders confirma la Order y el ERP la factura..."
wait_event "$ok" OrderInvoiced
echo "  Eventos de la Order $ok:"
events "$ok"
reservation "$ok"
stock

note "Se esperan 10\", para que en Grafana se distingan los casos..."
sleep 10

step "2. bob pide 50 unidades: Products no tiene stock"
short=$(create_order "$BOB" $PRODUCT 50)
wait_processed "$short"
echo "  Eventos de la Order $short:"
events "$short"
reservation "$short"
stock
note "Products publicó stock.rejected en lugar de stock.reserved: no hay nada que compensar."

note "Se esperan 10\", para que en Grafana se distingan los casos..."
sleep 10

step "3. Un User que no existe pide 3 unidades: Orders la rechaza después de la reserva"
missing=$(create_order 000000000000000000000000 $PRODUCT 3)
wait_processed "$missing"
until products_sql "SELECT status FROM stock_reservations WHERE order_id = $missing" | grep -q Released; do sleep 1; done
echo "  Eventos de la Order $missing:"
events "$missing"
reservation "$missing"
stock
note "Products ya había descontado las 3 unidades. Con order.rejected las devolvió:"
note "es la compensación del primer paso."

note "Se esperan 10\", para que en Grafana se distingan los casos..."
sleep 10

step "4. bob pide 12 unidades: el ERP no factura pedidos de más de 10"
erp=$(create_order "$BOB" $PRODUCT 12)
note "Orders confirma la Order, el ERP la rechaza y Orders la cancela..."
wait_event "$erp" OrderCancelled
until products_sql "SELECT status FROM stock_reservations WHERE order_id = $erp" | grep -q Released; do sleep 1; done
echo "  Eventos de la Order $erp:"
events "$erp"
reservation "$erp"
stock
note "La Order ya estaba confirmada, así que no se rechaza: se cancela, y con"
note "order.cancelled Products devuelve el stock. Las compensaciones van en orden"
note "inverso: el último paso que salió bien se deshace primero."

note "Se esperan 10\", para que en Grafana se distingan los casos..."
sleep 10

step "Mensajes de la Saga, en orden"
docker compose logs -t --since 3m products-service orders-worker 2>/dev/null \
    | grep -E "\[SAGA\].*(Order|orden) ($ok|$short|$missing|$erp)( |:|$|\])" \
    | sort -t'|' -k2 | sed -E 's/\| [0-9TZ:.-]+ +/| /; s/^/  /'

step "Idempotencia: se publican otra vez order.created de la Order $ok y order.rejected de la $missing"
publish_event order.created "{\"OrderId\": $ok, \"UserId\": \"$BOB\", \"ProductId\": $PRODUCT, \"Quantity\": 2}"
publish_event order.rejected "{\"OrderId\": $missing, \"Reason\": \"copia\"}"
sleep 3
docker compose logs --since 10s products-service 2>/dev/null | grep -E "\[SAGA\].*Order ($ok|$missing)" | sed -E 's/\| +/| /; s/^/  /'
stock
note "La reserva tiene como clave la Order: la segunda inserción falla y deshace el"
note "descuento. La liberación es un UPDATE que solo cambia una reserva Reserved:"
note "la copia no encuentra nada que liberar. El stock no cambia."
