# Funciones comunes de las demos de despliegue con Kubernetes, de la 32 a la 34.
# Se carga después de demos/common.sh, del que se usan get_user, user_summary,
# versions, tally y deploy_probe. Acá apuntan al Gateway del clúster, en el
# puerto 8092, en lugar de la entrada de despliegue de Docker Compose. No se
# ejecuta directamente.

DEPLOY=http://localhost:8092
PREVIEW=http://preview.localhost:8092

# demos/common.sh, al terminar, quita las fallas simuladas de Docker Compose:
# acá no corresponde.
trap - EXIT

kubectl config use-context kind-andis > /dev/null 2>&1 \
    || { echo "No está el clúster: hay que crearlo con ./demos/k8s-up.sh"; exit 1; }

# rollouts <argumentos>: el plugin de Argo Rollouts, en el namespace andis.
rollouts() { kubectl argo rollouts -n andis "$@"; }

# strategy_apply <carpeta>: aplica los manifiestos de una estrategia, de
# k8s/<carpeta>, y espera a que la versión v1 esté sana y responda por el
# Gateway. Las tres estrategias usan el mismo Rollout, users: hay una sola a la
# vez.
strategy_apply() {
    kubectl apply -f "k8s/$1" > /dev/null
    rollouts status users --timeout 180s > /dev/null
    until [ "$(get_user "$ALICE" -o /dev/null -w '%{http_code}')" = 200 ]; do sleep 1; done
}

# strategy_delete <carpeta>: borra los manifiestos de una estrategia, sin
# esperar a que terminen los pods.
strategy_delete() {
    kubectl delete -f "k8s/$1" --ignore-not-found --wait=false > /dev/null 2>&1 || true
}

# rollout_tree: el estado del Rollout como lo muestra Argo Rollouts: cada
# versión, con su ReplicaSet y sus pods, y para qué está, por ejemplo stable,
# active o preview.
rollout_tree() {
    rollouts get rollout users --no-color | sed -n '/^NAME/,$p' | sed 's/^/  /'
}

# wait_phase <fase>: espera a que el Rollout llegue a una fase, por ejemplo
# Paused o Healthy.
wait_phase() {
    until [ "$(kubectl -n andis get rollout users -o jsonpath='{.status.phase}')" = "$1" ]; do sleep 1; done
}

# while_probing <comando>: ejecuta un comando mientras una sonda pide el User de alice
# cada 0,1 segundos, y muestra qué respondió cada versión durante el cambio.
while_probing() {
    local probes running
    probes=$(mktemp)
    running=$(mktemp)
    deploy_probe "$probes" "$running" &
    sleep 2
    "$@" > /dev/null
    sleep 4
    rm -f "$running"
    wait
    tally "$probes"
    rm -f "$probes"
}
