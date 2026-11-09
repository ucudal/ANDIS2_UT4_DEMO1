#!/usr/bin/env bash
# A/B Testing: la versión v2 de Users trae un experimento. Cada User ve su
# nombre en una de dos variantes, elegida por segmento o al azar, y se cuenta
# cuántas veces se mostró cada una, para comparar su efecto.
#
# Bajo carga y en Grafana: ./demos/load-deployment.sh ab
source "$(dirname "$0")/common.sh"

cleanup() {
    echo
    note "Volviendo la entrada de despliegue a la versión v1..."
    deploy_target users-service:8000
    reset_chaos
}
trap cleanup EXIT

# experiment_counts: cuántas veces mostró cada variante la versión v2, según sus
# propias métricas.
experiment_counts() {
    docker compose exec -T api-gateway curl -s http://users-service-v2:8000/metrics \
        | grep '^users_experiment_total{' \
        | sed -E 's/.*segment="([^"]*)".*variant="([^"]*)".*\} ([0-9]+).*/  variante \2, segmento \1: \3/' \
        | sort
}

title "A/B Testing"

step "Con la versión v1 no hay experimento"
get_user 1 -i | user_summary

step "Se despliega la versión v2, con Blue/Green"
deploy_target users-service-v2:8000
annotate "A/B Testing: la versión v2 en producción" despliegue
note "Variante A: DisplayName es el nombre. Variante B: el nombre y el email."
before=$(experiment_counts)

step "Al azar: la variante la decide el id del User"
for id in 1 2; do get_user "$id" -i | user_summary; done

step "Fija: el mismo User recibe siempre la misma variante"
for _ in 1 2 3; do get_user 2 -i | user_summary; done
note "Si cambiara en cada consulta, no se podría medir el efecto de cada variante."

step "Por segmento: con X-Segment: beta, el User 2 recibe la variante B"
get_user 2 -i -H 'X-Segment: beta' | user_summary
note "Por ejemplo, para mostrarle lo nuevo solo a quien se anotó para probarlo."

step "Cuántas veces se mostró cada variante, según las métricas de Users"
note "Antes de esta demo:"
echo "${before:-  ninguna}"
note "Ahora:"
experiment_counts
note "Con estos números, y con lo que hizo cada User después, se elige una"
note "variante. El experimento está en el código: el gateway no sabe nada de él."
