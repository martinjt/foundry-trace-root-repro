# foundry-trace-root-repro

Minimal reproduction: traces that pass through an Azure AI Foundry **hosted agent** have a missing parent span in any OTLP backend, and calls made without a caller trace have no root span at all.

## What it shows

A caller instrumented with OpenTelemetry calls a hosted agent (Responses protocol) through the project endpoint and sends a W3C `traceparent`. Foundry keeps the caller's trace id but replaces the parent span id with one of its own before the request reaches the agent container (in a project with Application Insights connected, that parent shows up there as the platform's `agentsv2` `invoke_agent` span). That platform span is never exported over OTLP: the agent version's `OTEL_EXPORTER_OTLP_*` settings, as described in [Configure telemetry for hosted agents](https://learn.microsoft.com/en-us/azure/foundry/agents/how-to/configure-hosted-agent-telemetry), only cover the runtime inside the container. So the agent's `POST /responses` server span points at a parent that no exporter ever sends.

**Expected:** in an OTLP backend, the agent's `POST /responses` span is a child of the caller's outgoing `HTTP POST` span, or of a platform span that is exported to the same OTLP endpoint, so the trace is one connected tree. A call without a `traceparent` has a root span.

**Actual:** the agent's `POST /responses` span has the caller's trace id but a parent span id that appears nowhere in the exported telemetry. The backend shows two disconnected pieces in one trace (the Aspire Dashboard draws `POST /responses` as a second top-level span beside `GET /ask`). A direct call without a `traceparent` produces a trace whose only top-level span, `POST /responses`, still has a parent id, so the trace has no root.

Nothing in the agent changes how it extracts trace context. It is `AgentHost.CreateBuilder` with `AddFoundryResponses` and the SDK's default telemetry, answering a fixed reply so no model deployment is needed.

## What gets deployed

`infra/main.bicep`, into one resource group (no Application Insights, no Log Analytics, no project monitoring):

- A Foundry (AIServices) account with project management and one project, `trace-repro`.
- An Azure Container Registry the project's identity pulls from, linked to the project with an `acr` connection.
- A project `CustomKeys` connection, `otlp`, whose target is the dashboard's HTTPS URL and whose `headers` key holds `x-otlp-api-key=<key>`. The agent version refers to it as `${{connections.otlp.target}}` and `${{connections.otlp.credentials.headers}}`.
- A Container Apps environment without Log Analytics, and two apps in it:
  - **Aspire Dashboard** (`mcr.microsoft.com/dotnet/aspire-dashboard`), the only telemetry destination. UI, OTLP/HTTP and the telemetry API all share one HTTPS ingress. OTLP needs the `x-otlp-api-key` header, the UI needs a browser token, the API needs an `x-api-key`.
  - **Caller** (`caller/`), an ASP.NET Core app with OpenTelemetry tracing (ASP.NET Core and HttpClient instrumentation, OTLP exporter). `GET /ask` calls the hosted agent's Responses endpoint with its user-assigned managed identity.
- Role assignments: the caller's identity gets Foundry Agent Consumer on the project (to invoke the agent); whoever runs `deploy.sh` gets Foundry User on the project (to create agent versions).

The hosted agent (`agent/`) is created by `deploy.sh` through the Foundry REST API from `agent/agent-version.json`.

## Steps

Prerequisites: Azure CLI signed in to the tenant (`az login`), with rights to create a resource group and role assignments in the subscription; `jq` and `openssl`. No local Docker: images are built with `az acr build`.

```bash
SUBSCRIPTION=<subscription-id> ./deploy.sh
```

It registers the resource providers if needed, deploys the Bicep, builds and pushes both images, deploys the caller, creates the agent (or a new version of it) and waits for the version to become active. It ends by printing the dashboard URL, the caller URL and the agent's Responses endpoint. `RESOURCE_GROUP` (default `rg-foundry-root-repro`) and `LOCATION` (default `uksouth`, which must support hosted agents) can be overridden.

The generated keys live in `.repro.env` next to the scripts (git-ignored). To sign in to the dashboard, open `<dashboard URL>/login?t=<BROWSER_TOKEN from .repro.env>`.

Then:

1. **Through the caller:** `curl <caller URL>/ask`. The response is the agent's fixed reply.
2. **Directly, without a `traceparent`:**

   ```bash
   token=$(az account get-access-token --subscription "$SUBSCRIPTION" --resource https://ai.azure.com --query accessToken -o tsv)
   curl -X POST "<agent Responses endpoint>" -H "Authorization: Bearer $token" \
     -H "Content-Type: application/json" -d '{"input":"hello"}'
   ```

3. Open the dashboard's **Traces** page. The `caller: GET /ask` trace contains the caller's `GET /ask` and `HTTP POST` spans and, as a separate top-level span, the agent's `POST /responses`. Select `POST /responses` and type `Id` in the details filter: its **ParentId** is not the caller's `HTTP POST` span id and is not the id of any span in the dashboard. The direct call's trace has `POST /responses` as its top-level span with a ParentId that likewise matches nothing.

To read the same thing as JSON from the dashboard's telemetry API (the list returns the newest 100 spans; `/api/telemetry/traces/<trace id>` returns one whole trace):

```bash
source .repro.env
curl -s -H "x-api-key: $DASHBOARD_API_KEY" "<dashboard URL>/api/telemetry/traces" |
  jq -r '.data.resourceSpans[] | (.resource.attributes[] | select(.key == "service.name").value.stringValue) as $service
         | .scopeSpans[].spans[] | [.traceId, $service, .name, .spanId, (.parentSpanId // "-")] | @tsv'
```

The dashboard keeps telemetry in memory, so it is lost if its single replica restarts.

To remove everything (the AIServices account is purged too, so its name is free again):

```bash
SUBSCRIPTION=<subscription-id> ./teardown.sh
```

## Versions

Run on 2026-10-25 in uksouth.

| Component | Version |
| --- | --- |
| Azure.AI.AgentServer.Core | 1.0.0-beta.29 |
| Azure.AI.AgentServer.Responses | 1.0.0-beta.8 (via Foundry.Hosting) |
| Microsoft.Agents.AI.Foundry.Hosting | 1.26.0-preview.261023.1 |
| Hosted agent protocol | responses 2.0.0, REST `api-version=v1` |
| OpenTelemetry (caller) | 1.18.0, ASP.NET Core instrumentation 1.15.2, HttpClient instrumentation 1.15.1 |
| .NET | 10.0 |
| Aspire Dashboard | 13.6.2 |
| Azure CLI / Bicep | 2.89.1 / 0.48.1 |
