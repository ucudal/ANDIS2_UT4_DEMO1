#!/usr/bin/env bash
# Retry: Orders reintenta con retroceso exponencial las llamadas que fallan
# por causas transitorias, y el usuario no se entera de la falla. Se hace la
# misma consulta con el Retry apagado y prendido, para comparar.
source "$(dirname "$0")/common.sh"

title "Retry"

step "Sin fallas: curl $GATEWAY/orders/user/1/details"
details

step "Products falla en el 30 % de las solicitudes -responde 503-"
chaos products 0.3 0

step "Se apagan todos los patrones, también el Retry"
only_pattern ninguno

step "Se repite la misma consulta 8 veces, una por segundo"
for i in $(seq 8); do details; sleep 1; done

step "Se deja prendido solo el Retry: el Circuit Breaker no interviene"
only_pattern retry

step "Se repite la misma consulta 8 veces, una por segundo"
for i in $(seq 8); do details; sleep 1; done
