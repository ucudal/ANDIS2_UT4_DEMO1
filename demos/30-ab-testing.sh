#!/usr/bin/env bash
# A/B Testing: la versión v2 de Users trae un experimento. Cada User ve su
# nombre en una de dos variantes, y se cuenta cuántas veces se mostró cada una,
# para comparar su efecto. Qué variante ve cada User lo decide Unleash, en un
# flag que se cambia sin desplegar; Users se lo pregunta con OpenFeature.
#
# Bajo carga y en Grafana: ./demos/load-deployment.sh ab
source "$(dirname "$0")/common.sh"

cleanup() {
    echo
    note "Volviendo la entrada de despliegue a la versión v1 y el reparto a la mitad..."
    deploy_target users-service:8000
    experiment_start
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

note "Levantando Unleash, con el flag $EXPERIMENT_FLAG: la mitad de los Users en cada variante..."
experiment_start

step "Con la versión v1 no hay experimento"
get_user "$ALICE" -i | user_summary

step "Se despliega la versión v2, con Blue/Green"
deploy_target users-service-v2:8000
annotate "A/B Testing: la versión v2 en producción" despliegue
note "Variante A: DisplayName es el nombre. Variante B: el nombre y el email."
note "Qué variante ve cada User lo decide Unleash: $UNLEASH, usuario admin, contraseña unleash4all."
before=$(experiment_counts)

step "Por porcentaje: la mitad de los Users en cada variante, según su id"
for id in "$ALICE" "$BOB"; do get_user "$id" -i | user_summary; done

step "Fija: el mismo User recibe siempre la misma variante"
for _ in 1 2 3; do get_user "$BOB" -i | user_summary; done
note "Si cambiara en cada consulta, no se podría medir el efecto de cada variante."

step "Por segmento: con X-Segment: beta, bob recibe la variante B"
get_user "$BOB" -i -H 'X-Segment: beta' | user_summary
note "Por ejemplo, para mostrarle lo nuevo solo a quien se anotó para probarlo."

step "Se cambia el reparto en Unleash, sin desplegar: todos los Users a la variante B"
experiment_split 100
sleep 3
for id in "$ALICE" "$BOB"; do get_user "$id" -i | user_summary; done
note "Users vuelve a leer el flag cada 2\": no se reinició ni se desplegó nada."

step "Si Unleash se cae, Users sigue con la última configuración que leyó"
docker compose stop unleash > /dev/null 2>&1
for id in "$ALICE" "$BOB"; do get_user "$id" -i | user_summary; done
note "Unleash no está en el camino de cada solicitud: si no responde, no la detiene."

step "Unleash vuelve, y el reparto vuelve a la mitad"
experiment_start
sleep 3
for id in "$ALICE" "$BOB"; do get_user "$id" -i | user_summary; done

step "Cuántas veces se mostró cada variante, según las métricas de Users"
note "Antes de esta demo:"
echo "${before:-  ninguna}"
note "Ahora:"
experiment_counts
note "Con estos números, y con lo que hizo cada User después, se elige una"
note "variante. El reparto está en Unleash: ni el gateway ni el código de Users lo conocen."
