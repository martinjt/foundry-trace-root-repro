#!/usr/bin/env bash
# Deploys the whole repro into one resource group, builds both images in the registry, creates a new
# version of the hosted agent and waits for it to become active. Safe to re-run.
set -euo pipefail

SUBSCRIPTION="${SUBSCRIPTION:?set SUBSCRIPTION to the subscription id to deploy into}"
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-foundry-root-repro}"
LOCATION="${LOCATION:-uksouth}"
AGENT_NAME="trace-repro"
API_VERSION="v1"
# Workaround switch (default off): see README "Workaround: x-client-* headers".
CLIENT_HEADER_PROPAGATION="${CLIENT_HEADER_PROPAGATION:-false}"

here="$(cd "$(dirname "$0")" && pwd)"
secrets_file="$here/.repro.env"

register_providers() {
  for namespace in Microsoft.CognitiveServices Microsoft.App Microsoft.ContainerRegistry Microsoft.ManagedIdentity; do
    state=$(az provider show --subscription "$SUBSCRIPTION" --namespace "$namespace" --query registrationState -o tsv)
    if [ "$state" != "Registered" ]; then
      echo "Registering $namespace"
      az provider register --subscription "$SUBSCRIPTION" --namespace "$namespace" --wait
    fi
  done
}

# Generated once and kept beside the scripts (git-ignored), so a re-run keeps the same keys.
load_or_create_secrets() {
  if [ ! -f "$secrets_file" ]; then
    umask 077
    {
      echo "OTLP_API_KEY=$(openssl rand -hex 24)"
      echo "BROWSER_TOKEN=$(openssl rand -hex 24)"
      echo "DASHBOARD_API_KEY=$(openssl rand -hex 24)"
    } > "$secrets_file"
  fi
  # shellcheck disable=SC1090
  source "$secrets_file"
}

deploy_infrastructure() {
  local caller_image="$1"
  az deployment group create \
    --subscription "$SUBSCRIPTION" \
    --resource-group "$RESOURCE_GROUP" \
    --name trace-repro \
    --template-file "$here/infra/main.bicep" \
    --parameters location="$LOCATION" agentName="$AGENT_NAME" callerImage="$caller_image" \
      deployerPrincipalId="$(az ad signed-in-user show --query id -o tsv)" \
      otlpApiKey="$OTLP_API_KEY" browserToken="$BROWSER_TOKEN" dashboardApiKey="$DASHBOARD_API_KEY" \
      clientHeaderPropagation="$CLIENT_HEADER_PROPAGATION" \
    --query properties.outputs -o json
}

output() { jq -r --arg name "$1" '.[$name].value' <<<"$outputs"; }

build_image() {
  local repository="$1" context="$2"
  az acr build --subscription "$SUBSCRIPTION" --registry "$(output registryName)" \
    --image "$repository:$tag" --platform linux/amd64 "$context" -o none >&2
  echo "$(output registryLoginServer)/$repository:$tag"
}

# Agent versions are Foundry data-plane objects, so they go through the REST API rather than ARM.
create_agent_version() {
  local image="$1" agents body created
  agents="$(output projectEndpoint)/agents"
  body=$(jq --arg image "$image" --arg clientHeaderPropagation "$CLIENT_HEADER_PROPAGATION" \
    '.definition.container_configuration.image = $image
     | .definition.environment_variables.CLIENT_HEADER_PROPAGATION = $clientHeaderPropagation' \
    "$here/agent/agent-version.json")
  if foundry GET "$agents/$AGENT_NAME?api-version=$API_VERSION" >/dev/null 2>&1; then
    created=$(foundry POST "$agents/$AGENT_NAME/versions?api-version=$API_VERSION" "$body")
  else
    created=$(foundry POST "$agents?api-version=$API_VERSION" "$(jq --arg name "$AGENT_NAME" '. + {name: $name}' <<<"$body")")
  fi
  jq -r '.version // .versions.latest.version' <<<"$created"
}

# The deployer's Foundry User grant can take a few minutes to reach the data plane after the first deploy.
foundry() {
  local method="$1" url="$2" body="${3:-}" attempt
  for attempt in $(seq 1 20); do
    if [ -n "$body" ]; then
      az rest --method "$method" --resource https://ai.azure.com --url "$url" --body "$body" 2>/tmp/foundry-rest.err && return 0
    else
      az rest --method "$method" --resource https://ai.azure.com --url "$url" 2>/tmp/foundry-rest.err && return 0
    fi
    grep -q -E "PermissionDenied|AuthorizationFailed|\(403\)|Forbidden" /tmp/foundry-rest.err || { cat /tmp/foundry-rest.err >&2; return 1; }
    echo "Waiting for the Foundry role assignment to apply (attempt $attempt)" >&2
    sleep 30
  done
  cat /tmp/foundry-rest.err >&2
  return 1
}

wait_until_active() {
  local version="$1" url status
  url="$(output projectEndpoint)/agents/$AGENT_NAME/versions/$version?api-version=$API_VERSION"
  for _ in $(seq 1 90); do
    status=$(foundry GET "$url" | jq -r .status)
    echo "Agent version $version: $status"
    case "$status" in
      active) return 0 ;;
      failed) foundry GET "$url"; return 1 ;;
    esac
    sleep 10
  done
  echo "Version $version was not active after 15 minutes" >&2
  return 1
}

register_providers
load_or_create_secrets
az group create --subscription "$SUBSCRIPTION" --name "$RESOURCE_GROUP" --location "$LOCATION" -o none

echo "Deploying infrastructure"
outputs=$(deploy_infrastructure "")

tag="$(date -u +%Y%m%d%H%M%S)"
echo "Building images ($tag)"
agent_image=$(build_image trace-repro-agent "$here/agent")
caller_image=$(build_image trace-repro-caller "$here/caller")

echo "Deploying the caller"
outputs=$(deploy_infrastructure "$caller_image")

echo "Creating an agent version from $agent_image"
version=$(create_agent_version "$agent_image")
wait_until_active "$version"

dashboard_url="$(output dashboardUrl)"
cat <<DONE

Dashboard:  $dashboard_url
Sign in:    $dashboard_url/login?t=\$BROWSER_TOKEN   (BROWSER_TOKEN is in $secrets_file)
Caller:     $(output callerUrl)/ask
Agent:      $(output projectEndpoint)/agents/$AGENT_NAME/endpoint/protocols/openai/responses?api-version=$API_VERSION
DONE
