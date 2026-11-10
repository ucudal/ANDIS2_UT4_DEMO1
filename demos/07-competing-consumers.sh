#!/usr/bin/env bash
# Competing Consumers: tres réplicas de orders-worker consumen la misma cola de
# RabbitMQ y se reparten las Orders. Si una réplica se cae, las otras siguen.
# Tip: mira la cola en la consola de RabbitMQ, http://localhost:15672 -guest/guest-.
# Bajo carga y en Grafana: docker compose run --rm k6 run /scripts/competing-consumers.js
source "$(dirname "$0")/common.sh"

title "Competing Consumers"

# Se crean las Orders directamente en orders-service, sin pasar por el gateway,
# para no chocar con el límite de POST /orders/ del gateway.
create_orders() {
    for i in $(seq "$1"); do
        curl -s -X POST "$ORDERS_DIRECT/orders/" -H 'Content-Type: application/json' \
            -d '{"userId": 2, "productId": "60f7c2b5e1d3c2a1b8e4d125", "quantity": 1}' \
            | grep -o '"id":[0-9]*' | cut -d: -f2
    done
}

wait_until_processed() {
    for id in $1; do
        until curl -s "$ORDERS_DIRECT/orders/$id" | grep -qv '"status":"Pending"'; do sleep 1; done
    done
}

# Muestra qué réplica procesó cada Order: el worker guarda su hostname, que es
# el id del contenedor, en el evento OrderConfirmed.
who_processed() {
    names=$(docker ps --format '{{.ID}} {{.Names}}')
    for id in $1; do
        host=$(curl -s "$ORDERS_DIRECT/orders/$id/events" | grep -o '"ProcessedBy":"[^"]*"' | cut -d'"' -f4)
        name=$(echo "$names" | awk -v h="$host" 'index($1, h) == 1 || index(h, $1) == 1 { print $2 }')
        echo "  Order $id -> ${name:-$host}"
    done
}

step "Se crean 9 Orders; cada worker demora 2\" en procesar una"
ids=$(create_orders 9)
started=$(date +%s)
wait_until_processed "$ids"
echo "  Las 9 Orders se procesaron en $(( $(date +%s) - started ))\"."
who_processed "$ids"

step "Se crean 9 Orders más y se detiene una réplica mientras procesa"
worker=$(docker compose ps --format '{{.Name}}' orders-worker | head -1)
ids=$(create_orders 9)
sleep 1
docker stop "$worker" > /dev/null
in_progress=$(docker logs "$worker" 2>&1 | grep -o 'procesando la orden [0-9]*' | tail -1 | grep -o '[0-9]*$' || true)
echo "  Réplica detenida: $worker, mientras procesaba la Order ${in_progress:-?}"
wait_until_processed "$ids"
who_processed "$ids"

step "Se vuelve a levantar la réplica detenida"
docker start "$worker" > /dev/null
echo "  $worker iniciada"
