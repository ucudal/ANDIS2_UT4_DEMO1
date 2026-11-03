#!/usr/bin/env bash
# Materialized View: order_details guarda el detalle de cada Order con el nombre
# del cliente y del producto ya copiados. El proyector la mantiene al día a
# partir de los eventos, y la consulta la lee sin llamar a Users ni a Products.
source "$(dirname "$0")/common.sh"

PRODUCT=60f7c2b5e1d3c2a1b8e4d123

# Al terminar, el proyector queda levantado y el Product recupera su nombre.
cleanup() {
    docker compose start orders-projector > /dev/null 2>&1
    update_product "$PRODUCT" "Camisa" 1200.50
    reset_chaos
    reset_patterns
}
trap cleanup EXIT

view() { curl -s "$GATEWAY/orders/user/1/details/view" | table; }

title "Materialized View"

step "La vista del User 1"
view

step "Products cambia el nombre de un Product: la vista lo recibe por product.updated"
update_product "$PRODUCT" "Camisa de lino" 1200.50
sleep 2
view

step "Products falla siempre; se apagan los patrones de resiliencia, que no son los de esta demo"
only_pattern ninguno
chaos products 1 0
echo "  details: arma el detalle en el momento y no consigue los nombres"
curl -s "$GATEWAY/orders/user/1/details" | table
echo
echo "  details/view: los nombres ya están en la vista"
view
reset_chaos

step "Se detiene el proyector y se crea una Order para el User 2"
docker compose stop orders-projector 2>&1 | sed 's/^/  /'
id=$(create_order 2 "$PRODUCT")
wait_processed "$id"
echo "  Order $id: $(curl -s "$ORDERS_DIRECT/orders/$id" | jq -r .status)"
echo "  En la vista: $(curl -s "$GATEWAY/orders/user/2/details/view" | jq --argjson id "$id" 'map(select(.order_id == $id)) | length') filas"
echo "  Eventos esperando en orders.details-view: $(queue_length orders.details-view)"
note "Se esperan 15 segundos con el proyector detenido, para que se vea en Grafana..."
sleep 15

step "Se vuelve a levantar el proyector: procesa los eventos pendientes y la vista se pone al día"
docker compose start orders-projector > /dev/null 2>&1
echo "  orders-projector iniciado"
until [ "$(curl -s "$GATEWAY/orders/user/2/details/view" | jq --argjson id "$id" 'map(select(.order_id == $id and .status != "Pending")) | length')" = 1 ]; do sleep 1; done
curl -s "$GATEWAY/orders/user/2/details/view" | jq --argjson id "$id" 'map(select(.order_id == $id))' | table
