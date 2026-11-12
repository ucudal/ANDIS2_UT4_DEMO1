#!/usr/bin/env bash
# Backends for Frontends: la web y la app móvil muestran las Orders de un User,
# pero cada una necesita algo distinto. Cada cliente tiene su propio backend, que
# arma exactamente lo que ese cliente muestra, y que su equipo cambia y despliega
# sin coordinar con el otro.
#
# Bajo carga y en Grafana: ./demos/load-modifiability.sh bff
source "$(dirname "$0")/common.sh"

PROBES=$(mktemp)
RUNNING=$(mktemp)
cleanup() {
    rm -f "$PROBES" "$RUNNING"
}
trap cleanup EXIT

# probe <web|mobile>: consulta su BFF cada 0,2 segundos y anota el código HTTP,
# mientras exista el archivo RUNNING.
probe() {
    while [ -f "$RUNNING" ]; do
        echo "$1 $(curl -s -m 2 -o /dev/null -w '%{http_code}' "$GATEWAY/$1/orders/user/$ALICE")" >> "$PROBES"
        sleep 0.2
    done
}

started() { docker inspect -f '{{.State.StartedAt}}' "$(docker compose ps -q "$1")"; }

title "Backends for Frontends"

step "La web: cada Order con el precio de su Product, y el total"
curl -s "$GATEWAY/web/orders/user/$ALICE" | table '.orders'
echo "  total: $(curl -s "$GATEWAY/web/orders/user/$ALICE" | jq .total)"

step "La app móvil: solo el Product y el estado de cada Order"
curl -s "$GATEWAY/mobile/orders/user/$ALICE" | table '.orders'

step "La misma consulta, dos respuestas a la medida"
for client in web mobile; do
    printf '  %-6s %s bytes\n' "$client" "$(curl -s -o /dev/null -w '%{size_download}' "$GATEWAY/$client/orders/user/$ALICE")"
done
note "Ninguno de los dos clientes tiene que pedir de más ni armar la respuesta."

step "El equipo móvil publica un cambio: se redespliega bff-mobile"
web_started=$(started bff-web)
probe web &
probe mobile &
sleep 2
docker compose up -d --no-deps --force-recreate --wait bff-mobile > /dev/null 2>&1
sleep 2
rm -f "$RUNNING"
wait
sort "$PROBES" | uniq -c | awk '{ printf "  %-6s HTTP %s: %s respuestas\n", $2, $3, $1 }'
note "Mientras se reemplazaba su contenedor, la app pudo tener demoras o errores. La"
note "web siguió igual: su backend no se tocó."

step "La web no se enteró"
[ "$web_started" = "$(started bff-web)" ] && echo "  bff-web sigue con la misma hora de arranque: no se reinició."
note "Cada cliente cambia su backend a su ritmo, sin tocar el del otro ni los servicios."
