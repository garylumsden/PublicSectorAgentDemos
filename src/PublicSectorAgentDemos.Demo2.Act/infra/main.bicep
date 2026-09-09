targetScope = 'subscription'

type modelDeploymentConfig = {
  name: string
  modelName: string
  modelVersion: string
  skuName: string
  capacity: int
}

@minLength(36)
@maxLength(36)
type objectId = string

@minLength(1)
@maxLength(64)
@description('Name of the azd environment.')
param environmentName string

@minLength(1)
@description('Azure region for Foundry and model deployments.')
param aiLocation string

@minLength(1)
@description('Azure region for application and shared platform resources.')
param appLocation string

@minLength(1)
@description('DNS suffix used by App Service default hostnames.')
param appServiceHostNameSuffix string

@minLength(1)
@maxLength(90)
@description('Name of the resource group to create or reuse.')
param resourceGroupName string = 'rg-${environmentName}'

@minLength(1)
@description('SKU for the Foundry AI Services account.')
param accountSkuName string = 'S0'

@description('Chat model deployment configuration.')
param chatDeployment modelDeploymentConfig

@allowed([
  'low'
  'medium'
  'high'
])
@description('Reasoning effort used by all GPT agents.')
param reasoningEffort string = 'low'

@maxLength(6)
@description('Optional Microsoft Entra user object IDs allowed to access Demo 2. Defaults to the deployment principal.')
param authorizedUserObjectIds objectId[] = []

@maxLength(6)
@description('Optional Microsoft Entra administrator user object IDs allowed to access Demo 2. Defaults to the deployment principal.')
param adminUserObjectIds objectId[] = []

@maxLength(12)
@description('Additional application or service-principal object IDs allowed to call the tools API.')
param toolsAuthorizedPrincipalObjectIds objectId[] = []

@description('Object ID of the deployment principal in the selected subscription tenant.')
param deployerPrincipalId objectId

var tags = {
  'azd-env-name': environmentName
}
var resourceGroupTags = union(tags, {
  SecurityControl: 'Ignore'
})
var effectiveAuthorizedUserObjectIds = empty(authorizedUserObjectIds) ? [deployerPrincipalId] : authorizedUserObjectIds
var effectiveAdminUserObjectIds = empty(adminUserObjectIds) ? [deployerPrincipalId] : adminUserObjectIds

resource resourceGroup 'Microsoft.Resources/resourceGroups@2025-04-01' = {
  name: resourceGroupName
  location: appLocation
  tags: resourceGroupTags
}

module foundry './modules/foundry.bicep' = {
  scope: resourceGroup
  params: {
    environmentName: environmentName
    location: aiLocation
    accountSkuName: accountSkuName
    chatDeployment: chatDeployment
    tags: tags
  }
}

module platform './modules/platform.bicep' = {
  scope: resourceGroup
  params: {
    environmentName: environmentName
    location: appLocation
    appServiceHostNameSuffix: appServiceHostNameSuffix
    tags: tags
    deployerPrincipalId: deployerPrincipalId
    authorizedUserObjectIds: effectiveAuthorizedUserObjectIds
    adminUserObjectIds: effectiveAdminUserObjectIds
    toolsAuthorizedPrincipalObjectIds: toolsAuthorizedPrincipalObjectIds
    foundryAccountName: foundry.outputs.accountName
    foundryProjectName: foundry.outputs.projectName
    foundryProjectPrincipalId: foundry.outputs.projectPrincipalId
    foundryProjectEndpoint: foundry.outputs.projectEndpoint
    chatDeploymentName: foundry.outputs.chatDeploymentName
    reasoningEffort: reasoningEffort
  }
}

