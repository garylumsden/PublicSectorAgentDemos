targetScope = 'resourceGroup'

extension microsoftGraphV1

@minLength(36)
@maxLength(36)
type objectId = string

type stringMap = {
  *: string
}

@minLength(1)
@maxLength(64)
param environmentName string

@minLength(1)
param location string

@minLength(1)
param appServiceHostNameSuffix string

param tags stringMap

param deployerPrincipalId objectId

@minLength(1)
@maxLength(6)
param authorizedUserObjectIds objectId[]

@minLength(1)
@maxLength(6)
param adminUserObjectIds objectId[]

@maxLength(12)
param toolsAuthorizedPrincipalObjectIds objectId[] = []

@minLength(2)
param foundryAccountName string

@minLength(2)
param foundryProjectName string

param foundryProjectPrincipalId objectId

@minLength(1)
param foundryProjectEndpoint string

@minLength(1)
param chatDeploymentName string

@allowed([
  'low'
  'medium'
  'high'
])
param reasoningEffort string

var resourceToken = uniqueString(subscription().subscriptionId, resourceGroup().id, environmentName)
var appServicePlanName = 'asp-defra-${resourceToken}'
var toolsSiteName = 'app-defra-tools-${resourceToken}'
var demo2SiteName = 'app-demo2-${resourceToken}'
var cosmosAccountName = 'cosmos-${resourceToken}'
var logAnalyticsWorkspaceName = 'log-${resourceToken}'
var applicationInsightsName = 'appi-${resourceToken}'
var toolsIdentityName = 'id-defra-tools-${resourceToken}'
var demo2IdentityName = 'id-demo2-${resourceToken}'
var cosmosDatabaseName = 'defra-demos'
var demo2ContainerName = 'demo2-flood-support-cases'
var toolsSiteUrl = 'https://${toolsSiteName}.${appServiceHostNameSuffix}'
var demo2SiteUrl = 'https://${demo2SiteName}.${appServiceHostNameSuffix}'
var toolsAudience = 'api://${tenant().tenantId}/${toolsSiteName}'
var demo2Audience = 'api://${tenant().tenantId}/${demo2SiteName}'
var entraIssuer = '${environment().authentication.loginEndpoint}${tenant().tenantId}/v2.0'
var allowedUserPrincipalIds = union(authorizedUserObjectIds, adminUserObjectIds)
var allowedToolsPrincipalIds = union(
  union(
    [
      foundryProjectPrincipalId
      demo2Identity.outputs.principalId
    ],
    allowedUserPrincipalIds
  ),
  toolsAuthorizedPrincipalObjectIds
)
var cosmosResourceId = resourceId('Microsoft.DocumentDB/databaseAccounts', cosmosAccountName)
var demo2CosmosScope = '${cosmosResourceId}/dbs/${cosmosDatabaseName}/colls/${demo2ContainerName}'
var cosmosDataContributorRoleId = '00000000-0000-0000-0000-000000000002'
var foundryUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d'
var foundryProjectManagerRoleId = 'eadc314b-1a2d-4efa-be10-5d325db5065e'
var logAnalyticsReaderRoleId = '73c42c96-874c-492b-b04d-ab87d138a893'
var websiteContributorRoleId = 'de139f84-1756-47ae-9be6-808fbbe84772'

module toolsIdentity 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  params: {
    name: toolsIdentityName
    location: location
    tags: tags
  }
}

module demo2Identity 'br/public:avm/res/managed-identity/user-assigned-identity:0.6.0' = {
  params: {
    name: demo2IdentityName
    location: location
    tags: tags
  }
}

module logAnalytics 'br/public:avm/res/operational-insights/workspace:0.15.1' = {
  params: {
    name: logAnalyticsWorkspaceName
    location: location
    dataRetention: 30
    diagnosticSettings: [
      {
        name: 'send-to-log-analytics'
        useThisWorkspace: true
        logAnalyticsDestinationType: 'Dedicated'
        logCategoriesAndGroups: [
          {
            categoryGroup: 'allLogs'
          }
        ]
        metricCategories: [
          {
            category: 'AllMetrics'
          }
        ]
      }
    ]
    forceCmkForQuery: false
    tags: tags
  }
}

