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
param appInsightsName string
param logAnalyticsWorkspaceId string
param accessPrincipalId string
param tags object

var apiManagementServiceContributorRoleDefinitionId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '312a565d-c81f-4fd8-895a-4e21e48d571c'
)
var adapterBareAudience = replace(adapterApiAudience, 'api://', '')
var apiPath = 'copilot-studio'
var agentCardPath = '.well-known/agent-card.json'
var runtimePath = 'a2a/copilot-studio'
var runtimePolicyTemplate = '''
  <policies>
    <inbound>
      <base />
      <validate-jwt header-name="Authorization"
                    require-scheme="Bearer"
                    failed-validation-httpcode="401"
                    failed-validation-error-message="Unauthorized. The delegated access token is missing or invalid."
                    output-token-variable-name="validatedJwt"
                    clock-skew="60">
        <openid-config url="__ENTRA_LOGIN_ENDPOINT__{{entra-tenant-id}}/v2.0/.well-known/openid-configuration" />
        <audiences>
          <audience>{{adapter-api-audience}}</audience>
          <audience>{{adapter-api-bare-audience}}</audience>
        </audiences>
        <issuers>
          <issuer>__ENTRA_LOGIN_ENDPOINT__{{entra-tenant-id}}/v2.0</issuer>
          <issuer>https://sts.windows.net/{{entra-tenant-id}}/</issuer>
        </issuers>
        <required-claims>
          <claim name="scp" match="any" separator=" ">
            <value>{{adapter-delegated-scope}}</value>
          </claim>
        </required-claims>
      </validate-jwt>
      <validate-content unspecified-content-type-action="prevent"
                        max-size="1048576"
                        size-exceeded-action="prevent"
                        errors-variable-name="requestBodyValidation">
        <content type="application/json" validate-as="json" action="prevent" />
      </validate-content>
      <rate-limit-by-key calls="60"
                         renewal-period="60"
                         counter-key='@(((Jwt)context.Variables["validatedJwt"]).Claims.GetValueOrDefault("tid", "unknown") + ":" + ((Jwt)context.Variables["validatedJwt"]).Claims.GetValueOrDefault("oid", "unknown"))' />
      <set-header name="X-Correlation-ID" exists-action="skip">
        <value>@(context.RequestId.ToString())</value>
      </set-header>
    </inbound>
    <backend>
      <forward-request timeout="120" />
    </backend>
    <outbound>
      <set-header name="X-Correlation-ID" exists-action="override">
        <value>@(context.Request.Headers.GetValueOrDefault("X-Correlation-ID", context.RequestId.ToString()))</value>
      </set-header>
    </outbound>
    <on-error>
      <base />
      <set-header name="X-Correlation-ID" exists-action="override">
        <value>@(context.Request.Headers.GetValueOrDefault("X-Correlation-ID", context.RequestId.ToString()))</value>
      </set-header>
    </on-error>
  </policies>
'''
var runtimePolicyValue = replace(runtimePolicyTemplate, '__ENTRA_LOGIN_ENDPOINT__', entraLoginEndpoint)

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
  ]
  properties: {
    format: 'rawxml'
    value: runtimePolicyValue
  }
}

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

output apimId string = apim.id
output apimPrincipalId string = apim.identity.principalId
output gatewayUrl string = apim.properties.gatewayUrl
output apiBaseUrl string = '${apim.properties.gatewayUrl}/${apiPath}'
output agentCardUrl string = '${apim.properties.gatewayUrl}/${apiPath}/${agentCardPath}'
output a2aRuntimeUrl string = '${apim.properties.gatewayUrl}/${apiPath}/${runtimePath}'
