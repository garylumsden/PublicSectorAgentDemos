targetScope = 'resourceGroup'

@description('Azure region for Demo 1 resources.')
param location string

@minLength(1)
@maxLength(64)
@description('Environment name used for deterministic resource names.')
param environmentName string

@description('Object ID of the user who prepares and rehearses Demo 1.')
param userPrincipalId string

@description('Existing AI Services account that owns the Foundry project.')
param aiServicesName string

@description('Existing Foundry project used for the two prompt agents.')
param aiProjectName string

@description('Tags applied to Demo 1 resources.')
param tags object = {}

var suffix = toLower(uniqueString(subscription().id, resourceGroup().id, environmentName, 'demo1'))
var storageName = 'st${suffix}'
var searchName = 'srch-${suffix}'
var containerName = 'managing-public-money-v1'
var knowledgeSourceName = 'managing-public-money-govuk-v1'
var knowledgeBaseName = 'managing-public-money-kb-v1'
var connectionName = 'managing-public-money-iq'
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var storageBlobDataReaderRoleId = '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
var searchServiceContributorRoleId = '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
var searchIndexDataContributorRoleId = '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
var searchIndexDataReaderRoleId = '1407120a-92aa-4202-b7e9-c0e197c71c8f'
var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'

resource aiServices 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: aiServicesName
}

resource aiProject 'Microsoft.CognitiveServices/accounts/projects@2025-09-01' existing = {
  parent: aiServices
  name: aiProjectName
}

resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: storageName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-06-01' = {
  parent: storage
  name: 'default'
}

resource corpusContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = {
  parent: blobService
  name: containerName
  properties: {
    publicAccess: 'None'
  }
}

resource search 'Microsoft.Search/searchServices@2026-03-01-preview' = {
  name: searchName
  location: location
  tags: tags
  sku: {
    name: 'standard'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    disableLocalAuth: true
    knowledgeRetrieval: 'standard'
    partitionCount: 1
    publicNetworkAccess: 'Enabled'
    replicaCount: 1
    semanticSearch: 'free'
  }
}

resource searchConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-09-01' = {
  parent: aiProject
  name: 'demo1-search'
  properties: {
    category: 'CognitiveSearch'
    authType: 'AAD'
    target: 'https://${search.name}.search.windows.net'
    isSharedToAll: true
    metadata: {
      ApiType: 'Azure'
      ResourceId: search.id
      location: search.location
    }
  }
}

resource knowledgeConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-09-01' = {
  parent: aiProject
  name: connectionName
  properties: any({
    category: 'RemoteTool'
    authType: 'ProjectManagedIdentity'
    target: 'https://${search.name}.search.windows.net/knowledgebases/${knowledgeBaseName}/mcp?api-version=2026-05-01-preview'
    audience: 'https://search.azure.com/'
    isSharedToAll: true
    metadata: {
      ApiType: 'Azure'
    }
  })
}

resource storageUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, userPrincipalId, storageBlobDataOwnerRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwnerRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

resource storageProjectRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, aiProject.id, storageBlobDataContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      storageBlobDataContributorRoleId
    )
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource storageSearchReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(corpusContainer.id, search.id, storageBlobDataReaderRoleId)
  scope: corpusContainer
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataReaderRoleId)
    principalId: search.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource searchUserServiceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, userPrincipalId, searchServiceContributorRoleId)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchServiceContributorRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

resource searchUserDataRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, userPrincipalId, searchIndexDataContributorRoleId)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      searchIndexDataContributorRoleId
    )
    principalId: userPrincipalId
    principalType: 'User'
  }
}

resource searchProjectReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, aiProject.id, searchIndexDataReaderRoleId)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexDataReaderRoleId)
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource searchProjectServiceRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, aiProject.id, searchServiceContributorRoleId)
  scope: search
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchServiceContributorRoleId)
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource searchModelRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiServices.id, search.id, cognitiveServicesUserRoleId)
  scope: aiServices
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalId: search.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output storageAccountName string = storage.name
output storageAccountBlobHost string = '${storage.name}.blob.${environment().suffixes.storage}'
output storageAccountResourceId string = storage.id
output knowledgeContainerName string = corpusContainer.name
output knowledgeSourceName string = knowledgeSourceName
output searchEndpoint string = 'https://${search.name}.search.windows.net'
output knowledgeBaseName string = knowledgeBaseName
output knowledgeConnectionName string = knowledgeConnection.name
