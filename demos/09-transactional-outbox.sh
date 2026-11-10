#!/usr/bin/env bash
# Transactional Outbox: POST /orders/ guarda la Order y el mensaje para RabbitMQ
# en la misma transacción; el relay del outbox los publica después. Si RabbitMQ
# no está, las Orders se aceptan igual y los mensajes esperan en la base.
source "$(dirname "$0")/common.sh"

# Al terminar, RabbitMQ queda levantado aunque el script se interrumpa.
trap 'docker compose start rabbitmq > /dev/null 2>&1; reset_chaos' EXIT

pending() { orders_sql "SELECT id, routing_key, created_at FROM outbox_messages WHERE published_at IS NULL ORDER BY id"; }

title "Transactional Outbox"

step "Se detiene RabbitMQ"
docker compose stop rabbitmq 2>&1 | sed 's/^/  /'

step "Se crean 5 Orders"
ids=""
for i in $(seq 5); do
    response=$(curl -s -w '\n%{http_code}' -X POST "$ORDERS_DIRECT/orders/" -H 'Content-Type: application/json' \
        -d '{"userId": 2, "productId": "60f7c2b5e1d3c2a1b8e4d123", "quantity": 1}')
    id=$(head -1 <<< "$response" | jq -r .id)
    ids="$ids $id"
    echo "  Order $id -> HTTP $(tail -1 <<< "$response")"
done
note "Sin outbox, Orders publicaba después de guardar y respondía 503: la Order quedaba Pending y nadie la procesaba."

step "Salud de Orders: sin RabbitMQ queda Degraded, no Unhealthy"
curl -s "$GATEWAY/health/orders" | jq -c '{status, rabbitmq: .checks.rabbitmq.status}' | sed 's/^/  /'

step "Mensajes pendientes en el outbox"
pending
note "Se esperan 15\" con RabbitMQ detenido, para que se vea en Grafana..."
sleep 15

step "Se vuelve a levantar RabbitMQ"
docker compose start rabbitmq 2>&1 | sed 's/^/  /'
note "Esperando a que el relay publique los mensajes y los workers procesen las Orders..."
until [ "$(orders_sql "SELECT COUNT(*) FROM outbox_messages WHERE published_at IS NULL" | tail -1 | tr -d ' ')" = 0 ]; do sleep 1; done
wait_processed "$ids"

step "Mensajes pendientes en el outbox"
pending
echo "  -ninguno-"

step "Estado de las Orders"
for id in $ids; do curl -s "$ORDERS_DIRECT/orders/$id" | jq -c '{id, status}' | sed 's/^/  /'; done
