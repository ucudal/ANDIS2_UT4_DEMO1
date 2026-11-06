#!/usr/bin/env bash
# Messaging Bridge: un ERP legado usa otro broker, ActiveMQ, y otro formato,
# registros de ancho fijo. El puente reenvía las Orders confirmadas de RabbitMQ a
# ActiveMQ, y las facturas en sentido contrario. Ni Orders ni el ERP cambian, y
# si un lado no está, los mensajes esperan.
#
# Bajo carga y en Grafana: ./demos/load-modifiability.sh bridge
source "$(dirname "$0")/common.sh"

cleanup() {
    docker compose start legacy-broker > /dev/null 2>&1 || true
    reset_chaos
}
trap cleanup EXIT

PRODUCT=60f7c2b5e1d3c2a1b8e4d124

# invoice <id>: espera hasta 20 segundos a que la Order tenga su factura.
invoice() {
    for _ in $(seq 20); do
        curl -s "$ORDERS_DIRECT/orders/$1/events" | jq -e '.events[] | select(.type == "OrderInvoiced")' > /dev/null && break
        sleep 1
    done
}

history() {
    curl -s "$ORDERS_DIRECT/orders/$1/events" | table '[.events[] | {type, data: (.data | tostring)}]'
}

logs_since() {
    docker compose logs --no-log-prefix --timestamps --since "$1" messaging-bridge legacy-erp 2>&1 \
        | grep -E '\[(MESSAGING BRIDGE|ERP)\]' | grep -v Escuchando | sort \
        | cut -d' ' -f2- | sed 's/^ */  /'
}

title "Messaging Bridge"

sleep 1
since=$(date +%s)

step "Una Order confirmada llega al ERP, y su factura vuelve"
id=$(create_order 2 "$PRODUCT")
wait_processed "$id"
invoice "$id"
history "$id"
note "Orders no llamó a nadie: publicó su evento en RabbitMQ, como siempre."

step "Lo que pasó por el puente"
logs_since "$since"
echo
note "El JSON de RabbitMQ se volvió un registro de ancho fijo en ActiveMQ."
note "El ERP solo conoce ActiveMQ y sus registros: no sabe que existe RabbitMQ."

step "Se detiene el broker del ERP y se crea otra Order"
annotate "ActiveMQ detenido"
docker compose stop legacy-broker > /dev/null 2>&1
id=$(create_order 2 "$PRODUCT")
wait_processed "$id"
sleep 2
history "$id"
echo "  Mensajes esperando en la cola bridge.erp de RabbitMQ: $(queue_length bridge.erp)"
note "La Order se confirmó igual: la aplicación no depende del ERP."

step "Vuelve el broker del ERP"
sleep 1
since=$(date +%s)
annotate "ActiveMQ vuelve"
docker compose start legacy-broker > /dev/null 2>&1
note "Se espera a que el puente se reconecte y entregue el mensaje..."
invoice "$id"
history "$id"
echo "  Mensajes esperando en la cola bridge.erp de RabbitMQ: $(queue_length bridge.erp)"
sleep 1
logs_since "$since"
note "Nada se perdió. La entrega es al menos una vez: si se repite un pedido,"
note "el ERP emite la misma factura y Orders no la registra dos veces."
