#!/usr/bin/env bash
# Canary con Argo Rollouts: la demo 29 en Kubernetes. Una fracción de las
# solicitudes va a la versión nueva, y la fracción crece si las métricas son
# buenas. Lo que en la demo 29 se hacía a mano -cambiar el porcentaje, mirar los
# errores, decidir- lo hace Argo Rollouts: cambia los pesos de la HTTPRoute
# users, y en cada paso consulta a Prometheus los errores de la versión nueva.
#
# Primero la versión v2, que avanza sola hasta el 100%. Después la v3, con un
# error: el análisis falla en el 10%, y Argo vuelve a la v2 sin que nadie
# intervenga.
#
# Necesita el clúster: ./demos/k8s-up.sh
source "$(dirname "$0")/common.sh"
source k8s/common.sh

PROBES=$(mktemp)
RUNNING=$(mktemp)
cleanup() {
    rm -f "$RUNNING"
    wait 2> /dev/null || true
    rm -f "$PROBES"
    echo
    note "Borrando el Rollout, sus Service y la HTTPRoute..."
    strategy_delete canary
}
trap cleanup EXIT

# weights: los pesos de la HTTPRoute users, es decir, qué porcentaje de las
# solicitudes va a cada Service.
weights() {
    kubectl -n andis get httproute users \
        -o jsonpath='{range .spec.rules[0].backendRefs[*]}{.name} {.weight} %   {end}'
}

# follow <imagen>: despliega una imagen mientras una sonda pide el User de alice, y
# muestra cada cambio de los pesos hasta que el Rollout termina: Healthy si
# llegó al 100%, Degraded si abortó.
follow() {
    local start phase last="" now
    : > "$PROBES"
    touch "$RUNNING"
    deploy_probe "$PROBES" "$RUNNING" &
    sleep 2
    start=$(date +%s)
    rollouts set image users users="users-service:$1" > /dev/null
    sleep 2
    while :; do
        phase=$(kubectl -n andis get rollout users -o jsonpath='{.status.phase}')
        now="$(weights) $phase"
        [ "$now" != "$last" ] && printf '  %3s s  %s\n' "$(($(date +%s) - start))" "$now"
        last=$now
        case $phase in Healthy | Degraded | Paused) break ;; esac
        sleep 1
    done
    sleep 3
    rm -f "$RUNNING"
    wait
    tally "$PROBES"
}

# measured: lo que midió el último análisis: la fracción de errores de los pods
# de la versión nueva, en cada medida.
measured() {
    printf '  errores medidos en los pods de la versión nueva: %s\n' \
        "$(kubectl -n andis get analysisrun --sort-by=.metadata.creationTimestamp \
            -o jsonpath='{.items[-1].status.metricResults[0].measurements[*].value}')"
}

title "Canary con Argo Rollouts"

note "Aplicando k8s/canary: el Rollout users, con la versión v1..."
strategy_apply canary

step "Producción: todo a la versión v1"
rollout_tree
printf '  %s\n' "$(weights)"
versions 10

step "Se despliega la versión v2: 10%, análisis, 50%, análisis, 100%"
note "Una sonda pide el User de alice cada 0,1\". Cada análisis espera 30\""
note "y mide 3 veces, cada 10\": el despliegue tarda un minuto y medio."
follow v2
measured
note "Nadie tocó nada: en cada paso, el análisis midió menos del 5% de errores en"
note "los pods de la versión v2, y Argo subió el peso."

step "Se despliega una versión v3 con un error"
follow v3-bad
rollout_tree
measured
note "El análisis midió más del 5% de errores con el 10% del tráfico. Argo abortó"
note "y volvió todo a la versión v2: solo vieron la falla las solicitudes que fueron"
note "a la v3, durante el análisis."

step "Producción: todo a la versión v2"
printf '  %s\n' "$(weights)"
versions 10
