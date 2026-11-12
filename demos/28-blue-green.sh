#!/usr/bin/env bash
# Blue/Green Deployment: la versión nueva de Users se despliega al lado de la
# actual, se prueba sin que reciba tráfico y se conmuta de una vez. Si algo sale
# mal, volver atrás es conmutar de nuevo: la versión anterior sigue viva.
#
# Bajo carga y en Grafana: ./demos/load-deployment.sh bluegreen
source "$(dirname "$0")/common.sh"

PROBES=$(mktemp)
RUNNING=$(mktemp)
cleanup() {
    rm -f "$RUNNING"
    wait 2> /dev/null || true
    echo
    note "Volviendo la entrada de despliegue a la versión v1..."
    deploy_target users-service:8000
    rm -f "$PROBES"
    reset_chaos
}
trap cleanup EXIT

# switch <destino> <texto>: conmuta mientras una sonda consulta a Users, y
# muestra qué respondió cada versión durante el cambio.
switch() {
    : > "$PROBES"
    touch "$RUNNING"
    deploy_probe "$PROBES" "$RUNNING" &
    sleep 2
    annotate "$2" despliegue
    deploy_target "$1"
    docker compose exec -T api-gateway grep default /etc/nginx/deployment-routes.conf | sed 's/^ */  destino: /'
    sleep 2
    rm -f "$RUNNING"
    wait
    tally "$PROBES"
}

title "Blue/Green Deployment"

step "Producción: la versión v1, en users-service, atiende la entrada de despliegue"
get_user "$ALICE" -i | user_summary
versions 10

step "La versión v2 ya está desplegada al lado, en users-service-v2, sin tráfico"
docker compose ps --format '{{.Service}}: {{.Status}}' users-service users-service-v2 | sed 's/^/  /'
note "Se la prueba directamente, desde adentro de la red, antes de exponerla:"
get_user_inside users-service-v2 "$ALICE" -i | user_summary
note "La versión v2 trae un campo nuevo, DisplayName. Los clientes todavía no lo ven."

step "Se conmuta: todo el tráfico pasa a la versión v2"
switch users-service-v2:8000 "Blue/Green: se conmuta a la versión v2"
note "Las solicitudes pasan de una versión a la otra sin errores: nginx recarga su"
note "configuración sin cortar las conexiones."

step "Ahora responde la versión v2, en la misma dirección"
get_user "$ALICE" -i | user_summary
versions 10

step "Vuelta atrás: la versión v1 sigue viva, así que volver es conmutar de nuevo"
switch users-service:8000 "Blue/Green: vuelta atrás a la versión v1"
get_user "$ALICE" -i | user_summary
note "No hubo que desplegar nada para volver atrás. El costo: dos versiones"
note "completas al mismo tiempo, y una base que las dos tienen que entender."
