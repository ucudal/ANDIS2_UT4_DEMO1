#!/usr/bin/env bash
# Gatekeeper: el gateway es el intermediario entre los clientes y los
# servicios. En la entrada HTTPS valida y sanea cada solicitud antes de
# reenviarla: publica solo las rutas de la aplicación, con sus métodos; exige
# JSON; limita el tamaño del cuerpo; y reemplaza los encabezados de identidad.
# Para que sirva, los servicios no tienen que poder alcanzarse sin pasar por
# él: al final se dejan de publicar sus puertos en el host.
source "$(dirname "$0")/common.sh"

# Al terminar, los puertos de los servicios vuelven a publicarse, como los
# necesitan las demás demos.
cleanup() {
    echo
    note "Volviendo a publicar los puertos de los servicios..."
    docker compose up -d --wait > /dev/null 2>&1
    reset_chaos
}
trap cleanup EXIT

load_gateway_cert
alice=$(token alice)
JSON='Content-Type: application/json'

# codes <ruta>: el código HTTP de un GET a la ruta por cada entrada.
codes() {
    printf '  %-36s %-6s %s\n' "$1" \
        "$(curl -s -o /dev/null -w '%{http_code}' "$GATEWAY$1")" \
        "$(curl -s -o /dev/null -w '%{http_code}' --cacert "$GATEWAY_CERT" -H "Authorization: Bearer $alice" "$HTTPS_GATEWAY$1")"
}

title "Gatekeeper"

step "Rutas que no son de la aplicación: código HTTP por cada entrada, con el token de alice"
printf '  %-36s %-6s %s\n' "ruta" "HTTP" "HTTPS"
codes /orders/chaos
codes /orders/patterns
codes /products/cache
codes "/orders/user/1%20OR%201=1/details"
codes /products/no-es-un-id
note "Por HTTP, las rutas de administración responden 200: cualquiera podría hacer fallar un servicio."
note "Por HTTPS, el gateway responde 404 sin llegar a los servicios. Los ids mal formados también."

step "Métodos y tipos de cuerpo"
echo "  DELETE /orders/1, por HTTP y por HTTPS:"
call -X DELETE "$GATEWAY/orders/1"
call -X DELETE -H "Authorization: Bearer $alice" "$HTTPS_GATEWAY/orders/1"
echo "  POST /orders/ con un cuerpo de texto en lugar de JSON:"
call -X POST -H 'Content-Type: text/plain' -d 'hola' "$GATEWAY/orders/"
call -X POST -H 'Content-Type: text/plain' -d 'hola' -H "Authorization: Bearer $alice" "$HTTPS_GATEWAY/orders/"
note "Por HTTP responde Orders; por HTTPS, el gateway, con detail \"Gatekeeper: ...\"."

step "Un cuerpo de 100 KB, por HTTP y por HTTPS"
big=$(mktemp)
head -c 100000 /dev/zero | tr '\0' 'a' > "$big"
call -X POST -H "$JSON" --data-binary @"$big" "$GATEWAY/orders/"
call -X POST -H "$JSON" --data-binary @"$big" -H "Authorization: Bearer $alice" "$HTTPS_GATEWAY/orders/"
rm -f "$big"
note "Por HTTP, Orders recibe los 100 KB y recién ahí descubre que no son una Order."

step "alice manda encabezados de identidad falsos, diciendo ser bob"
call -H "Authorization: Bearer $alice" -H 'X-User-Id: 2' -H 'X-User-Name: bob' \
    "$HTTPS_GATEWAY/orders/user/2/details/view"
note "El gateway reemplaza esos encabezados con los claims del token: para Orders, sigue siendo alice."

step "Pero los servicios también están publicados en el host, y ahí no hay Gatekeeper"
sleep 1
since=$(date +%s)
call "http://localhost:8000/orders/chaos"
call -X POST -H "$JSON" -H 'X-User-Id: 2' -H 'X-User-Name: bob' -H 'X-User-Roles: customer' \
    -d '{"productId": "60f7c2b5e1d3c2a1b8e4d123", "quantity": 1}' "http://localhost:8000/orders/"
sleep 1
docker compose logs --no-log-prefix --since "$since" orders-service | grep '\[FEDERATED IDENTITY\]' | sed 's/^ */  /'
note "Sin token: Orders cree que es bob porque nadie reemplazó los encabezados."
note "Sin pasar por el gateway, cualquiera llega a la administración o se hace pasar por otro."

step "Se dejan de publicar los puertos de los servicios y de las bases"
docker compose -f docker-compose.yaml -f docker-compose.gatekeeper.yaml up -d --wait 2>&1 \
    | grep -E 'Recreated' | sed 's/^ */  /'
call "http://localhost:8000/orders/chaos"
call -H "Authorization: Bearer $alice" "$HTTPS_GATEWAY/orders/user/1/details/view" -o /dev/null
note "La única entrada es el gateway; los servicios siguen hablando entre sí por la red de Docker."