module applicationInsights 'br/public:avm/res/insights/component:0.7.2' = {
  params: {
    name: applicationInsightsName
    location: location
    applicationType: 'web'
    workspaceResourceId: logAnalytics.outputs.resourceId
    retentionInDays: 30
    diagnosticSettings: [
      {
        name: 'send-to-log-analytics'
        workspaceResourceId: logAnalytics.outputs.resourceId
        logAnalyticsDestinationType: 'Dedicated'
        logCategoriesAndGroups: [
          {
            categoryGroup: 'allLogs'
          }
        ]
        metricCategories: [
          {
            category: 'AllMetrics'
          }
        ]
      }
    ]
    tags: tags
  }
}

var fullDiagnosticSettings = [
  {
    name: 'send-to-log-analytics'
    workspaceResourceId: logAnalytics.outputs.resourceId
    logAnalyticsDestinationType: 'Dedicated'
    logCategoriesAndGroups: [
      {
        categoryGroup: 'allLogs'
      }
    ]
    metricCategories: [
      {
        category: 'AllMetrics'
      }
    ]
  }
]

var metricDiagnosticSettings = [
  {
    name: 'send-to-log-analytics'
    workspaceResourceId: logAnalytics.outputs.resourceId
    logAnalyticsDestinationType: 'Dedicated'
    metricCategories: [
      {
        category: 'AllMetrics'
      }
    ]
  }
]

var cosmosDiagnosticSettings = [
  {
    name: 'send-to-log-analytics'
    workspaceResourceId: logAnalytics.outputs.resourceId
    logAnalyticsDestinationType: 'Dedicated'
    logCategoriesAndGroups: [
      {
        categoryGroup: 'allLogs'
      }
    ]
    metricCategories: [
      {
        category: 'SLI'
      }
      {
        category: 'Requests'
      }
    ]
  }
]

module appServicePlan 'br/public:avm/res/web/serverfarm:0.7.0' = {
  params: {
    name: appServicePlanName
    location: location
    kind: 'linux'
    reserved: true
    skuName: 'P0v3'
    skuCapacity: 1
    zoneRedundant: false
    diagnosticSettings: metricDiagnosticSettings
    tags: tags
  }
}

module cosmosAccount 'br/public:avm/res/document-db/database-account:0.19.0' = {
  params: {
    name: cosmosAccountName
    location: location
    capabilitiesToAdd: [
      'EnableServerless'
    ]
    defaultConsistencyLevel: 'Session'
    disableKeyBasedMetadataWriteAccess: true
    disableLocalAuthentication: true
    enableAutomaticFailover: false
    networkRestrictions: {
      networkAclBypass: 'AzureServices'
      publicNetworkAccess: 'Enabled'
    }
    sqlDatabases: [
      {
        name: cosmosDatabaseName
        containers: [
          {
            name: demo2ContainerName
            paths: [
              '/caseId'
            ]
          }
        ]
      }
    ]
    sqlRoleAssignments: [
      {
        name: guid(demo2CosmosScope, demo2Identity.outputs.principalId, cosmosDataContributorRoleId)
        principalId: demo2Identity.outputs.principalId
        roleDefinitionId: cosmosDataContributorRoleId
        scope: demo2CosmosScope
      }
      {
        name: guid(cosmosResourceId, deployerPrincipalId, cosmosDataContributorRoleId)
        principalId: deployerPrincipalId
        roleDefinitionId: cosmosDataContributorRoleId
        scope: cosmosResourceId
      }
    ]
    zoneRedundant: false
    diagnosticSettings: cosmosDiagnosticSettings
    tags: tags
  }
}

resource demo2Application 'Microsoft.Graph/applications@v1.0' = {
  displayName: 'Cross-Government Flood Support ${environmentName}'
  uniqueName: 'defra-demo2-${resourceToken}'
  description: 'Microsoft Entra application for the simulated flood support workflow.'
  signInAudience: 'AzureADMyOrg'
  identifierUris: [
    demo2Audience
  ]
  api: {
    requestedAccessTokenVersion: 2
  }
  web: {
    homePageUrl: demo2SiteUrl
    logoutUrl: '${demo2SiteUrl}/.auth/logout'
    redirectUris: [
      '${demo2SiteUrl}/.auth/login/aad/callback'
    ]
    implicitGrantSettings: {
      enableAccessTokenIssuance: false
      enableIdTokenIssuance: true
    }
  }
}

