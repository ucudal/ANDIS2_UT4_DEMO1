#!/usr/bin/env bash
# Publisher/Subscriber: Orders, Users y Products publican eventos en el exchange
# andis.events. Cada suscriptor tiene su propia cola: un mismo evento llega a
# todos los suscriptores, y el publicador no sabe quiénes son.
source "$(dirname "$0")/common.sh"

PRODUCT=60f7c2b5e1d3c2a1b8e4d125

# Al terminar, el Product y el User recuperan su nombre.
cleanup() {
    update_product "$PRODUCT" "Zapatos" 3500.75
    update_user 2 "bob" "bob@example.com"
    reset_chaos
}
trap cleanup EXIT

# Muestra lo que dejaron en los logs los suscriptores desde un momento dado.
# Con -t, cada línea trae la hora: se ordenan por hora y no por réplica.
subscribers_log() {
    sleep 4
    docker compose logs -t --since "$1" orders-worker orders-projector 2>&1 \
        | grep -E 'recibió|procesando la orden|vista|actualizado' | sort -t'|' -k2 \
        | sed -E 's/^([^ ]+) +\| +[0-9TZ:.-]+ +/  \1  /' || true
}

title "Publisher/Subscriber"

step "Suscripciones al exchange andis.events: cada cola y las claves que recibe"
curl -s -u guest:guest 'http://localhost:15672/api/exchanges/%2F/andis.events/bindings/source' \
    | jq '[.[] | {cola: .destination, clave: .routing_key}]' | table

step "Se crea una Order: el evento order.created llega al worker y a los proyectores"
since=$(date -u +%Y-%m-%dT%H:%M:%SZ)
id=$(create_order 2 "$PRODUCT")
echo "  Order $id creada"
subscribers_log "$since"

step "Products cambia el nombre de un Product y publica product.updated"
since=$(date -u +%Y-%m-%dT%H:%M:%SZ)
update_product "$PRODUCT" "Zapatillas" 3500.75
subscribers_log "$since"

step "Users cambia el nombre de un User y publica user.updated"
since=$(date -u +%Y-%m-%dT%H:%M:%SZ)
update_user 2 "roberto" "bob@example.com"
subscribers_log "$since"
note "El worker no está suscripto a estos eventos: solo los reciben los proyectores."
