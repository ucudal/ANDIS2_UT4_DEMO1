#!/usr/bin/env bash
# Transactional Outbox: POST /orders/ guarda la Order y el mensaje para RabbitMQ
# en la misma transacción; el relay del outbox los publica después. Si RabbitMQ
# no está, las Orders se aceptan igual y los mensajes esperan en la base.
# Competing Consumers: la entrega es al menos una vez, y dos copias del mismo
# mensaje que llegan a la vez a dos workers confirman la Order una sola vez.
source "$(dirname "$0")/common.sh"

# Al terminar, RabbitMQ y Products quedan funcionando aunque el script se
# interrumpa.
trap 'docker compose start rabbitmq > /dev/null 2>&1; docker compose unpause products-service > /dev/null 2>&1; reset_chaos' EXIT

pending() { orders_sql "SELECT id, routing_key, created_at FROM outbox_messages WHERE published_at IS NULL ORDER BY id"; }

title "Transactional Outbox"

step "Se detiene RabbitMQ"
docker compose stop rabbitmq 2>&1 | sed 's/^/  /'

step "Se crean 5 Orders"
ids=""
for i in $(seq 5); do
    response=$(curl -s -w '\n%{http_code}' -X POST "$ORDERS_DIRECT/orders/" -H 'Content-Type: application/json' \
        -d "{\"userId\": \"$BOB\", \"productId\": 1, \"quantity\": 1}")
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
outbox_empty() { [ "$(orders_sql "SELECT COUNT(*) FROM outbox_messages WHERE published_at IS NULL" | tail -1 | tr -d ' ')" = 0 ]; }
until outbox_empty; do sleep 1; done
wait_processed "$ids"
# Los workers dejan order.confirmed en el outbox: se espera a que se publique.
until outbox_empty; do sleep 1; done

step "Mensajes pendientes en el outbox"
pending
echo "  -ninguno-"

step "Estado de las Orders"
for id in $ids; do curl -s "$ORDERS_DIRECT/orders/$id" | jq -c '{id, status}' | sed 's/^/  /'; done

# Products también entrega con un outbox -ver la demo 10-: puede publicar dos
# veces el mismo stock.reserved, que es lo que hace que el worker confirme una
# Order. Products se pone en pausa para que la Order quede Pending y las dos
# copias simuladas lleguen antes que la reserva real.
step "Se simula que Products publica dos veces el stock.reserved de una Order nueva"
docker compose pause products-service 2>&1 | sed 's/^/  /'
dup=$(create_order "$BOB" 1)
echo "  Order $dup -> $(curl -s "$ORDERS_DIRECT/orders/$dup" | jq -r .status)"
for i in 1 2; do publish_event stock.reserved "{\"OrderId\": $dup, \"ProductId\": 1, \"Quantity\": 1}"; done
note "Es lo que pasa si el relay se cae después de publicar y antes de marcar el mensaje."
wait_processed "$dup"
docker compose unpause products-service 2>&1 | sed 's/^/  /'
note "Products sigue: reserva el stock y publica su stock.reserved, que el worker ignora"
note "porque la Order ya no está Pending."
until outbox_empty; do sleep 1; done

step "La Order $dup: dos copias de stock.reserved, una sola confirmación"
# La réplica que descarta la Order lo registra después de que la otra confirma.
for i in $(seq 10); do
    discarded=$(docker compose logs --since 2m orders-worker 2>/dev/null | grep "descarta la orden $dup:" || true)
    [ -n "$discarded" ] && break
    sleep 1
done
# Con -t, cada línea trae la hora: se ordenan por hora y no por réplica.
docker compose logs -t --since 2m orders-worker 2>/dev/null | grep -E "orden $dup( |:|$)" \
    | sort -t'|' -k2 | sed -E 's/\| [0-9TZ:.-]+ +/| /; s/^/  /'
echo "  Eventos de la Order $dup:"
curl -s "$ORDERS_DIRECT/orders/$dup/events" | jq -r '.events[] | "    \(.type)"'
echo "  Mensajes de la Order $dup en el outbox:"
orders_sql "SELECT routing_key, COUNT(*) AS mensajes FROM outbox_messages
    WHERE JSON_EXTRACT(payload, '\$.OrderId') = $dup GROUP BY routing_key"
note "Las dos réplicas leyeron la Order como Pending; el UPDATE condicional dejó"
note "confirmarla a una sola. Sin él, habría dos OrderConfirmed y dos order.confirmed."
