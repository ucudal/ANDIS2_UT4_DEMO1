#!/usr/bin/env bash
# Circuit Breaker: si Products falla de forma sostenida, Orders deja de
# llamarlo durante un tiempo y responde de inmediato con una respuesta degradada.
# Bajo carga y en Grafana: docker compose run --rm k6 run /scripts/circuit-breaker.js
source "$(dirname "$0")/common.sh"

title "Circuit Breaker"

step "Products falla en el 100 % de las solicitudes"
chaos products 1 0

step "Se consulta 6 veces el detalle de Orders"
for i in 1 2 3 4 5 6; do details; done

step "Products se recupera"
reset_chaos
details

step "Se esperan 16 segundos a que el circuito pase a semiabierto..."
sleep 16
details