output AZURE_LOCATION string = aiLocation
output AZURE_APP_LOCATION string = appLocation
output AZURE_AI_LOCATION string = aiLocation
output AZURE_TENANT_ID string = tenant().tenantId
output AZURE_RESOURCE_GROUP string = resourceGroup.name
output AZURE_AI_ACCOUNT_ID string = foundry.outputs.accountId
output AZURE_AI_ACCOUNT_NAME string = foundry.outputs.accountName
output AZURE_AI_PROJECT_ID string = foundry.outputs.projectId
output AZURE_AI_PROJECT_NAME string = foundry.outputs.projectName
output AZURE_AI_PROJECT_PRINCIPAL_ID string = foundry.outputs.projectPrincipalId
output AZURE_AI_PROJECT_ENDPOINT string = foundry.outputs.projectEndpoint
output FOUNDRY_PROJECT_ENDPOINT string = foundry.outputs.projectEndpoint
output AZURE_AI_MODEL_DEPLOYMENT_NAME string = foundry.outputs.chatDeploymentName
output AZURE_AI_QUALITY_MODEL_DEPLOYMENT_NAME string = foundry.outputs.chatDeploymentName
output AZURE_AI_RAI_POLICY_NAME string = foundry.outputs.raiPolicyName
output AZURE_APP_SERVICE_PLAN_NAME string = platform.outputs.appServicePlanName
output DEFRA_TOOLS_SITE_NAME string = platform.outputs.toolsSiteName
output DEFRA_TOOLS_HOSTNAME string = platform.outputs.toolsSiteHostname
output DEFRA_TOOLS_URL string = platform.outputs.toolsSiteUrl
output DEMO2_WEB_SITE_NAME string = platform.outputs.demo2SiteName
output DEMO2_WEB_HOSTNAME string = platform.outputs.demo2SiteHostname
output DEMO2_WEB_URL string = platform.outputs.demo2SiteUrl
output DEFRA_TOOLS_MCP_FLOOD_SUPPORT_URL string = platform.outputs.toolsMcpFloodSupportUrl
output DEFRA_TOOLS_AUDIENCE string = platform.outputs.toolsAudience
output DEFRA_TOOLS_AUTH_CLIENT_ID string = platform.outputs.toolsApplicationClientId
output DEMO2_AUTH_CLIENT_ID string = platform.outputs.demo2ApplicationClientId
output AZURE_COSMOS_ACCOUNT_ID string = platform.outputs.cosmosAccountId
output AZURE_COSMOS_ACCOUNT_NAME string = platform.outputs.cosmosAccountName
output AZURE_COSMOS_ENDPOINT string = platform.outputs.cosmosEndpoint
output AZURE_COSMOS_DATABASE_NAME string = platform.outputs.cosmosDatabaseName
output AZURE_COSMOS_DEMO2_CONTAINER_NAME string = platform.outputs.demo2ContainerName
output LOG_ANALYTICS_WORKSPACE_RESOURCE_ID string = platform.outputs.logAnalyticsWorkspaceResourceId
output LOG_ANALYTICS_WORKSPACE_NAME string = platform.outputs.logAnalyticsWorkspaceName
output LOG_ANALYTICS_WORKSPACE_ID string = platform.outputs.logAnalyticsWorkspaceId
output APPLICATIONINSIGHTS_RESOURCE_ID string = platform.outputs.applicationInsightsResourceId
output APPLICATIONINSIGHTS_NAME string = platform.outputs.applicationInsightsName
output AZURE_AI_APPLICATION_INSIGHTS_CONNECTION_NAME string = platform.outputs.applicationInsightsConnectionName
output APPLICATIONINSIGHTS_CONNECTION_STRING string = platform.outputs.applicationInsightsConnectionString
output DEFRA_TOOLS_IDENTITY_RESOURCE_ID string = platform.outputs.toolsIdentityResourceId
output DEFRA_TOOLS_IDENTITY_CLIENT_ID string = platform.outputs.toolsIdentityClientId
output DEFRA_TOOLS_IDENTITY_PRINCIPAL_ID string = platform.outputs.toolsIdentityPrincipalId
output DEMO2_IDENTITY_RESOURCE_ID string = platform.outputs.demo2IdentityResourceId
output DEMO2_IDENTITY_CLIENT_ID string = platform.outputs.demo2IdentityClientId
output DEMO2_IDENTITY_PRINCIPAL_ID string = platform.outputs.demo2IdentityPrincipalId

output AZURE_AI_REASONING_EFFORT string = reasoningEffort
output DEMO2_AGENT_NAME string = 'cross-government-flood-support'
output DEFRA_TOOLS_FLOOD_SUPPORT_CONNECTION_NAME string = 'flood-support-connection'