resource toolsApplication 'Microsoft.Graph/applications@v1.0' = {
  displayName: 'Flood Support Tools API ${environmentName}'
  uniqueName: 'defra-tools-${resourceToken}'
  description: 'Microsoft Entra protected audience for the flood support MCP API.'
  signInAudience: 'AzureADMyOrg'
  identifierUris: [
    toolsAudience
  ]
  api: {
    requestedAccessTokenVersion: 2
  }
}

module toolsSite 'br/public:avm/res/web/site:0.23.1' = {
  params: {
    name: toolsSiteName
    location: location
    kind: 'app,linux'
    httpsOnly: true
    serverFarmResourceId: appServicePlan.outputs.resourceId
    clientAffinityEnabled: false
    managedIdentities: {
      userAssignedResourceIds: [
        toolsIdentity.outputs.resourceId
      ]
    }
    basicPublishingCredentialsPolicies: [
      {
        name: 'ftp'
        allow: false
      }
      {
        name: 'scm'
        allow: false
      }
    ]
    siteConfig: {
      alwaysOn: true
      ftpsState: 'Disabled'
      healthCheckPath: '/health'
      http20Enabled: true
      linuxFxVersion: 'DOTNETCORE|10.0'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      use32BitWorkerProcess: false
    }
    diagnosticSettings: fullDiagnosticSettings
    configs: [
      {
        name: 'appsettings'
        properties: {
          APPLICATIONINSIGHTS_CONNECTION_STRING: applicationInsights.outputs.connectionString
          MCP_FLOOD_SUPPORT_ACTION_CALLER_PRINCIPAL_ID: '${foundryProjectPrincipalId},${demo2Identity.outputs.principalId}'
          TOOLS_AUDIENCE: toolsAudience
          WEBSITE_RUN_FROM_PACKAGE: '1'
        }
      }
      {
        name: 'authsettingsV2'
        properties: {
          globalValidation: {
            excludedPaths: [
              '/health'
            ]
            requireAuthentication: true
            unauthenticatedClientAction: 'Return401'
          }
          httpSettings: {
            requireHttps: true
            routes: {
              apiPrefix: '/.auth'
            }
          }
          identityProviders: {
            azureActiveDirectory: {
              enabled: true
              login: {
                disableWWWAuthenticate: false
              }
              registration: {
                clientId: toolsApplication.appId
                openIdIssuer: entraIssuer
              }
              validation: {
                allowedAudiences: [
                  toolsApplication.appId
                  toolsAudience
                ]
                defaultAuthorizationPolicy: {
                  allowedPrincipals: {
                    identities: allowedToolsPrincipalIds
                  }
                }
              }
            }
          }
          login: {
            tokenStore: {
              enabled: false
            }
          }
          platform: {
            enabled: true
            runtimeVersion: '~1'
          }
        }
      }
    ]
    roleAssignments: [
      {
        name: guid(resourceId('Microsoft.Web/sites', toolsSiteName), deployerPrincipalId, websiteContributorRoleId)
        principalId: deployerPrincipalId
        roleDefinitionIdOrName: websiteContributorRoleId
      }
    ]
    tags: union(tags, {
      'azd-service-name': 'defra-tools'
    })
  }
}

