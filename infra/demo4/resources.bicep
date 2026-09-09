targetScope = 'resourceGroup'

@minLength(1)
@maxLength(64)
param environmentName string

@minLength(1)
param location string

@description('Object ID that receives package deployment access to the application.')
@minLength(36)
param deployerPrincipalId string

@description('Client ID of the Entra application registration that protects the assessment API.')
@minLength(36)
param applicationApiClientId string

@secure()
@minLength(1)
@description('Client secret of the Entra application registration that protects the web application.')
param applicationWebClientSecret string

@description('Microsoft Entra object IDs allowed to use the web application.')
@minLength(1)
param allowedUserPrincipalIds array

@description('Deployed hosted-agent Responses endpoint, when available.')
param hostedAgentEndpoint string = ''

var resourceToken = uniqueString(subscription().subscriptionId, resourceGroup().id, environmentName)
var accountName = 'aifdemo4${resourceToken}'
var projectName = 'project-demo4-${take(toLower(environmentName), 24)}'
var applicationName = 'app-demo4-${resourceToken}'
var applicationIdentityName = 'id-demo4-app-${resourceToken}'
var projectEndpoint = 'https://${accountName}.services.ai.azure.com/api/projects/${projectName}'
var foundryUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d'
var foundryProjectManagerRoleId = 'eadc314b-1a2d-4efa-be10-5d325db5065e'
var cognitiveServicesOpenAIUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
var websiteContributorRoleId = 'de139f84-1756-47ae-9be6-808fbbe84772'
var monitoringMetricsPublisherRoleId = '3913510d-42f4-4e42-8a64-420c390055eb'
var applicationInsightsConnectionName = 'appinsights'
var applicationAudience = 'api://${applicationApiClientId}'
var commonTags = {
  'azd-env-name': environmentName
  environment: environmentName
  workload: 'demo4-cross-government-control-preview'
  previewFixtureDataOnly: 'true'
  preview: 'true'
}

// App Service EasyAuth v2 is the browser gate for every Demo 4 route.
// One global action applies to all paths: unauthenticated callers are redirected to
// the Microsoft Entra login page. That includes the Razor pages, their handlers, the
// static application content, and the assessment API. Only /health stays anonymous so
// the App Service health probe can reach it. Application data is never served before
// a validated Entra sign-in.
var applicationAnonymousPaths = [
  '/health'
]

var applicationAuthSettings = {
  platform: {
    enabled: true
    runtimeVersion: '~1'
    configFilePath: ''
  }
  globalValidation: {
    requireAuthentication: true
    unauthenticatedClientAction: 'RedirectToLoginPage'
    redirectToProvider: 'azureactivedirectory'
    excludedPaths: applicationAnonymousPaths
  }
  identityProviders: {
    azureActiveDirectory: {
      enabled: true
      registration: {
        openIdIssuer: '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
        clientId: applicationApiClientId
        clientSecretSettingName: 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET'
      }
      validation: {
        allowedAudiences: [
          applicationAudience
          applicationApiClientId
        ]
        defaultAuthorizationPolicy: {
          allowedPrincipals: {
            identities: allowedUserPrincipalIds
          }
        }
      }
      login: {
        disableWWWAuthenticate: false
      }
    }
  }
  login: {
    routes: {}
    tokenStore: {
      enabled: true
      tokenRefreshExtensionHours: 72
    }
    preserveUrlFragmentsForLogins: false
    allowedExternalRedirectUrls: []
    cookieExpiration: {
      convention: 'FixedTime'
      timeToExpiration: '08:00:00'
    }
    nonce: {
      validateNonce: true
      nonceExpirationInterval: '00:05:00'
    }
  }
  httpSettings: {
    requireHttps: true
    routes: {
      apiPrefix: '/.auth'
    }
    forwardProxy: {
      convention: 'NoProxy'
    }
  }
}

resource foundryAccount 'Microsoft.CognitiveServices/accounts@2026-05-01' = {
  name: accountName
  location: location
  kind: 'AIServices'
  identity: {
    type: 'SystemAssigned'
  }
  sku: {
    name: 'S0'
  }
  properties: {
    allowProjectManagement: true
    customSubDomainName: accountName
    disableLocalAuth: true
    dynamicThrottlingEnabled: true
    publicNetworkAccess: 'Enabled'
    restrictOutboundNetworkAccess: false
  }
  tags: union(commonTags, {
    'azd-service-name': 'ai-project'
  })
}

resource foundryProject 'Microsoft.CognitiveServices/accounts/projects@2026-05-01' = {
  parent: foundryAccount
  name: projectName
  location: location
  dependsOn: [
    embeddingDeployment
  ]
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'Demo 4 cross-government control investigations'
    description: 'Preview project for cross-government control investigations.'
  }
  tags: union(commonTags, {
    'azd-service-name': 'ai-project'
  })
}

resource chatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2026-05-01' = {
  parent: foundryAccount
  name: 'gpt-5.4-mini'
  sku: {
    name: 'GlobalStandard'
    capacity: 50
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'gpt-5.4-mini'
      version: '2026-03-17'
    }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2026-05-01' = {
  parent: foundryAccount
  name: 'text-embedding-3-small'
  dependsOn: [
    chatDeployment
  ]
  sku: {
    name: 'GlobalStandard'
    capacity: 20
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: 'text-embedding-3-small'
      version: '1'
    }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}

resource applicationIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: applicationIdentityName
  location: location
  tags: commonTags
}

resource applicationFoundryRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryProject.id, applicationIdentity.id, foundryUserRoleId)
  scope: foundryProject
  properties: {
    principalId: applicationIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', foundryUserRoleId)
  }
}

