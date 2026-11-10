#!/usr/bin/env bash
# Health Endpoint Monitoring: cada servicio expone /health con el estado de sus
# dependencias, y Docker lo usa para saber si el contenedor está sano.
# Tip: corre ./demos/monitor-health.sh en otra terminal mientras tanto.
source "$(dirname "$0")/common.sh"

title "Health Endpoint Monitoring"

health() {
    for s in users products orders; do
        printf '  %-9s ' "$s"
        curl -s -w '  [HTTP %{http_code}]\n' "$GATEWAY/health/$s"
    done
}

docker_status() {
    docker compose ps --format '{{.Service}}: {{.Status}}' products-service orders-service | sed 's/^/  /'
}

step "Estado inicial"
health

step "Se detiene la base de datos de Products"
docker compose stop products-db 2>&1 | sed 's/^/  /'
sleep 5
health

step "Se esperan 15\" a que Docker marque el contenedor como unhealthy..."
sleep 15
docker_status

step "Se vuelve a levantar la base de datos de Products"
docker compose start products-db 2>&1 | sed 's/^/  /'
note "Esperando a que Products vuelva a estar sano..."
until curl -sf "$GATEWAY/health/products" > /dev/null; do sleep 2; done
sleep 6
health
docker_status
