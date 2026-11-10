#!/usr/bin/env bash
# Borra el clúster de las demos de Kubernetes, con todo lo que tiene adentro.
set -euo pipefail
kind delete cluster --name andis
