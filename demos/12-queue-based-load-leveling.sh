#!/usr/bin/env bash
# Queue-Based Load Leveling: POST /orders/ solo guarda la Order y responde; la
# cola orders.process absorbe el pico y los workers lo procesan a su ritmo.
# Bajo carga y en Grafana: docker compose run --rm k6 run /scripts/load-leveling.js
source "$(dirname "$0")/common.sh"

ORDERS=30

title "Queue-Based Load Leveling"

step "Se crean $ORDERS Orders a la vez, directamente en orders-service"
note "El gateway admite 1 POST por segundo por cliente: rechazaría el pico antes de llegar a la cola."
dir=$(mktemp -d)
started=$(jq -n now)
(cd "$dir" && seq "$ORDERS" | xargs -P "$ORDERS" -I{} curl -s -o {}.body -w '%{http_code} %{time_total}\n' \
    -X POST "$ORDERS_DIRECT/orders/" -H 'Content-Type: application/json' \
    -d "{\"userId\": \"$BOB\", \"productId\": 2, \"quantity\": 1}" > times)
ids=$(cat "$dir"/*.body | jq -r .id)
echo "  Respuestas: $(cut -d' ' -f1 "$dir/times" | sort | uniq -c | awk '{print $1 " con HTTP " $2}' | paste -sd, -)"
echo "  POST más lento: $(cut -d' ' -f2 "$dir/times" | sort -n | tail -1)\""
rm -rf "$dir"

step "La cola absorbe el pico y los workers la vacían a su ritmo"
note "3 workers, 2\" por Order: unas 1.5 Orders por segundo, con cualquier pico."
sleep 2
while :; do
    length=$(queue_length orders.process)
    printf '  %4s s   orders.process: %3s mensajes\n' "$(jq -n --argjson s "$started" 'now - $s | round')" "$length"
    [ "$length" = 0 ] && break
    sleep 3
done
wait_processed "$ids"
echo "  Las $ORDERS Orders se procesaron en $(jq -n --argjson s "$started" 'now - $s | round')\"."
note "Users y Products nunca recibieron más de 3 llamadas a la vez del worker, una por réplica."
