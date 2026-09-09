targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('Azure Developer CLI environment name.')
param environmentName string

@description('Azure region for the isolated Demo 1 resources.')
param location string

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

var tags = {
  'azd-env-name': environmentName
  project: 'managing-public-money-demo1'
  SecurityControl: 'Ignore'
  CostControl: 'Ignore'
}
var resourceGroupName = 'rg-${environmentName}'

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

module demo1 'demo1-standalone-resources.bicep' = {
  name: 'demo1-standalone'
  scope: resourceGroup
  params: {
    location: location
    environmentName: environmentName
    userPrincipalId: userPrincipalId
    modelName: modelName
    modelVersion: modelVersion
    modelCapacity: modelCapacity
    embeddingModelName: embeddingModelName
    embeddingModelVersion: embeddingModelVersion
    embeddingModelCapacity: embeddingModelCapacity
    tags: tags
  }
}

output AZURE_RESOURCE_GROUP string = resourceGroup.name
output AZURE_AI_FOUNDRY_ENDPOINT string = demo1.outputs.aiFoundryProjectEndpoint
output AZURE_AI_SERVICES_ENDPOINT string = demo1.outputs.aiServicesEndpoint
output AZURE_AI_PROJECT_NAME string = demo1.outputs.aiProjectName
output AZURE_AI_PROJECT_ID string = demo1.outputs.aiProjectId
output AI_SERVICES_RESOURCE_NAME string = demo1.outputs.aiServicesResourceName
output DEMO1_MODEL_DEPLOYMENT string = demo1.outputs.modelDeploymentName
output COUNCIL_FAST_MODEL string = demo1.outputs.modelDeploymentName
output EMBEDDING_DEPLOYMENT_NAME string = demo1.outputs.embeddingDeploymentName
output DEMO1_SEARCH_ENDPOINT string = demo1.outputs.searchEndpoint
output DEMO1_STORAGE_ACCOUNT_NAME string = demo1.outputs.storageAccountName
output DEMO1_STORAGE_ACCOUNT_BLOB_HOST string = demo1.outputs.storageAccountBlobHost
output DEMO1_STORAGE_ACCOUNT_RESOURCE_ID string = demo1.outputs.storageAccountResourceId
output DEMO1_KNOWLEDGE_CONTAINER_NAME string = demo1.outputs.knowledgeContainerName
output DEMO1_KNOWLEDGE_SOURCE_NAME string = demo1.outputs.knowledgeSourceName
output DEMO1_KNOWLEDGE_BASE_NAME string = demo1.outputs.knowledgeBaseName
output DEMO1_KNOWLEDGE_CONNECTION_NAME string = demo1.outputs.knowledgeConnectionName
output APPLICATIONINSIGHTS_RESOURCE_ID string = demo1.outputs.applicationInsightsResourceId
output APPLICATIONINSIGHTS_NAME string = demo1.outputs.applicationInsightsName
output APPLICATIONINSIGHTS_PROJECT_CONNECTION_NAME string = demo1.outputs.applicationInsightsConnectionName
