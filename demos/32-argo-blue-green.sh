#!/usr/bin/env bash
# Blue/Green con Argo Rollouts: la demo 28 en Kubernetes. La versión nueva de
# Users se despliega al lado de la actual, se prueba sin que reciba tráfico y
# se conmuta de una vez. Lo que en la demo 28 se hacía a mano -levantar la
# versión nueva, probarla, cambiar el destino del gateway- lo hace Argo
# Rollouts, con un comando por paso.
#
# Necesita el clúster: ./demos/k8s-up.sh
source "$(dirname "$0")/common.sh"
source k8s/common.sh

cleanup() {
    echo
    note "Borrando el Rollout y sus Service..."
    strategy_delete blue-green
}
trap cleanup EXIT

title "Blue/Green con Argo Rollouts"

note "Aplicando k8s/blue-green: el Rollout users, con la versión v1..."
strategy_apply blue-green

step "Producción: la versión v1 atiende el Gateway, en el puerto 8092"
rollout_tree
get_user "$ALICE" -i | user_summary
versions 10

step "Se despliega la versión v2: kubectl argo rollouts set image"
rollouts set image users users=users-service:v2 > /dev/null
wait_phase Paused
rollout_tree
note "Argo levantó los pods de la versión v2 al lado de los de la v1, y se detuvo."
note "La v1 es stable y active: la que ven los clientes. La v2 es preview."

step "Los clientes siguen en la versión v1"
versions 10

step "La versión v2 se prueba por el Service users-preview, en $PREVIEW"
DEPLOY=$PREVIEW get_user "$ALICE" -i | user_summary
note "La versión v2 trae un campo nuevo, DisplayName. Los clientes todavía no lo ven."

step "Se conmuta: kubectl argo rollouts promote"
while_probing rollouts promote users
rollout_tree
note "Argo apuntó users-active a los pods de la versión v2, sin errores. Los de la"
note "v1 siguen vivos un minuto -delay-, por si hay que volver atrás."

step "Ahora responde la versión v2, en la misma dirección"
get_user "$ALICE" -i | user_summary
versions 10

step "Vuelta atrás: kubectl argo rollouts undo"
while_probing rollouts undo users
rollout_tree
get_user "$ALICE" -i | user_summary
note "Como los pods de la versión v1 seguían vivos, volver fue conmutar de nuevo,"
note "sin esperar a que arranquen. Es la demo 28, pero con un comando por paso."
