#!/usr/bin/env bash
# Cache-Aside: Products busca primero en Redis y, si no está, lee MongoDB y lo
# guarda en Redis por 60 segundos. Al cambiar un Product, borra su clave.
# Bajo carga y en Grafana: docker compose run --rm k6 run /scripts/cache-aside.js
source "$(dirname "$0")/common.sh"

PRODUCT=1

# Al terminar, la caché vuelve a sus valores por defecto, Redis queda levantado
# y el Product recupera su nombre.
cleanup() {
    docker compose start redis > /dev/null 2>&1
    curl -s -X DELETE "$GATEWAY/products/cache" > /dev/null
    update_product "$PRODUCT" "Camisa" 1200.50
    reset_chaos
}
trap cleanup EXIT

# Lee el Product y muestra su nombre, el tiempo de respuesta y la línea de log
# de la caché.
read_product() {
    local since out
    since=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    out=$(curl -s -w '\n%{time_total}' "$GATEWAY/products/$PRODUCT")
    printf '  %-16s %s s   ' "$(head -1 <<< "$out" | jq -r .name)" "$(tail -1 <<< "$out")"
    sleep 1
    docker compose logs --since "$since" products-service 2>&1 | grep -o '\[CACHE-ASIDE\] \(hit\|miss\|Redis\).*' | head -1 || echo
}

title "Cache-Aside"

step "Se simula una base más lenta: cada lectura de MongoDB demora 200 ms"
curl -s -X PUT "$GATEWAY/products/cache" -H 'Content-Type: application/json' -d '{"enabled": true, "dbDelayMs": 200}' \
    | sed 's/^/  /'; echo
docker compose exec -T redis redis-cli DEL "product:$PRODUCT" > /dev/null

step "Se lee el mismo Product 3 veces: la primera es un miss, las siguientes, hits"
for i in 1 2 3; do read_product; done

step "Se cambia el nombre del Product: Products borra su clave de Redis"
update_product "$PRODUCT" "Camisa de lino" 1200.50
read_product
read_product

step "Se detiene Redis: Products sigue respondiendo, desde MongoDB"
docker compose stop redis 2>&1 | sed 's/^/  /'
read_product
read_product

step "Se vuelve a levantar Redis"
docker compose start redis 2>&1 | sed 's/^/  /'
sleep 2
read_product
read_product
