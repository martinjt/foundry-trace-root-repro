// Everything the repro runs on, in one resource group. No Application Insights, no Log Analytics:
// the Aspire Dashboard is the only place telemetry goes.

@description('A region where Foundry hosted agents are available.')
param location string = 'uksouth'

@description('Short suffix that keeps the globally named resources unique.')
param suffix string = uniqueString(resourceGroup().id)

@description('Object id of whoever runs deploy.sh; it creates the agent and its versions.')
param deployerPrincipalId string

@description('User, or ServicePrincipal when deploying from automation.')
param deployerPrincipalType string = 'User'

@description('The caller image in the registry. Empty on the first deploy, which runs before the registry exists to build it in.')
param callerImage string = ''

@description('Name of the hosted agent the caller invokes.')
param agentName string = 'trace-repro'

@secure()
@description('Key the dashboard requires on OTLP requests, sent as the x-otlp-api-key header.')
param otlpApiKey string

@secure()
@description('Token for signing in to the dashboard UI.')
param browserToken string

@secure()
@description('Key for the dashboard telemetry API (x-api-key), used to read traces back as JSON.')
param dashboardApiKey string

var projectName = 'trace-repro'
var otlpConnectionName = 'otlp'
var otlpHeadersKey = 'headers'
var dashboardImage = 'mcr.microsoft.com/dotnet/aspire-dashboard:13.6.2'

var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  foundryUser: '53ca6127-db72-4b80-b1b0-d745d6d5456d'
  foundryAgentConsumer: 'eed3b665-ab3a-47b6-8f48-c9382fb1dad6'
}

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: 'aif-repro-${suffix}'
  location: location
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: 'aif-repro-${suffix}'
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
    allowProjectManagement: true
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: account
  name: projectName
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {}
}

resource registry 'Microsoft.ContainerRegistry/registries@2025-04-01' = {
  name: 'acrrepro${suffix}'
  location: location
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    // Foundry pulls the agent image with an ARM-audience token; without this the version fails with image_pull_failed.
    policies: { azureADAuthenticationAsArmPolicy: { status: 'enabled' } }
  }
}

resource projectPullsImages 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, project.id, roles.acrPull)
  properties: {
    principalId: project.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
  }
}

resource registryConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-06-01' = {
  parent: project
  name: 'acr'
  properties: {
    authType: 'ManagedIdentity'
    category: 'ContainerRegistry'
    target: registry.properties.loginServer
    credentials: { clientId: project.identity.principalId, resourceId: registry.id }
    metadata: { ResourceId: registry.id }
  }
  dependsOn: [projectPullsImages]
}

// The documented pattern for hosted-agent OTLP headers: the agent version refers to
// ${{connections.otlp.credentials.headers}} instead of carrying the key itself.
resource otlpConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-06-01' = {
  parent: project
  name: otlpConnectionName
  properties: {
    authType: 'CustomKeys'
    category: 'CustomKeys'
    target: 'https://${dashboard.properties.configuration.ingress.fqdn}'
    credentials: { keys: { '${otlpHeadersKey}': 'x-otlp-api-key=${otlpApiKey}' } }
  }
  // Sibling connections created at the same time conflict on the project.
  dependsOn: [registryConnection]
}

resource deployerCreatesAgents 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: project
  name: guid(project.id, deployerPrincipalId, roles.foundryUser)
  properties: {
    principalId: deployerPrincipalId
    principalType: deployerPrincipalType
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.foundryUser)
  }
}

resource environment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: 'cae-repro-${suffix}'
  location: location
  properties: {}
}

// One port serves the UI, OTLP/HTTP and the telemetry API, so all of it sits behind the one HTTPS ingress.
resource dashboard 'Microsoft.App/containerApps@2025-01-01' = {
  name: 'ca-dashboard-${suffix}'
  location: location
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: { external: true, targetPort: 18888, transport: 'auto', allowInsecure: false }
      secrets: [
        { name: 'otlp-api-key', value: otlpApiKey }
        { name: 'browser-token', value: browserToken }
        { name: 'dashboard-api-key', value: dashboardApiKey }
      ]
    }
    template: {
      containers: [
        {
          name: 'dashboard'
          image: dashboardImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'DOTNET_DASHBOARD_OTLP_HTTP_ENDPOINT_URL', value: 'http://+:18888' }
            { name: 'DOTNET_DASHBOARD_OTLP_ENDPOINT_URL', value: '' }
            { name: 'DOTNET_DASHBOARD_MCP_ENDPOINT_URL', value: '' }
            { name: 'Dashboard__Otlp__AuthMode', value: 'ApiKey' }
            { name: 'Dashboard__Otlp__PrimaryApiKey', secretRef: 'otlp-api-key' }
            { name: 'Dashboard__Frontend__AuthMode', value: 'BrowserToken' }
            { name: 'Dashboard__Frontend__BrowserToken', secretRef: 'browser-token' }
            { name: 'Dashboard__Api__Enabled', value: 'true' }
            { name: 'Dashboard__Api__AuthMode', value: 'ApiKey' }
            { name: 'Dashboard__Api__PrimaryApiKey', secretRef: 'dashboard-api-key' }
          ]
        }
      ]
      // Telemetry lives in the replica's memory, so exactly one, always running.
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
}

resource callerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-caller-${suffix}'
  location: location
}

resource callerPullsImages 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, callerIdentity.id, roles.acrPull)
  properties: {
    principalId: callerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
  }
}

resource callerInvokesAgents 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: project
  name: guid(project.id, callerIdentity.id, roles.foundryAgentConsumer)
  properties: {
    principalId: callerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.foundryAgentConsumer)
  }
}

var projectEndpoint = 'https://${account.properties.customSubDomainName}.services.ai.azure.com/api/projects/${projectName}'

resource caller 'Microsoft.App/containerApps@2025-01-01' = if (!empty(callerImage)) {
  name: 'ca-caller-${suffix}'
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${callerIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: { external: true, targetPort: 8080, transport: 'auto', allowInsecure: false }
      registries: [{ server: registry.properties.loginServer, identity: callerIdentity.id }]
      #disable-next-line use-secure-value-for-secure-inputs
      secrets: [{ name: 'otlp-headers', value: 'x-otlp-api-key=${otlpApiKey}' }]
    }
    template: {
      containers: [
        {
          name: 'caller'
          image: callerImage
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: [
            { name: 'AZURE_CLIENT_ID', value: callerIdentity.properties.clientId }
            { name: 'AGENT_RESPONSES_URL', value: '${projectEndpoint}/agents/${agentName}/endpoint/protocols/openai/responses?api-version=v1' }
            { name: 'OTEL_SERVICE_NAME', value: 'caller' }
            { name: 'OTEL_EXPORTER_OTLP_ENDPOINT', value: 'https://${dashboard.properties.configuration.ingress.fqdn}' }
            { name: 'OTEL_EXPORTER_OTLP_PROTOCOL', value: 'http/protobuf' }
            { name: 'OTEL_EXPORTER_OTLP_HEADERS', secretRef: 'otlp-headers' }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
  dependsOn: [callerPullsImages]
}

output projectEndpoint string = projectEndpoint
output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output dashboardUrl string = 'https://${dashboard.properties.configuration.ingress.fqdn}'
output callerUrl string = empty(callerImage) ? '' : 'https://${caller!.properties.configuration.ingress.fqdn}'
output otlpConnectionName string = otlpConnectionName
output otlpHeadersKey string = otlpHeadersKey
