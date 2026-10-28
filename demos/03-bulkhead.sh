#!/usr/bin/env bash
# Bulkhead: las llamadas a cada dependencia tienen su propio cupo de
# concurrencia. Si Products está lento, solo se agota el cupo de Products.
# Bajo carga y en Grafana: docker compose run --rm k6 run /scripts/bulkhead.js
source "$(dirname "$0")/common.sh"

title "Bulkhead"

warm_up

step "Products responde lento: 1,5 segundos por solicitud"
chaos products 0 1500

step "Se lanzan 15 consultas de detalle simultáneas"
rows=$(concurrent_details 15)
table <<< "$rows"

step "Resumen"
count() { jq "map(select($1)) | length" <<< "$rows"; }
echo "  Consultas con todos los productos:   $(count '[.[]] | all(type != "string" or (startswith("no disponible") | not))')"
echo "  Consultas con productos rechazados:  $(count '[.[]] | any(type == "string" and contains("bulkhead lleno"))')"
echo "  Consultas sin el nombre del cliente: $(count '.cliente | startswith("no disponible")')"
