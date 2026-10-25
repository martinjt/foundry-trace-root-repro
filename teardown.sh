#!/usr/bin/env bash
# Deletes everything deploy.sh created. The AIServices account is soft-deleted, so it is purged too,
# otherwise its name stays taken for 48 hours. Safe to re-run: a purge that failed last time is retried.
set -euo pipefail

SUBSCRIPTION="${SUBSCRIPTION:?set SUBSCRIPTION to the subscription id the repro was deployed into}"
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-foundry-root-repro}"

if [ "$(az group exists --subscription "$SUBSCRIPTION" --name "$RESOURCE_GROUP")" = "true" ]; then
  az group delete --subscription "$SUBSCRIPTION" --name "$RESOURCE_GROUP" --yes
fi

az cognitiveservices account list-deleted --subscription "$SUBSCRIPTION" \
  --query "[?contains(id, '/resourceGroups/$RESOURCE_GROUP/')].[name, location]" -o tsv |
  while read -r account location; do
    az cognitiveservices account purge --subscription "$SUBSCRIPTION" \
      --resource-group "$RESOURCE_GROUP" --location "$location" --name "$account"
  done

rm -f "$(cd "$(dirname "$0")" && pwd)/.repro.env"