module demo2Site 'br/public:avm/res/web/site:0.23.1' = {
  params: {
    name: demo2SiteName
    location: location
    kind: 'app,linux'
    httpsOnly: true
    serverFarmResourceId: appServicePlan.outputs.resourceId
    clientAffinityEnabled: true
    managedIdentities: {
      userAssignedResourceIds: [
        demo2Identity.outputs.resourceId
      ]
    }
    basicPublishingCredentialsPolicies: [
      {
        name: 'ftp'
        allow: false
      }
      {
        name: 'scm'
        allow: false
      }
    ]
    siteConfig: {
      alwaysOn: true
      ftpsState: 'Disabled'
      healthCheckPath: '/health'
      http20Enabled: true
      linuxFxVersion: 'DOTNETCORE|10.0'
      minTlsVersion: '1.2'
      scmMinTlsVersion: '1.2'
      use32BitWorkerProcess: false
      webSocketsEnabled: true
    }
    diagnosticSettings: fullDiagnosticSettings
    configs: [
      {
        name: 'appsettings'
        properties: {
          APPLICATIONINSIGHTS_CONNECTION_STRING: applicationInsights.outputs.connectionString
          AZURE_CLIENT_ID: demo2Identity.outputs.clientId
          AZURE_TENANT_ID: tenant().tenantId
          AZURE_AI_MODEL_DEPLOYMENT_NAME: chatDeploymentName
          AZURE_AI_REASONING_EFFORT: reasoningEffort
          AZURE_AI_PROJECT_ENDPOINT: foundryProjectEndpoint
          AZURE_COSMOS_CONTAINER_NAME: demo2ContainerName
          AZURE_COSMOS_DATABASE_NAME: cosmosDatabaseName
          AZURE_COSMOS_ENDPOINT: cosmosAccount.outputs.endpoint
          DEFRA_TOOLS_AUDIENCE: toolsAudience
          DEFRA_TOOLS_MCP_URL: '${toolsSiteUrl}/mcp/flood-support'
          WEBSITE_RUN_FROM_PACKAGE: '1'
        }
      }
      {
        name: 'authsettingsV2'
        properties: {
          globalValidation: {
            excludedPaths: [
              '/health'
            ]
            redirectToProvider: 'azureActiveDirectory'
            requireAuthentication: true
            unauthenticatedClientAction: 'RedirectToLoginPage'
          }
          httpSettings: {
            requireHttps: true
            routes: {
              apiPrefix: '/.auth'
            }
          }
          identityProviders: {
            azureActiveDirectory: {
              enabled: true
              registration: {
                clientId: demo2Application.appId
                clientSecretSettingName: 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET'
                openIdIssuer: entraIssuer
              }
              validation: {
                allowedAudiences: [
                  demo2Application.appId
                  demo2Audience
                ]
                defaultAuthorizationPolicy: {
                  allowedPrincipals: {
                    identities: allowedUserPrincipalIds
                  }
                }
              }
            }
          }
          login: {
            tokenStore: {
              enabled: true
            }
          }
          platform: {
            enabled: true
            runtimeVersion: '~1'
          }
        }
      }
      {
        name: 'slotConfigNames'
        properties: {
          appSettingNames: [
            'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET'
          ]
        }
      }
    ]
    roleAssignments: [
      {
        name: guid(resourceId('Microsoft.Web/sites', demo2SiteName), deployerPrincipalId, websiteContributorRoleId)
        principalId: deployerPrincipalId
        roleDefinitionIdOrName: websiteContributorRoleId
      }
    ]
    tags: union(tags, {
      'azd-service-name': 'demo2-web'
    })
  }
}

resource foundryAccount 'Microsoft.CognitiveServices/accounts@2026-05-01' existing = {
  name: foundryAccountName
}

resource foundryProject 'Microsoft.CognitiveServices/accounts/projects@2026-05-01' existing = {
  parent: foundryAccount
  name: foundryProjectName
}

resource foundryAccountDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'send-to-log-analytics'
  scope: foundryAccount
  properties: {
    workspaceId: logAnalytics.outputs.resourceId
    logAnalyticsDestinationType: 'Dedicated'
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
  }
}

resource foundryProjectDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'send-to-log-analytics'
  scope: foundryProject
  properties: {
    workspaceId: logAnalytics.outputs.resourceId
    logAnalyticsDestinationType: 'Dedicated'
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
  }
}

resource applicationInsightsComponent 'Microsoft.Insights/components@2020-02-02' existing = {
  name: applicationInsightsName
  dependsOn: [
    applicationInsights
  ]
}

