#!/usr/bin/env bash
# Strangler Fig: una fachada recibe todas las solicitudes y desvía, ruta por
# ruta, del monolito a los microservicios. Los clientes no cambian, y al final el
# monolito se apaga sin que nadie lo note.
#
# Bajo carga y en Grafana: ./demos/load-modifiability.sh strangler
source "$(dirname "$0")/common.sh"

PRODUCT=1

cleanup() {
    echo
    note "Volviendo la fachada al monolito..."
    curl -s -o /dev/null -X PUT "$GATEWAY/products/$PRODUCT" -H 'Content-Type: application/json' \
        -d '{"name": "Camisa", "price": 1200.50}'
    docker compose up -d --force-recreate --wait monolith > /dev/null 2>&1
    strangler_route users monolith:8000
    strangler_route products monolith:8000
    strangler_route orders monolith:8000
    reset_chaos
}
trap cleanup EXIT

SOAP="<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\"><soap:Body><GetUser xmlns=\"http://ucu.edu.uy/andis/users\"><id>$ALICE</id></GetUser></soap:Body></soap:Envelope>"

# served <etiqueta> <ruta> [argumentos de curl]: el código HTTP, quién atendió la
# solicitud y un resumen de la respuesta.
served() {
    local label=$1 path=$2 out summary
    shift 2
    out=$(curl -s -D - "$@" "$FACADE$path" | tr -d '\r')
    summary=$(sed '1,/^$/d' <<< "$out" | { grep -oE '[Nn]ame("?:"|>)[^",<]*' || true; } | head -2 | sed -E 's/.*[:>]"?//' | paste -sd, - | sed 's/,$//; s/,/, /g')
    printf '  %-9s HTTP %s  %-22s %s\n' "$label" "$(head -1 <<< "$out" | awk '{ print $2 }')" \
        "$(grep -i '^x-served-by:' <<< "$out" | awk '{ print $2 }')" "$summary"
}

# routes: las tres rutas, por la fachada, con las mismas solicitudes de siempre.
routes() {
    served users /users/UserService.svc -X POST -H 'Content-Type: text/xml; charset=utf-8' \
        -H 'SOAPAction: "http://ucu.edu.uy/andis/users/IUserService/GetUser"' -d "$SOAP"
    served products "/products/$PRODUCT"
    served orders /orders/user/$ALICE/details
}

title "Strangler Fig"

sleep 1
since=$(date +%s)

step "Al principio, el monolito atiende todo"
routes
note "Un solo proceso y una sola base: el detalle de una Order es un JOIN."

step "Se migra Products: se cambia una línea de la tabla de rutas y se recarga nginx"
annotate "Products migrado"
strangler_route products products-service:8000
docker compose exec -T api-gateway grep '^set' /etc/nginx/strangler-routes.conf | sed 's/^/  /'
routes
note "El cliente hace las mismas solicitudes y recibe las mismas respuestas."

step "El costo: mientras conviven, los datos están en dos lugares"
curl -s -o /dev/null -X PUT "$FACADE/products/$PRODUCT" -H 'Content-Type: application/json' \
    -d '{"name": "Camisa nueva", "price": 1200.50}'
echo "  Se cambia el nombre del Product por la fachada; lo recibe el microservicio."
served products "/products/$PRODUCT"
served orders /orders/user/$ALICE/details
note "El JOIN del monolito sigue viendo el nombre viejo, en su propia base."
note "Una migración real tiene que sincronizar los datos mientras dure la convivencia."
curl -s -o /dev/null -X PUT "$FACADE/products/$PRODUCT" -H 'Content-Type: application/json' \
    -d '{"name": "Camisa", "price": 1200.50}'

step "Se migran Users y Orders, de a una ruta"
annotate "Users migrado"
strangler_route users users-service:8000
routes
annotate "Orders migrado"
strangler_route orders orders-service:8000
routes
note "Si algo falla después de migrar una ruta, volver atrás es cambiar la misma línea."

step "Cuántas solicitudes atendió cada uno, según la fachada"
docker compose logs --no-log-prefix --since "$since" api-gateway | grep '\[STRANGLER\]' \
    | awk '{ print $NF }' | sort | uniq -c | awk '{ printf "  %3s  %s\n", $1, $2 }'

step "Ya nada llega al monolito: se apaga"
annotate "Monolito apagado"
docker compose stop monolith > /dev/null 2>&1
routes
note "La higuera reemplazó al árbol: el monolito se retiró sin cortar el servicio."
