#!/usr/bin/env bash
# Crea el clúster de las demos de despliegue con Kubernetes, de la 32 a la 34, y
# deja instalado lo que comparten:
# - Gateway API y NGINX Gateway Fabric, la entrada de los clientes en el 8092.
# - Argo Rollouts, que despliega cada versión con una estrategia, y su plugin de
#   Gateway API, con el que cambia los pesos de una HTTPRoute.
# - De k8s/base: la base de Users, su configuración, el Gateway, Prometheus y el
#   análisis de errores.
# - Las tres versiones de Users, como imágenes: v1, v2 y v3-bad.
#
# Necesita Docker, kind, kubectl y el plugin kubectl-argo-rollouts. Si el
# clúster ya existe, solo actualiza lo que haya cambiado. Se borra con
# ./demos/k8s-down.sh.
set -euo pipefail
cd "$(dirname "$0")/.."

CLUSTER=andis
GATEWAY_API=v1.6.3
NGINX_GATEWAY_FABRIC=v2.8.0
ARGO_ROLLOUTS=v1.10.2
ARGO_GATEWAY_API_PLUGIN=v0.17.0

step() { printf '\n\033[1m> %s\033[0m\n' "$*"; }

for tool in docker kind kubectl kubectl-argo-rollouts; do
    command -v "$tool" > /dev/null || { echo "Falta $tool"; exit 1; }
done

step "El clúster, con kind"
if kind get clusters | grep -qx "$CLUSTER"; then
    echo "  ya existe"
else
    kind create cluster --config k8s/kind.yaml
fi
kubectl config use-context "kind-$CLUSTER" > /dev/null

# Una imagen por versión de Users, como en producción. El nodo no las baja de un
# registro: kind las copia desde Docker -imagePullPolicy: Never en los Rollout-.
# mongo:9 no se copia, porque kind no puede copiar imágenes de varias
# plataformas: el nodo la baja de Docker Hub.
step "Las imágenes de Users: v1, v2 y v3-bad, la versión con un error"
docker build -q -f src/UsersService/Dockerfile -t users-service:v1 src > /dev/null
docker build -q -f src/UsersService/Dockerfile -t users-service:v2 --build-arg RELEASE_VERSION=2 src > /dev/null
docker build -q -f src/UsersService/Dockerfile -t users-service:v3-bad \
    --build-arg RELEASE_VERSION=3 --build-arg CHAOS_FAILURE_RATE=0.3 src > /dev/null
kind load docker-image --name "$CLUSTER" users-service:v1 users-service:v2 users-service:v3-bad

step "Gateway API $GATEWAY_API y NGINX Gateway Fabric $NGINX_GATEWAY_FABRIC"
kubectl apply --server-side -f \
    "https://github.com/kubernetes-sigs/gateway-api/releases/download/$GATEWAY_API/standard-install.yaml" > /dev/null
kubectl apply --server-side -f \
    "https://raw.githubusercontent.com/nginx/nginx-gateway-fabric/$NGINX_GATEWAY_FABRIC/deploy/crds.yaml" > /dev/null
kubectl apply -f \
    "https://raw.githubusercontent.com/nginx/nginx-gateway-fabric/$NGINX_GATEWAY_FABRIC/deploy/nodeport/deploy.yaml" > /dev/null
kubectl -n nginx-gateway wait --for=condition=Available deployment/nginx-gateway --timeout=180s

step "Argo Rollouts $ARGO_ROLLOUTS"
kubectl create namespace argo-rollouts --dry-run=client -o yaml | kubectl apply -f - > /dev/null
# --server-side: las CRD de Argo Rollouts son más grandes que lo que kubectl
# apply guarda en la anotación de la última configuración aplicada.
kubectl apply --server-side -n argo-rollouts -f \
    "https://github.com/argoproj/argo-rollouts/releases/download/$ARGO_ROLLOUTS/install.yaml" > /dev/null

# El plugin de Gateway API se registra en argo-rollouts-config, con la dirección
# del binario para la arquitectura del nodo. Argo Rollouts lo baja al arrancar:
# si el registro cambió, hay que reiniciarlo.
arch=$(kubectl get nodes -o jsonpath='{.items[0].status.nodeInfo.architecture}')
plugin="https://github.com/argoproj-labs/rollouts-plugin-trafficrouter-gatewayapi/releases/download/$ARGO_GATEWAY_API_PLUGIN/gatewayapi-plugin-linux-$arch"
if [[ "$(kubectl -n argo-rollouts get configmap argo-rollouts-config -o jsonpath='{.data.trafficRouterPlugins}' 2> /dev/null)" != *"$plugin"* ]]; then
    kubectl -n argo-rollouts create configmap argo-rollouts-config --dry-run=client -o yaml \
        --from-literal=trafficRouterPlugins="- name: argoproj-labs/gatewayAPI
  location: $plugin" | kubectl apply -f - > /dev/null
    kubectl -n argo-rollouts rollout restart deployment/argo-rollouts > /dev/null
fi
kubectl -n argo-rollouts rollout status deployment/argo-rollouts --timeout=180s

step "La base de Users, su configuración, el Gateway y Prometheus"
kubectl apply -f k8s/base/namespace.yaml > /dev/null
kubectl -n andis create configmap users-db-init --from-file=src/UsersService/mongoinit \
    --dry-run=client -o yaml | kubectl apply -f - > /dev/null
kubectl apply -f k8s/base > /dev/null
kubectl -n andis rollout status statefulset/users-db --timeout=180s
kubectl -n andis rollout status deployment/prometheus --timeout=180s
kubectl -n andis wait --for=condition=Programmed gateway/andis --timeout=180s
# NGINX Gateway Fabric crea el nginx del Gateway, andis-nginx, después de que
# el Gateway está programado.
until kubectl -n andis get deployment/andis-nginx > /dev/null 2>&1; do sleep 1; done
kubectl -n andis rollout status deployment/andis-nginx --timeout=180s

step "Listo"
echo "  Gateway: http://localhost:8092. Las demos: ./demos/32-argo-blue-green.sh"