resource applicationInsightsConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2026-05-01' = {
  parent: foundryProject
  name: applicationInsightsName
  properties: {
    category: 'AppInsights'
    target: applicationInsights.outputs.resourceId
    authType: 'ApiKey'
    isSharedToAll: true
    credentials: {
      key: applicationInsights.outputs.connectionString
    }
    metadata: {
      ApiType: 'Azure'
      ResourceId: applicationInsights.outputs.resourceId
    }
  }
}

resource foundryProjectApplicationInsightsLogAnalyticsReaderRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(applicationInsightsComponent.id, foundryProjectPrincipalId, logAnalyticsReaderRoleId)
  scope: applicationInsightsComponent
  properties: {
    principalId: foundryProjectPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', logAnalyticsReaderRoleId)
  }
  dependsOn: [
    applicationInsights
  ]
}

resource floodSupportToolsConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-09-01' = {
  parent: foundryProject
  name: 'flood-support-connection'
  properties: {
    category: 'RemoteTool'
    // Preserve the documented project identity contract; see infra/README.md for the schema exception.
    #disable-next-line BCP036
    authType: 'ProjectManagedIdentity'
    target: '${toolsSiteUrl}/mcp/flood-support'
    audience: toolsAudience
    metadata: {}
  }
  dependsOn: [
    toolsSite
  ]
}

resource foundryProjectIdentityUserRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryAccount.id, foundryProjectPrincipalId, foundryUserRoleId)
  scope: foundryAccount
  properties: {
    principalId: foundryProjectPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', foundryUserRoleId)
  }
}

// Demo2 creates durable Project OpenAI conversations before invoking its prompt agent.
resource demo2IdentityFoundryUserRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryProject.id, demo2IdentityName, foundryUserRoleId)
  scope: foundryProject
  properties: {
    principalId: demo2Identity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', foundryUserRoleId)
  }
}

resource deployerFoundryProjectManagerRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(foundryProject.id, deployerPrincipalId, foundryProjectManagerRoleId)
  scope: foundryProject
  properties: {
    principalId: deployerPrincipalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', foundryProjectManagerRoleId)
  }
}

output appServicePlanName string = appServicePlan.outputs.name
output toolsSiteName string = toolsSite.outputs.name
output toolsSiteHostname string = toolsSite.outputs.defaultHostname
output toolsSiteUrl string = 'https://${toolsSite.outputs.defaultHostname}'
output demo2SiteName string = demo2Site.outputs.name
output demo2SiteHostname string = demo2Site.outputs.defaultHostname
output demo2SiteUrl string = 'https://${demo2Site.outputs.defaultHostname}'
output toolsMcpFloodSupportUrl string = 'https://${toolsSite.outputs.defaultHostname}/mcp/flood-support'
output toolsAudience string = toolsAudience
output toolsApplicationClientId string = toolsApplication.appId
output demo2ApplicationClientId string = demo2Application.appId
output cosmosAccountId string = cosmosAccount.outputs.resourceId
output cosmosAccountName string = cosmosAccount.outputs.name
output cosmosEndpoint string = cosmosAccount.outputs.endpoint
output cosmosDatabaseName string = cosmosDatabaseName
output demo2ContainerName string = demo2ContainerName
output logAnalyticsWorkspaceResourceId string = logAnalytics.outputs.resourceId
output logAnalyticsWorkspaceName string = logAnalytics.outputs.name
output logAnalyticsWorkspaceId string = logAnalytics.outputs.logAnalyticsWorkspaceId
output applicationInsightsResourceId string = applicationInsights.outputs.resourceId
output applicationInsightsName string = applicationInsights.outputs.name
output applicationInsightsConnectionName string = applicationInsightsConnection.name
output applicationInsightsApplicationId string = applicationInsights.outputs.applicationId
output applicationInsightsConnectionString string = applicationInsights.outputs.connectionString
output toolsIdentityResourceId string = toolsIdentity.outputs.resourceId
output toolsIdentityClientId string = toolsIdentity.outputs.clientId
output toolsIdentityPrincipalId string = toolsIdentity.outputs.principalId
output demo2IdentityResourceId string = demo2Identity.outputs.resourceId
output demo2IdentityClientId string = demo2Identity.outputs.clientId
output demo2IdentityPrincipalId string = demo2Identity.outputs.principalId
