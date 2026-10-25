#!/usr/bin/env bash
# Deletes everything deploy.sh created. The AIServices account is soft-deleted, so it is purged too,
# otherwise its name stays taken for 48 hours.
set -euo pipefail

SUBSCRIPTION="${SUBSCRIPTION:?set SUBSCRIPTION to the subscription id the repro was deployed into}"
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-foundry-root-repro}"
LOCATION="${LOCATION:-uksouth}"

accounts=$(az cognitiveservices account list --subscription "$SUBSCRIPTION" --resource-group "$RESOURCE_GROUP" --query "[].name" -o tsv)
az group delete --subscription "$SUBSCRIPTION" --name "$RESOURCE_GROUP" --yes
for account in $accounts; do
  az cognitiveservices account purge --subscription "$SUBSCRIPTION" --resource-group "$RESOURCE_GROUP" --location "$LOCATION" --name "$account"
done
rm -f "$(cd "$(dirname "$0")" && pwd)/.repro.env"
