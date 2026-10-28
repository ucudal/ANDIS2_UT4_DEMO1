#!/usr/bin/env bash
# Retry: Orders reintenta con retroceso exponencial las llamadas que fallan
# por causas transitorias, y el usuario no se entera de la falla.
source "$(dirname "$0")/common.sh"

title "Retry"

step "Sin fallas: curl $GATEWAY/orders/user/1/details"
details

step "Products falla en el 20 % de las solicitudes -responde 503-"
chaos products 0.2 0

step "Se repite la misma consulta 8 veces"
for i in $(seq 8); do details; done
