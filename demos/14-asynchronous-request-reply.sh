#!/usr/bin/env bash
# Asynchronous Request-Reply: POST /orders/ responde 202 Accepted enseguida,
# con Location para consultar el estado y Retry-After para indicar cada cuánto.
# Cuando la Order se procesa, el estado responde 303 See Other a la Order.
source "$(dirname "$0")/common.sh"

title "Asynchronous Request-Reply"

step "Se crea una Order a través del gateway"
headers=$(mktemp)
body=$(curl -s -D "$headers" -X POST "$GATEWAY/orders/" -H 'Content-Type: application/json' \
    -d '{"userId": 2, "productId": "60f7c2b5e1d3c2a1b8e4d123", "quantity": 1}')
grep -i -E '^HTTP|^location|^retry-after' "$headers" | tr -d '\r' | sed 's/^/  /'
echo "  $body"
location=$(grep -i '^location' "$headers" | cut -d' ' -f2 | tr -d '\r')

step "Se consulta el estado, esperando lo que indica Retry-After entre consulta y consulta"
started=$(jq -n now)
while :; do
    code=$(curl -s -D "$headers" -o "$headers.body" -w '%{http_code}' "$GATEWAY$location")
    retry_after=$(grep -i '^retry-after' "$headers" | cut -d' ' -f2 | tr -d '\r' || true)
    printf '  %4s s   GET %s -> HTTP %s %s\n' "$(jq -n --argjson s "$started" 'now - $s | . * 10 | round / 10')" \
        "$location" "$code" "$(if [ "$code" = 303 ]; then grep -i '^location' "$headers" | tr -d '\r'; else cat "$headers.body"; fi)"
    [ "$code" = 303 ] && break
    sleep "${retry_after:-2}"
done

step "Se sigue la redirección y se obtiene el resultado"
curl -s -L "$GATEWAY$location" | table
rm -f "$headers" "$headers.body"
