#!/usr/bin/env bash
# Bulkhead: las llamadas a cada dependencia tienen su propio cupo de
# concurrencia. Si Products está lento, solo se agota el cupo de Products. Se
# lanzan las mismas consultas con el Bulkhead apagado y prendido, para comparar
# cuántas llamadas atiende Products a la vez.
# Bajo carga y en Grafana: docker compose run --rm k6 run /scripts/bulkhead.js
source "$(dirname "$0")/common.sh"

# consult: lanza 15 consultas de detalle simultáneas, las muestra y resume.
# Mientras tanto, mide cada 0,1 segundos cuántas llamadas atiende Products.
consult() {
    local samples rows sampler
    samples=$(mktemp)
    (while :; do products_active >> "$samples"; sleep 0.1; done) &
    sampler=$!
    rows=$(concurrent_details 15)
    kill "$sampler"; wait "$sampler" 2> /dev/null || true
    table <<< "$rows"
    count() { jq "map(select($1)) | length" <<< "$rows"; }
    echo "  Consultas con todos los productos:   $(count '[.[]] | all(type != "string" or (startswith("no disponible") | not))')"
    echo "  Consultas con productos rechazados:  $(count '[.[]] | any(type == "string" and contains("bulkhead lleno"))')"
    echo "  Consultas sin el nombre del cliente: $(count '.cliente | startswith("no disponible")')"
    echo "  Respuesta más lenta:                 $(jq 'map(.segundos) | max' <<< "$rows")\""
    note "Products llegó a atender $(sort -n "$samples" | tail -1) llamadas a la vez."
    rm -f "$samples"
}

title "Bulkhead"

step "Se apagan todos los patrones, también el Bulkhead"
only_pattern ninguno

warm_up

step "Products responde lento: 1.5\" por solicitud"
chaos products 0 1500

step "Se lanzan 15 consultas de detalle simultáneas"
consult

note "Se esperan 10\", para que en Grafana se distingan las dos tandas..."
sleep 10

step "Se deja prendido solo el Bulkhead"
only_pattern bulkhead

step "Se lanzan otras 15 consultas de detalle simultáneas"
consult
