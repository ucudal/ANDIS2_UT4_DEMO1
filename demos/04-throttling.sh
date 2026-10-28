#!/usr/bin/env bash
# Throttling: Orders atiende como máximo 20 consultas de detalle a la vez. Las
# que exceden ese límite reciben 503 con Retry-After, en lugar de que el
# servicio se degrade para todos.
# Bajo carga y en Grafana: docker compose run --rm k6 run /scripts/throttling.js
source "$(dirname "$0")/common.sh"

title "Throttling"

warm_up

step "Orders demora 2 segundos en cada consulta"
chaos orders 0 2000

step "Se lanzan 40 consultas de detalle simultáneas"
rows=$(concurrent_details 40 lanzamiento)
table <<< "$rows"

step "Resumen"
count() { jq "map(select(.HTTP == $1)) | length" <<< "$rows"; }
echo "  Consultas atendidas -200-:  $(count 200)"
echo "  Consultas rechazadas -503-: $(count 503)"
