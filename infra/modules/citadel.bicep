metadata name = 'Citadel A2A gateway'
metadata description = 'Deploys API Management as a governed delegated-OAuth entry point for the A2A adapter.'

param location string
param apimName string

@allowed([
  'Developer'
  'StandardV2'
  'PremiumV2'
])
param apimSkuName string

param apimSkuCapacity int
param publisherName string
param publisherEmail string
param entraLoginEndpoint string
param tenantId string
param adapterBackendUrl string
param adapterApiAudience string
param adapterDelegatedScope string
@minLength(1)
param allowedOrigins array
param specialistAgentIds array = []
param appInsightsName string
param logAnalyticsWorkspaceId string
param accessPrincipalId string
param adapterPrincipalId string
param tags object

var apiManagementServiceContributorRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '312a565d-c81f-4fd8-895a-4e21e48d571c'
)
var readerRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  'acdd72a7-3385-48ef-bd42-f606fba81ae7'
)
var adapterBareAudience = replace(adapterApiAudience, 'api://', '')
var apiPath = 'copilot-studio'
var agentCardPath = '.well-known/agent-card.json'
var runtimePath = 'a2a/copilot-studio'
var originElements = map(allowedOrigins, origin => '<origin>${replace(replace(replace(origin, '&', '&amp;'), '<', '&lt;'), '>', '&gt;')}</origin>')
var apiPolicyValue = replace(
  replace(loadTextContent('../policies/citadel-api.xml'), '__ALLOWED_ORIGINS__', join(originElements, '')),
  '__BACKEND_HEADERS__',
  ''
)
var delegatedPolicyTemplate = replace(
  loadTextContent('../policies/citadel-delegated-operation.xml'),
  '__ENTRA_LOGIN_ENDPOINT__',
  entraLoginEndpoint
)
var discoveryPolicyValue = loadTextContent('../policies/citadel-discovery-operation.xml')
var runtimePolicyValue = replace(
  replace(delegatedPolicyTemplate, '__RATE_LIMIT_CALLS__', '60'),
  '__REQUEST_VALIDATION__',
  loadTextContent('../policies/citadel-validate-json.xml')
)
var tracePolicyValue = replace(
  replace(delegatedPolicyTemplate, '__RATE_LIMIT_CALLS__', '600'),
  '__REQUEST_VALIDATION__',
  ''
)

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
}

resource apim 'Microsoft.ApiManagement/service@2024-05-01' = {
  name: apimName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  sku: {
    name: apimSkuName
    capacity: apimSkuCapacity
  }
  tags: tags
  properties: {
    customProperties: {
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Backend.Protocols.Ssl30': 'False'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Backend.Protocols.Tls10': 'False'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Backend.Protocols.Tls11': 'False'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Ciphers.TripleDes168': 'False'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Protocols.Ssl30': 'False'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Protocols.Tls10': 'False'
      'Microsoft.WindowsAzure.ApiManagement.Gateway.Security.Protocols.Tls11': 'False'
    }
    publisherEmail: publisherEmail
    publisherName: publisherName
    publicNetworkAccess: 'Enabled'
    virtualNetworkType: 'None'
  }
}

resource tenantNamedValue 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  parent: apim
  name: 'entra-tenant-id'
  properties: {
    displayName: 'entra-tenant-id'
    secret: false
    value: tenantId
  }
}

resource audienceNamedValue 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  parent: apim
  name: 'adapter-api-audience'
  properties: {
    displayName: 'adapter-api-audience'
    secret: false
    value: adapterApiAudience
  }
}

resource bareAudienceNamedValue 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  parent: apim
  name: 'adapter-api-bare-audience'
  properties: {
    displayName: 'adapter-api-bare-audience'
    secret: false
    value: adapterBareAudience
  }
}

resource delegatedScopeNamedValue 'Microsoft.ApiManagement/service/namedValues@2024-05-01' = {
  parent: apim
  name: 'adapter-delegated-scope'
  properties: {
    displayName: 'adapter-delegated-scope'
    secret: false
    value: adapterDelegatedScope
  }
}

resource api 'Microsoft.ApiManagement/service/apis@2024-05-01' = {
  parent: apim
  name: 'copilot-studio-a2a'
  properties: {
    apiType: 'http'
    displayName: 'Copilot Studio A2A'
    path: apiPath
    protocols: [
      'https'
    ]
    serviceUrl: adapterBackendUrl
    subscriptionRequired: false
    type: 'http'
  }
}

resource apiPolicy 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = {
  parent: api
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: apiPolicyValue
  }
}

resource agentCardOperation 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  parent: api
  name: 'get-agent-card'
  properties: {
    description: 'Public A2A agent discovery document.'
    displayName: 'Get agent card'
    method: 'GET'
    responses: [
      {
        statusCode: 200
      }
    ]
    templateParameters: []
    urlTemplate: '/${agentCardPath}'
  }
}

resource agentCardPolicy 'Microsoft.ApiManagement/service/apis/operations/policies@2024-05-01' = {
  parent: agentCardOperation
  name: 'policy'
  dependsOn: [
    apiPolicy
  ]
  properties: {
    format: 'rawxml'
    value: discoveryPolicyValue
  }
}