resource projectOpenAIUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryAccount.id, foundryProject.id, cognitiveServicesOpenAIUserRoleId)
  scope: foundryAccount
  properties: {
    principalId: foundryProject.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      cognitiveServicesOpenAIUserRoleId
    )
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-demo4-${resourceToken}'
  location: location
  properties: {
    retentionInDays: 30
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    sku: {
      name: 'PerGB2018'
    }
  }
  tags: commonTags
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-demo4-${resourceToken}'
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    DisableLocalAuth: true
    WorkspaceResourceId: logAnalytics.id
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
  tags: commonTags
}

resource applicationInsightsConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-09-01' = {
  parent: foundryProject
  name: applicationInsightsConnectionName
  properties: any({
    category: 'AppInsights'
    target: applicationInsights.id
    authType: 'ProjectManagedIdentity'
    isSharedToAll: true
    metadata: {
      ApiType: 'Azure'
      ResourceId: applicationInsights.id
      ApplicationInsightsConnectionString: applicationInsights.properties.ConnectionString
    }
  })
}

resource projectTelemetryPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(applicationInsights.id, foundryProject.id, monitoringMetricsPublisherRoleId)
  scope: applicationInsights
  properties: {
    principalId: foundryProject.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      monitoringMetricsPublisherRoleId
    )
  }
}

resource applicationTelemetryPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(applicationInsights.id, applicationIdentity.id, monitoringMetricsPublisherRoleId)
  scope: applicationInsights
  properties: {
    principalId: applicationIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      monitoringMetricsPublisherRoleId
    )
  }
}

resource applicationPlan 'Microsoft.Web/serverfarms@2024-11-01' = {
  name: 'asp-demo4-${resourceToken}'
  location: location
  kind: 'linux'
  sku: {
    name: 'B1'
    tier: 'Basic'
    capacity: 1
  }
  properties: {
    reserved: true
  }
  tags: commonTags
}

resource application 'Microsoft.Web/sites@2024-11-01' = {
  name: applicationName
  location: location
  kind: 'app,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${applicationIdentity.id}': {}
    }
  }
  properties: {
    clientAffinityEnabled: false
    httpsOnly: true
    publicNetworkAccess: 'Enabled'
    serverFarmId: applicationPlan.id
    siteConfig: {
      alwaysOn: true
      appCommandLine: 'dotnet PublicSectorAgentDemos.Demo4.Application.dll'
      ftpsState: 'Disabled'
      healthCheckPath: '/health'
      http20Enabled: true
      linuxFxVersion: 'DOTNETCORE|10.0'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      appSettings: concat(
        [
          {
            name: 'AzureAd__Audience'
            value: applicationApiClientId
          }
          {
            name: 'AzureAd__ClientId'
            value: applicationApiClientId
          }
          {
            name: 'AzureAd__Instance'
            value: environment().authentication.loginEndpoint
          }
          {
            name: 'AzureAd__TenantId'
            value: tenant().tenantId
          }
          {
            name: 'AZURE_CLIENT_ID'
            value: applicationIdentity.properties.clientId
          }
          {
            name: 'AZURE_TENANT_ID'
            value: tenant().tenantId
          }
          {
            name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
            value: applicationInsights.properties.ConnectionString
          }
          {
            name: 'FOUNDRY_PROJECT_ENDPOINT'
            value: projectEndpoint
          }
          {
            name: 'AZURE_AI_MODEL_DEPLOYMENT_NAME'
            value: chatDeployment.name
          }
          {
            name: 'MEMORY_STORE_EMBEDDING_MODEL_DEPLOYMENT_NAME'
            value: embeddingDeployment.name
          }
          {
            name: 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET'
            value: applicationWebClientSecret
          }
          {
            name: 'WEBSITE_RUN_FROM_PACKAGE'
            value: '1'
          }
        ],
        empty(hostedAgentEndpoint)
          ? []
          : [
              {
                name: 'DEMO4_HOSTED_AGENT_ENDPOINT'
                value: hostedAgentEndpoint
              }
            ]
      )
    }
  }
  tags: union(commonTags, {
    'azd-service-name': 'demo4-application'
  })
}

resource applicationAuth 'Microsoft.Web/sites/config@2024-11-01' = {
  parent: application
  name: 'authsettingsV2'
  properties: applicationAuthSettings
}

resource applicationDeploymentRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId)) {
  name: guid(application.id, deployerPrincipalId, websiteContributorRoleId)
  scope: application
  properties: {
    principalId: deployerPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', websiteContributorRoleId)
  }
}

resource deployerFoundryProjectManagerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId)) {
  name: guid(foundryProject.id, deployerPrincipalId, foundryProjectManagerRoleId)
  scope: foundryProject
  properties: {
    principalId: deployerPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', foundryProjectManagerRoleId)
  }
}

resource deployerFoundryUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerPrincipalId)) {
  name: guid(foundryProject.id, deployerPrincipalId, foundryUserRoleId)
  scope: foundryProject
  properties: {
    principalId: deployerPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', foundryUserRoleId)
  }
}

output accountName string = foundryAccount.name
output projectName string = foundryProject.name
output projectId string = foundryProject.id
output projectEndpoint string = projectEndpoint
output chatDeploymentName string = chatDeployment.name
output embeddingDeploymentName string = embeddingDeployment.name
output applicationName string = application.name
output applicationIdentityClientId string = applicationIdentity.properties.clientId
output applicationIdentityPrincipalId string = applicationIdentity.properties.principalId
output applicationInsightsResourceId string = applicationInsights.id
output applicationInsightsName string = applicationInsights.name
output applicationInsightsConnectionName string = applicationInsightsConnection.name
