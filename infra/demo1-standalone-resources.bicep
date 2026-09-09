targetScope = 'resourceGroup'

@description('Azure region for the isolated Demo 1 resources.')
param location string

@minLength(1)
@maxLength(64)
@description('Environment name used for deterministic resource names.')
param environmentName string

@description('Object ID of the user who provisions and rehearses Demo 1.')
param userPrincipalId string

@description('Chat model name and deployment name.')
param modelName string = 'gpt-5-mini'

@description('Chat model version.')
param modelVersion string = '2025-08-07'

@minValue(1)
@description('Chat model capacity in thousands of tokens per minute.')
param modelCapacity int = 100

@description('Embedding model name and deployment name.')
param embeddingModelName string = 'text-embedding-3-small'

@description('Embedding model version.')
param embeddingModelVersion string = '1'

@minValue(1)
@description('Embedding model capacity in thousands of tokens per minute.')
param embeddingModelCapacity int = 30

@description('Tags applied to isolated Demo 1 resources.')
param tags object = {}

var suffix = toLower(uniqueString(resourceGroup().id, environmentName, 'demo1-standalone'))
var aiServicesName = 'ais-demo1-${suffix}'
var aiProjectName = 'demo1-${suffix}'
var applicationInsightsConnectionName = 'appinsights'
var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'
var azureAIUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d'
var azureAIProjectManagerRoleId = 'b8b15564-4fa6-4a59-ab12-03e1d9594795'
var monitoringMetricsPublisherRoleId = '3913510d-42f4-4e42-8a64-420c390055eb'

resource aiServices 'Microsoft.CognitiveServices/accounts@2025-09-01' = {
  name: aiServicesName
  location: location
  tags: tags
  kind: 'AIServices'
  sku: {
    name: 'S0'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    customSubDomainName: aiServicesName
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: true
    allowProjectManagement: true
  }
}

module embeddingDeployment 'demo1-model-deployment.bicep' = {
  name: 'demo1-deploy-embedding'
  params: {
    aiServicesName: aiServices.name
    deploymentName: embeddingModelName
    modelName: embeddingModelName
    modelVersion: embeddingModelVersion
    skuCapacity: embeddingModelCapacity
  }
}

module modelDeployment 'demo1-model-deployment.bicep' = {
  name: 'demo1-deploy-model'
  dependsOn: [
    embeddingDeployment
  ]
  params: {
    aiServicesName: aiServices.name
    deploymentName: modelName
    modelName: modelName
    modelVersion: modelVersion
    skuCapacity: modelCapacity
  }
}

resource aiProject 'Microsoft.CognitiveServices/accounts/projects@2025-09-01' = {
  parent: aiServices
  name: aiProjectName
  dependsOn: [
    modelDeployment
  ]
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'Managing Public Money Demo 1'
    description: 'Isolated Foundation and Ground comparison project'
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-demo1-${suffix}'
  location: location
  tags: tags
  properties: {
    retentionInDays: 30
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-demo1-${suffix}'
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    DisableLocalAuth: true
    WorkspaceResourceId: logAnalytics.id
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

resource applicationInsightsConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-09-01' = {
  parent: aiProject
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
  name: guid(applicationInsights.id, aiProject.id, monitoringMetricsPublisherRoleId)
  scope: applicationInsights
  properties: {
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      monitoringMetricsPublisherRoleId)
  }
}

resource presenterTelemetryPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(applicationInsights.id, userPrincipalId, monitoringMetricsPublisherRoleId)
  scope: applicationInsights
  properties: {
    principalId: userPrincipalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      monitoringMetricsPublisherRoleId)
  }
}

resource aiServicesUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiServices.id, userPrincipalId, cognitiveServicesUserRoleId)
  scope: aiServices
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

resource aiProjectUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiProject.id, userPrincipalId, azureAIUserRoleId)
  scope: aiProject
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAIUserRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

resource aiProjectManagerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiProject.id, userPrincipalId, azureAIProjectManagerRoleId)
  scope: aiProject
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAIProjectManagerRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

resource projectModelRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiServices.id, aiProject.id, cognitiveServicesUserRoleId)
  scope: aiServices
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource projectSelfRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiProject.id, 'project-mi', azureAIUserRoleId)
  scope: aiProject
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAIUserRoleId)
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

module foundationAndGround 'demo1-foundation-ground.bicep' = {
  name: 'demo1-foundation-ground'
  dependsOn: [
    embeddingDeployment
    modelDeployment
  ]
  params: {
    location: location
    environmentName: environmentName
    userPrincipalId: userPrincipalId
    aiServicesName: aiServices.name
    aiProjectName: aiProject.name
    tags: tags
  }
}

output aiFoundryProjectEndpoint string = 'https://${aiServices.name}.services.ai.azure.com/api/projects/${aiProject.name}'
output aiServicesEndpoint string = 'https://${aiServices.name}.services.ai.azure.com/'
output aiProjectName string = aiProject.name
output aiProjectId string = aiProject.id
output aiServicesResourceName string = aiServices.name
output modelDeploymentName string = modelDeployment.outputs.deploymentName
output embeddingDeploymentName string = embeddingDeployment.outputs.deploymentName
output searchEndpoint string = foundationAndGround.outputs.searchEndpoint
output storageAccountName string = foundationAndGround.outputs.storageAccountName
output storageAccountBlobHost string = foundationAndGround.outputs.storageAccountBlobHost
output storageAccountResourceId string = foundationAndGround.outputs.storageAccountResourceId
output knowledgeContainerName string = foundationAndGround.outputs.knowledgeContainerName
output knowledgeSourceName string = foundationAndGround.outputs.knowledgeSourceName
output knowledgeBaseName string = foundationAndGround.outputs.knowledgeBaseName
output knowledgeConnectionName string = foundationAndGround.outputs.knowledgeConnectionName
output applicationInsightsResourceId string = applicationInsights.id
output applicationInsightsName string = applicationInsights.name
output applicationInsightsConnectionName string = applicationInsightsConnection.name
