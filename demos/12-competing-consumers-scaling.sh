#!/usr/bin/env bash
# Competing Consumers como patrón de rendimiento: el throughput crece con la
# cantidad de consumidores. Se procesa el mismo pico con 3 y con 6 réplicas de
# orders-worker.
# Bajo carga y en Grafana: docker compose run --rm k6 run /scripts/load-leveling.js
source "$(dirname "$0")/common.sh"

ORDERS=30

# Al terminar, vuelven a quedar 3 réplicas.
trap 'docker compose up -d --scale orders-worker=3 orders-worker > /dev/null 2>&1; reset_chaos' EXIT

# Crea las Orders a la vez y devuelve los segundos que tardan en procesarse.
burst() {
    local started ids
    started=$(jq -n now)
    ids=$(seq "$ORDERS" | xargs -P "$ORDERS" -I{} curl -s -X POST "$ORDERS_DIRECT/orders/" \
        -H 'Content-Type: application/json' \
        -d '{"userId": 2, "productId": "60f7c2b5e1d3c2a1b8e4d125", "quantity": 1}' | jq -r .id)
    wait_processed "$ids"
    jq -n --argjson s "$started" 'now - $s | round'
}

title "Competing Consumers: escalar los consumidores"

step "Con 3 réplicas de orders-worker se procesan $ORDERS Orders"
echo "  Procesadas en $(burst) s"

step "Se escala a 6 réplicas"
docker compose up -d --scale orders-worker=6 --wait orders-worker > /dev/null 2>&1
echo "  Réplicas sanas: $(docker compose ps orders-worker --format '{{.Status}}' | grep -c healthy)"
until [ "$(queue_consumers orders.process)" = 6 ]; do sleep 1; done
echo "  Consumidores de orders.process: $(queue_consumers orders.process)"

step "Con 6 réplicas se procesan otras $ORDERS Orders"
echo "  Procesadas en $(burst) s"
note "Cerca de la mitad: el trabajo se reparte entre el doble de consumidores, sin cambiar el código."

step "Se vuelve a 3 réplicas"
docker compose up -d --scale orders-worker=3 --wait orders-worker > /dev/null 2>&1
echo "  Réplicas: $(docker compose ps orders-worker --format '{{.Status}}' | wc -l | tr -d ' ')"
