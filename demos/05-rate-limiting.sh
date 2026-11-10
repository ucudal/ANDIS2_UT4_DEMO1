#!/usr/bin/env bash
# Rate Limiting: un proceso por lotes regula el ritmo al que envía solicitudes
# para no superar el límite del servicio al que llama, en lugar de enviar todo
# de golpe y reintentar las rechazadas. El servicio es el gateway, que acepta
# como máximo 1 POST /orders/ por segundo por IP, con una ráfaga de 5.
# Bajo carga y en Grafana: docker compose run --rm k6 run /scripts/rate-limiting.js
source "$(dirname "$0")/common.sh"

ORDERS=15

# Crea una Order a través del gateway y agrega a rows una fila con el segundo
# en que se envió, contado desde started, el número de Order dentro del lote,
# el número de intento y el código HTTP.
rows=""
send_order() {
    local t code
    t=$(jq -n --argjson s "$started" '(now - $s) * 10 | floor / 10')
    code=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$GATEWAY/orders/" \
        -H 'Content-Type: application/json' \
        -d '{"userId": 2, "productId": "60f7c2b5e1d3c2a1b8e4d123", "quantity": 1}')
    rows+="{\"segundo\": $t, \"Order\": $1, \"intento\": $2, \"HTTP\": $code}"$'\n'
}

# Muestra las filas como tabla y un resumen.
report() {
    jq -s . <<< "$rows" | table
    echo
    echo "  Solicitudes: $(jq -s length <<< "$rows"); rechazadas con 429: $(jq -s 'map(select(.HTTP == 429)) | length' <<< "$rows"); tiempo: $(jq -n --argjson s "$started" "now - \$s | round")\""
}

title "Rate Limiting"

step "Sin Rate Limiting: se envían $ORDERS Orders de golpe y se reintentan las rechazadas cada 1\""
pending=$(seq "$ORDERS")
attempt=1
started=$(jq -n now)
while [ -n "$pending" ]; do
    rows_before=$rows
    for i in $pending; do send_order "$i" "$attempt"; done
    pending=$(jq -r --argjson a "$attempt" 'select(.intento == $a and .HTTP == 429) | .Order' <<< "${rows#"$rows_before"}")
    attempt=$((attempt + 1))
    if [ -n "$pending" ]; then sleep 1; fi
done
report

note "Se esperan 10\", para que el gateway vuelva a admitir una ráfaga y en Grafana se distingan las dos tandas..."
sleep 10

step "Con Rate Limiting: se envían $ORDERS Orders, una ráfaga de 5 y después 1 por segundo"
rows=""
started=$(jq -n now)
for i in $(seq "$ORDERS"); do
    if [ "$i" -gt 5 ]; then sleep 1; fi
    send_order "$i" 1
done
report
