#!/usr/bin/env bash
# Rolling Upgrade en Kubernetes: la demo 29 en Kubernetes, en dos partes.
#
# 1. Con un Deployment: Kubernetes reemplaza los pods de a uno, esperando a que
#    cada pod nuevo esté listo. Es lo que la demo 29 hacía a mano. Pero solo
#    sabe si un pod está listo: una versión que responde /health y falla en las
#    solicitudes llega a todos los pods, y volver atrás es a mano.
# 2. Con Argo Rollouts: el mismo reemplazo, pero después de cada paso Argo mira
#    en Prometheus los errores de los pods nuevos. Si fallan, vuelve atrás solo.
#
# La versión con el error es users-service:v3-bad: falla en el 30% de las
# solicitudes desde que arranca -ver src/UsersService/Dockerfile-.
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
    note "Borrando el Deployment, el Rollout y el Service..."
    kubectl -n andis delete deployment users --ignore-not-found --wait=false > /dev/null 2>&1 || true
    strategy_delete rolling-upgrade
}
trap cleanup EXIT

# pods: los pods de Users, con su imagen y si están listos. No muestra los que
# se están deteniendo, que esperan unos segundos antes de terminar.
pods() {
    kubectl -n andis get pods -l app=users -o json | jq -r '
        ["POD", "IMAGEN", "LISTO"],
        (.items | sort_by(.metadata.creationTimestamp)[] | select(.metadata.deletionTimestamp == null)
         | [.metadata.name, .spec.containers[0].image, (.status.containerStatuses[0].ready | tostring)])
        | @tsv' | column -t | sed 's/^/  /'
}

# deployment_update <imagen>: cambia la imagen del Deployment y espera a que
# Kubernetes termine de reemplazar los pods.
deployment_update() {
    kubectl -n andis set image deployment/users users="users-service:$1" > /dev/null
    kubectl -n andis rollout status deployment/users --timeout=180s
}

# deployment_undo: vuelve el Deployment a la versión anterior.
deployment_undo() {
    kubectl -n andis rollout undo deployment/users 2> /dev/null
    kubectl -n andis rollout status deployment/users --timeout=180s
}

title "Rolling Upgrade en Kubernetes"

printf '\n\033[1;34m-- Parte 1: un Deployment de Kubernetes --\033[0m\n'

note "Aplicando k8s/rolling-upgrade: el Deployment users, con 3 pods de la versión v1..."
kubectl apply -f k8s/rolling-upgrade/service.yaml -f k8s/rolling-upgrade/deployment.yaml > /dev/null
kubectl -n andis rollout status deployment/users --timeout=180s > /dev/null
until [ "$(get_user "$ALICE" -o /dev/null -w '%{http_code}')" = 200 ]; do sleep 1; done

step "Producción: 3 pods de la versión v1"
pods
versions 10

step "Se despliega la versión v2: kubectl set image"
while_probing deployment_update v2
pods
note "Kubernetes levantó un pod nuevo, esperó a que estuviera listo y recién"
note "entonces detuvo uno viejo, tres veces. Sin errores: es la demo 29, sin scripts."

step "Se despliega una versión v3 con un error"
while_probing deployment_update v3-bad
pods
versions 30
note "Kubernetes la desplegó en los 3 pods: responden /health, así que para él"
note "están listos. Las fallas siguen hasta que alguien se da cuenta."

step "Vuelta atrás, a mano: kubectl rollout undo"
while_probing deployment_undo
versions 10

printf '\n\033[1;34m-- Parte 2: Argo Rollouts, con análisis --\033[0m\n'

note "Cambiando el Deployment por el Rollout users, con 3 pods de la versión v2..."
kubectl -n andis delete deployment users --wait=false > /dev/null
kubectl apply -f k8s/rolling-upgrade/rollout.yaml > /dev/null
rollouts status users --timeout 180s > /dev/null
sleep 5

step "Producción: 3 pods de la versión v2"
rollout_tree
versions 10

step "Se despliega la versión v3 con el error: kubectl argo rollouts set image"
note "Una sonda pide el User de alice cada 0,1\": el análisis necesita tráfico."
: > "$PROBES"
touch "$RUNNING"
deploy_probe "$PROBES" "$RUNNING" &
sleep 2
rollouts set image users users=users-service:v3-bad > /dev/null
until kubectl -n andis get analysisrun -o name 2> /dev/null | grep -q .; do sleep 1; done
rollout_tree
note "Paso 1: 1 de 3 pods con la versión v3, y el análisis midiendo sus errores."

step "El análisis falla, y Argo vuelve atrás solo"
wait_phase Degraded
sleep 6
rm -f "$RUNNING"
wait
rollout_tree
printf '  errores medidos en los pods de la versión v3: %s\n' \
    "$(kubectl -n andis get analysisrun -o jsonpath='{.items[-1].status.metricResults[0].measurements[*].value}')"
tally "$PROBES"
note "La versión v3 llegó a un solo pod, durante unos segundos, y nadie tuvo que"
note "intervenir. El Deployment, en cambio, la desplegó en todos."
