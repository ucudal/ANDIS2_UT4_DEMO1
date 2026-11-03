#!/usr/bin/env bash
# Throttling: Orders atiende como máximo 20 consultas de detalle a la vez. Las
# que exceden ese límite reciben 503 con Retry-After, en lugar de que el
# servicio se degrade para todos. Se lanzan las mismas consultas con el
# Throttling apagado y prendido, para comparar cuántas atiende Orders a la vez.
# Bajo carga y en Grafana: docker compose run --rm k6 run /scripts/throttling.js
source "$(dirname "$0")/common.sh"

# consult: con Orders demorando 5 segundos, lanza 40 consultas de detalle
# simultáneas, las muestra y resume. Mientras tanto, mide cada 0,1 segundos
# cuántas atiende Orders. La demora se quita al terminar, para que no afecte al
# cambio de patrones.
consult() {
    local samples rows sampler
    chaos orders 0 5000
    samples=$(mktemp)
    (while :; do orders_in_progress >> "$samples"; sleep 0.1; done) &
    sampler=$!
    rows=$(concurrent_details 40 lanzamiento)
    kill "$sampler"; wait "$sampler" 2> /dev/null || true
    reset_chaos
    table <<< "$rows"
    count() { jq "map(select(.HTTP == $1)) | length" <<< "$rows"; }
    echo "  Consultas atendidas -200-:  $(count 200)"
    echo "  Consultas rechazadas -503-: $(count 503)"
    note "Orders llegó a atender $(sort -n "$samples" | tail -1) consultas a la vez."
    rm -f "$samples"
}

title "Throttling"

step "Se apagan todos los patrones, también el Throttling"
only_pattern ninguno

warm_up

step "Orders demora 5 segundos en cada consulta; se lanzan 40 consultas de detalle simultáneas"
consult

note "Se esperan 10 segundos, para que en Grafana se distingan las dos tandas..."
sleep 10

step "Se deja prendido solo el Throttling: el Bulkhead no interviene"
only_pattern throttling

step "Orders demora 5 segundos en cada consulta; se lanzan otras 40 consultas de detalle simultáneas"
consult
