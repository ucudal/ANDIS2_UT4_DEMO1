#!/usr/bin/env bash
# Asynchronous Request-Reply: POST /orders/ responde 202 Accepted enseguida,
# con Location para consultar el estado y Retry-After para indicar cada cuánto.
# Cuando la Order se procesa, el estado responde 303 See Other a la Order.
# Con el encabezado Idempotency-Key, un reintento no crea otra Order.
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

# post_with_key <clave> <cuerpo>: POST /orders/ con Idempotency-Key; muestra el
# código, los encabezados que importan y el cuerpo.
post_with_key() {
    curl -s -D "$headers" -o "$headers.body" -X POST "$GATEWAY/orders/" -H 'Content-Type: application/json' \
        -H "Idempotency-Key: $1" -d "$2" > /dev/null
    grep -i -E '^HTTP|^location|^idempotent-replayed' "$headers" | tr -d '\r' | sed 's/^/  /'
    echo "  $(cat "$headers.body")"
}

headers=$(mktemp)
key=$(uuidgen | tr '[:upper:]' '[:lower:]')
body='{"userId": 2, "productId": "60f7c2b5e1d3c2a1b8e4d123", "quantity": 1}'

step "Se crea una Order con Idempotency-Key: $key"
post_with_key "$key" "$body"

step "El cliente no recibió la respuesta y reintenta con la misma clave"
post_with_key "$key" "$body"
note "La misma Order, sin crear otra: Idempotent-Replayed avisa que es una repetición."
note "Sin la clave, el reintento habría creado una segunda Order, con su propio pedido al ERP."

step "La misma clave con otra cantidad"
post_with_key "$key" '{"userId": 2, "productId": "60f7c2b5e1d3c2a1b8e4d123", "quantity": 3}'
note "422: la clave ya se usó para otra Order."
rm -f "$headers" "$headers.body"
