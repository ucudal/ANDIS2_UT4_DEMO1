#!/usr/bin/env bash
# Event Sourcing: cada cambio de una Order se guarda como un evento inmutable.
# La tabla orders es solo una proyección y se puede reconstruir desde los eventos.
source "$(dirname "$0")/common.sh"

title "Event Sourcing"

step "Se crea una Order y se espera a que un worker la procese"
id=$(curl -s -X POST "$ORDERS_DIRECT/orders/" -H 'Content-Type: application/json' \
    -d "{\"userId\": \"$BOB\", \"productId\": 2, \"quantity\": 3}" | grep -o '"id":[0-9]*' | cut -d: -f2)
until curl -s "$ORDERS_DIRECT/orders/$id" | grep -qv '"status":"Pending"'; do sleep 1; done
echo "  Orden $id creada y procesada"

step "Eventos de la Order $id y estado que resulta de reproducirlos"
events=$(curl -s "$GATEWAY/orders/$id/events")
table .events <<< "$events"
echo
table .replayed_state <<< "$events"

step "Se simula la pérdida de la proyección: se borra la tabla orders"
docker compose exec -T orders-db mysql -u ordersusr -pPa55w0rd ordersdb \
    -e "DELETE FROM orders" 2>/dev/null
echo "  GET /orders/ -> $(curl -s "$GATEWAY/orders/")"

step "Se reconstruye la proyección a partir de los eventos"
curl -s -X POST "$GATEWAY/orders/projections/rebuild" | table
echo "  GET /orders/$id:"
curl -s "$GATEWAY/orders/$id" | table
