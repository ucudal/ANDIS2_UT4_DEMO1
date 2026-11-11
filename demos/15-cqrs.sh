#!/usr/bin/env bash
# CQRS: los comandos -crear y procesar Orders- escriben unas tablas, y la
# consulta de detalle lee otra, order_details, que ya tiene la forma de la
# respuesta. Se compara la consulta síncrona, que llama a Users y a Products,
# con la que lee la vista.
# Bajo carga y en Grafana: docker compose run --rm k6 run -e DETAILS=view /scripts/load.js
source "$(dirname "$0")/common.sh"

# query <ruta>: consulta el detalle de las Orders del User 1 y muestra la tabla,
# el código HTTP y el tiempo de respuesta.
query() {
    local out
    out=$(curl -s -w '\n%{http_code} %{time_total}' "$GATEWAY/orders/user/1/$1")
    table <<< "${out%$'\n'*}"
    read -r code time <<< "${out##*$'\n'}"
    printf '  [HTTP %s, %s s]\n\n' "$code" "$time"
}

# count <ruta> <userId>: cantidad de Orders que devuelve la consulta.
count() { curl -s "$GATEWAY/orders/user/$2/$1" | jq 'length'; }

title "CQRS"

step "La misma consulta por los dos lados"
echo "  GET /orders/user/1/details: lee orders y llama a Users y a Products"
query details
echo "  GET /orders/user/1/details/view: lee solo order_details"
query details/view

step "Products demora 300 ms en responder"
chaos products 0 300
echo "  details: una llamada a Products por Order"
query details
echo "  details/view: no llama a nadie"
query details/view
reset_chaos

step "El costo: consistencia eventual. Se crea una Order y se consulta enseguida"
before=$(count details/view 2)
create_order 2 60f7c2b5e1d3c2a1b8e4d123 > /dev/null
echo "  details:      $(count details 2) Orders del User 2"
echo "  details/view: $(count details/view 2) Orders del User 2"
note "La vista todavía no la tiene: el evento tiene que pasar por el outbox y por los proyectores."
until [ "$(count details/view 2)" -gt "$before" ]; do sleep 0.5; done
echo "  Un momento después, details/view: $(count details/view 2) Orders del User 2"
