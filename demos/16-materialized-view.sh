#!/usr/bin/env bash
# Materialized View: order_details guarda el detalle de cada Order con el nombre
# del cliente y del producto ya copiados. Los proyectores la mantienen al día a
# partir de los eventos, y la consulta la lee sin llamar a Users ni a Products.
# Tres proyectores aplican los eventos en cualquier orden.
source "$(dirname "$0")/common.sh"

PRODUCT=1

# Al terminar, los proyectores quedan levantados y el Product y el User recuperan su
# nombre.
cleanup() {
    docker compose start orders-projector > /dev/null 2>&1
    update_product "$PRODUCT" "Camisa" 1200.50
    update_user "$BOB" bob bob@example.com
    reset_chaos
    reset_patterns
}
trap cleanup EXIT

view() { curl -s "$GATEWAY/orders/user/$ALICE/details/view" | table; }

title "Materialized View"

step "La vista de alice"
view

step "Products cambia el nombre de un Product: la vista lo recibe por product.updated"
update_product "$PRODUCT" "Camisa de lino" 1200.50
sleep 2
view

step "Products falla siempre; se apagan los patrones de resiliencia, que no son los de esta demo"
only_pattern ninguno
chaos products 1 0
echo "  details: arma el detalle en el momento y no consigue los nombres"
curl -s "$GATEWAY/orders/user/$ALICE/details" | table
echo
echo "  details/view: los nombres ya están en la vista"
view
reset_chaos

step "Se detienen los proyectores, Users demora 1,5\" y se crea una Order para bob"
docker compose stop orders-projector 2>&1 | sed 's/^/  /'
chaos users 0 1500
id=$(create_order "$BOB" "$PRODUCT")
wait_processed "$id"
echo "  Order $id: $(curl -s "$ORDERS_DIRECT/orders/$id" | jq -r .status)"
echo "  En la vista: $(curl -s "$GATEWAY/orders/user/$BOB/details/view" | jq --argjson id "$id" 'map(select(.order_id == $id)) | length') filas"
echo "  Eventos esperando en orders.details-view: $(queue_length orders.details-view)"
note "Se esperan 15\" con los proyectores detenidos, para que se vea en Grafana..."
sleep 15

step "Se levantan las 3 réplicas: order.created y order.confirmed llegan a la vez a réplicas distintas"
since=$(date +%s)
docker compose start orders-projector > /dev/null 2>&1
echo "  orders-projector iniciado"
until [ "$(curl -s "$GATEWAY/orders/user/$BOB/details/view" | jq --argjson id "$id" 'map(select(.order_id == $id and .status != "Pending" and .customer_name != null)) | length')" = 1 ]; do sleep 1; done
curl -s "$GATEWAY/orders/user/$BOB/details/view" | jq --argjson id "$id" 'map(select(.order_id == $id))' | table
docker compose logs --since "$since" orders-projector 2>/dev/null | grep -E "Order $id( |:)" | grep -v MESSAGING | sed 's/^/  /'
reset_chaos
note "La réplica que recibe order.confirmed mientras otra espera a Users crea la fila"
note "con el estado final, y la otra completa los nombres sin tocar el estado. Sin el"
note "upsert, el estado se descartaba y la vista quedaba Pending para siempre."

step "Dos cambios del nombre de bob llegan desordenados"
# Las fechas salen del reloj de un contenedor, el mismo que usan los servicios,
# y no del de la máquina, que puede no coincidir.
container_time() { docker compose exec -T rabbitmq date -u -d "$1" '+%Y-%m-%dT%H:%M:%S.%6NZ'; }
newer=$(container_time now)
older=$(container_time '-10 seconds')
publish_event user.updated "{\"UserId\": 2, \"Name\": \"Roberto\", \"OccurredAt\": \"$newer\"}"
sleep 1
publish_event user.updated "{\"UserId\": 2, \"Name\": \"Bob\", \"OccurredAt\": \"$older\"}"
sleep 2
echo "  Llegan 'Roberto', de ahora, y después 'Bob', de 10\" antes"
curl -s "$GATEWAY/orders/user/$BOB/details/view" | table
note "Se publican directamente en RabbitMQ, como si Users los hubiera publicado. La"
note "vista guarda la fecha de cada nombre y descarta 'Bob', que es más viejo. Sin"
note "esa fecha ganaría el último en llegar, y con varias réplicas el orden de"
note "llegada no es el de los cambios."