resource agentCatalogOperation 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  parent: api
  name: 'get-agent-catalog'
  properties: {
    displayName: 'Get agent catalog'
    method: 'GET'
    urlTemplate: '/api/agents'
    templateParameters: []
    responses: [
      {
        statusCode: 200
      }
    ]
  }
}

resource traceOperation 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  parent: api
  name: 'get-caller-trace'
  properties: {
    displayName: 'Get caller-scoped trace'
    method: 'GET'
    urlTemplate: '/api/traces/{traceId}'
    templateParameters: [
      {
        name: 'traceId'
        type: 'string'
        required: true
      }
    ]
    responses: [
      {
        statusCode: 200
      }
      {
        statusCode: 401
      }
      {
        statusCode: 404
      }
    ]
  }
}

resource tracePolicy 'Microsoft.ApiManagement/service/apis/operations/policies@2024-05-01' = {
  parent: traceOperation
  name: 'policy'
  dependsOn: [
    tenantNamedValue
    audienceNamedValue
    bareAudienceNamedValue
    delegatedScopeNamedValue
    apiPolicy
  ]
  properties: {
    format: 'rawxml'
    value: tracePolicyValue
  }
}

resource runtimeOperation 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = {
  parent: api
  name: 'invoke-a2a-runtime'
  properties: {
    description: 'Authenticated A2A JSON-RPC endpoint.'
    displayName: 'Invoke A2A runtime'
    method: 'POST'
    request: {
      headers: [
        {
          name: 'Authorization'
          required: true
          type: 'string'
        }
        {
          name: 'A2A-Version'
          required: false
          type: 'string'
        }
      ]
      representations: [
        {
          contentType: 'application/json'
        }
      ]
    }
    responses: [
      {
        statusCode: 200
      }
      {
        statusCode: 400
      }
      {
        statusCode: 401
      }
      {
        statusCode: 429
      }
    ]
    templateParameters: []
    urlTemplate: '/${runtimePath}'
  }
}

resource runtimePolicy 'Microsoft.ApiManagement/service/apis/operations/policies@2024-05-01' = {
  parent: runtimeOperation
  name: 'policy'
  dependsOn: [
    tenantNamedValue
    audienceNamedValue
    bareAudienceNamedValue
    delegatedScopeNamedValue
    apiPolicy
  ]
  properties: {
    format: 'rawxml'
    value: runtimePolicyValue
  }
}

resource specialistApis 'Microsoft.ApiManagement/service/apis@2024-05-01' = [for agentId in specialistAgentIds: {
  parent: apim
  name: 'copilot-studio-a2a-${agentId}'
  properties: {
    displayName: 'A2A specialist - ${agentId}'
    path: '${apiPath}/a2a-agents/${agentId}'
    protocols: [
      'https'
    ]
    serviceUrl: '${adapterBackendUrl}/a2a-agents/${agentId}'
    subscriptionRequired: false
    type: 'http'
  }
}]

resource specialistApiPolicies 'Microsoft.ApiManagement/service/apis/policies@2024-05-01' = [for (agentId, index) in specialistAgentIds: {
  parent: specialistApis[index]
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: apiPolicyValue
  }
}]

resource specialistCards 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = [for (agentId, index) in specialistAgentIds: {
  parent: specialistApis[index]
  name: 'get-agent-card'
  properties: {
    displayName: 'Get specialist agent card'
    method: 'GET'
    urlTemplate: '/.well-known/agent-card.json'
    templateParameters: []
    responses: []
  }
}]

resource specialistCardPolicies 'Microsoft.ApiManagement/service/apis/operations/policies@2024-05-01' = [for (agentId, index) in specialistAgentIds: {
  parent: specialistCards[index]
  name: 'policy'
  dependsOn: [
    specialistApiPolicies
  ]
  properties: {
    format: 'rawxml'
    value: discoveryPolicyValue
  }
}]

resource specialistRuntimeCards 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = [for (agentId, index) in specialistAgentIds: {
  parent: specialistApis[index]
  name: 'get-runtime-agent-card'
  properties: {
    displayName: 'Get runtime-relative specialist agent card'
    method: 'GET'
    urlTemplate: '/a2a/.well-known/agent-card.json'
    templateParameters: []
    responses: []
  }
}]

resource specialistRuntimeCardPolicies 'Microsoft.ApiManagement/service/apis/operations/policies@2024-05-01' = [for (agentId, index) in specialistAgentIds: {
  parent: specialistRuntimeCards[index]
  name: 'policy'
  dependsOn: [
    specialistApiPolicies
  ]
  properties: {
    format: 'rawxml'
    value: discoveryPolicyValue
  }
}]

resource specialistLegacyCards 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = [for (agentId, index) in specialistAgentIds: {
  parent: specialistApis[index]
  name: 'get-legacy-agent-card'
  properties: {
    displayName: 'Get legacy specialist agent card'
    method: 'GET'
    urlTemplate: '/.well-known/agent.json'
    templateParameters: []
    responses: []
  }
}]

resource specialistLegacyCardPolicies 'Microsoft.ApiManagement/service/apis/operations/policies@2024-05-01' = [for (agentId, index) in specialistAgentIds: {
  parent: specialistLegacyCards[index]
  name: 'policy'
  dependsOn: [
    specialistApiPolicies
  ]
  properties: {
    format: 'rawxml'
    value: discoveryPolicyValue
  }
}]

resource specialistRuntimeLegacyCards 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = [for (agentId, index) in specialistAgentIds: {
  parent: specialistApis[index]
  name: 'get-runtime-legacy-agent-card'
  properties: {
    displayName: 'Get legacy runtime-relative specialist agent card'
    method: 'GET'
    urlTemplate: '/a2a/.well-known/agent.json'
    templateParameters: []
    responses: []
  }
}]

resource specialistRuntimeLegacyCardPolicies 'Microsoft.ApiManagement/service/apis/operations/policies@2024-05-01' = [for (agentId, index) in specialistAgentIds: {
  parent: specialistRuntimeLegacyCards[index]
  name: 'policy'
  dependsOn: [
    specialistApiPolicies
  ]
  properties: {
    format: 'rawxml'
    value: discoveryPolicyValue
  }
}]

resource specialistRuntimes 'Microsoft.ApiManagement/service/apis/operations@2024-05-01' = [for (agentId, index) in specialistAgentIds: {
  parent: specialistApis[index]
  name: 'invoke-a2a-runtime'
  properties: {
    displayName: 'Invoke specialist A2A runtime'
    method: 'POST'
    urlTemplate: '/a2a'
    request: {
      representations: [
        {
          contentType: 'application/json'
        }
      ]
    }
    templateParameters: []
    responses: []
  }
}]

resource specialistRuntimePolicies 'Microsoft.ApiManagement/service/apis/operations/policies@2024-05-01' = [for (agentId, index) in specialistAgentIds: {
  parent: specialistRuntimes[index]
  name: 'policy'
  dependsOn: [
    tenantNamedValue
    audienceNamedValue
    bareAudienceNamedValue
    delegatedScopeNamedValue
    specialistApiPolicies
  ]
  properties: {
    format: 'rawxml'
    value: runtimePolicyValue
  }
}]

resource appInsightsLogger 'Microsoft.ApiManagement/service/loggers@2024-05-01' = {
  parent: apim
  name: 'application-insights'
  properties: {
    credentials: {
      connectionString: appInsights.properties.ConnectionString
    }
    description: 'Workspace-based Application Insights logger for A2A gateway telemetry.'
    isBuffered: true
    loggerType: 'applicationInsights'
    resourceId: appInsights.id
  }
}

resource apiDiagnostic 'Microsoft.ApiManagement/service/apis/diagnostics@2024-05-01' = {
  parent: api
  name: 'applicationinsights'
  properties: {
    alwaysLog: 'allErrors'
    backend: {
      request: {
        body: {
          bytes: 0
        }
        headers: [
          'Content-Type'
          'A2A-Version'
          'traceparent'
          'X-Correlation-ID'
        ]
      }
      response: {
        body: {
          bytes: 0
        }
        headers: [
          'Content-Type'
          'traceparent'
          'X-Correlation-ID'
        ]
      }
    }
    frontend: {
      request: {
        body: {
          bytes: 0
        }
        headers: [
          'Content-Type'
          'A2A-Version'
          'traceparent'
          'X-Correlation-ID'
        ]
      }
      response: {
        body: {
          bytes: 0
        }
        headers: [
          'Content-Type'
          'traceparent'
          'X-Correlation-ID'
        ]
      }
    }
    httpCorrelationProtocol: 'W3C'
    logClientIp: false
    loggerId: appInsightsLogger.id
    metrics: true
    sampling: {
      percentage: 100
      samplingType: 'fixed'
    }
    verbosity: 'information'
  }
}

resource platformDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'send-to-log-analytics'
  scope: apim
  properties: {
    logs: [
      {
        categoryGroup: 'allLogs'
        enabled: true
      }
    ]
    metrics: [
      {
        category: 'AllMetrics'
        enabled: true
      }
    ]
    workspaceId: logAnalyticsWorkspaceId
  }
}

resource apimAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(apim.id, accessPrincipalId, apiManagementServiceContributorRoleDefinitionId)
  scope: apim
  properties: {
    principalId: accessPrincipalId
    principalType: 'User'
    roleDefinitionId: apiManagementServiceContributorRoleDefinitionId
  }
}

resource adapterApimReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(apim.id, adapterPrincipalId, readerRoleDefinitionId)
  scope: apim
  properties: {
    principalId: adapterPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: readerRoleDefinitionId
  }
}

output apimId string = apim.id
output apimPrincipalId string = apim.identity.principalId
output gatewayUrl string = apim.properties.gatewayUrl
output apiBaseUrl string = '${apim.properties.gatewayUrl}/${apiPath}'
output agentCardUrl string = '${apim.properties.gatewayUrl}/${apiPath}/${agentCardPath}'
output a2aRuntimeUrl string = '${apim.properties.gatewayUrl}/${apiPath}/${runtimePath}'
