targetScope = 'resourceGroup'

type modelDeploymentConfig = {
  name: string
  modelName: string
  modelVersion: string
  skuName: string
  capacity: int
}

type stringMap = {
  *: string
}

@minLength(1)
@maxLength(64)
param environmentName string

@minLength(1)
param location string

@minLength(1)
param accountSkuName string

param chatDeployment modelDeploymentConfig
param tags stringMap

var resourceToken = uniqueString(subscription().subscriptionId, resourceGroup().id, environmentName)
var environmentNameSegment = take(toLower(environmentName), 30)
var accountName = 'aif-${resourceToken}-${environmentNameSegment}'
var projectName = 'project-${environmentNameSegment}-${resourceToken}'
var projectEndpoint = 'https://${accountName}.services.ai.azure.com/api/projects/${projectName}'

resource foundryAccount 'Microsoft.CognitiveServices/accounts@2026-05-01' = {
  name: accountName
  location: location
  kind: 'AIServices'
  identity: {
    type: 'SystemAssigned'
  }
  sku: {
    name: accountSkuName
  }
  properties: {
    allowProjectManagement: true
    customSubDomainName: accountName
    disableLocalAuth: true
    dynamicThrottlingEnabled: true
    publicNetworkAccess: 'Enabled'
    restrictOutboundNetworkAccess: false
  }
  tags: tags
}

resource foundryProject 'Microsoft.CognitiveServices/accounts/projects@2026-05-01' = {
  parent: foundryAccount
  name: projectName
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'Cross-Government Flood Support'
    description: 'Microsoft Foundry project for the approval-controlled flood support demonstration.'
  }
  tags: tags
}

resource blockingRaiPolicy 'Microsoft.CognitiveServices/accounts/raiPolicies@2026-05-01' = {
  parent: foundryAccount
  name: 'defra-blocking'
  dependsOn: [
    foundryProject
  ]
  properties: {
    basePolicyName: 'Microsoft.DefaultV2'
    mode: 'Blocking'
    contentFilters: [
      {
        name: 'Hate'
        source: 'Prompt'
        severityThreshold: 'Medium'
        blocking: true
        enabled: true
      }
      {
        name: 'Hate'
        source: 'Completion'
        severityThreshold: 'Medium'
        blocking: true
        enabled: true
      }
      {
        name: 'Sexual'
        source: 'Prompt'
        severityThreshold: 'Medium'
        blocking: true
        enabled: true
      }
      {
        name: 'Sexual'
        source: 'Completion'
        severityThreshold: 'Medium'
        blocking: true
        enabled: true
      }
      {
        name: 'Violence'
        source: 'Prompt'
        severityThreshold: 'Medium'
        blocking: true
        enabled: true
      }
      {
        name: 'Violence'
        source: 'Completion'
        severityThreshold: 'Medium'
        blocking: true
        enabled: true
      }
      {
        name: 'Selfharm'
        source: 'Prompt'
        severityThreshold: 'Medium'
        blocking: true
        enabled: true
      }
      {
        name: 'Selfharm'
        source: 'Completion'
        severityThreshold: 'Medium'
        blocking: true
        enabled: true
      }
      {
        name: 'Jailbreak'
        source: 'Prompt'
        blocking: true
        enabled: true
      }
    ]
  }
  tags: tags
}

resource chatModelDeployment 'Microsoft.CognitiveServices/accounts/deployments@2026-05-01' = {
  parent: foundryAccount
  name: chatDeployment.name
  sku: {
    name: chatDeployment.skuName
    capacity: chatDeployment.capacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: chatDeployment.modelName
      version: chatDeployment.modelVersion
    }
    raiPolicyName: blockingRaiPolicy.name
    versionUpgradeOption: 'NoAutoUpgrade'
  }
}

output accountId string = foundryAccount.id
output accountName string = foundryAccount.name
output projectId string = foundryProject.id
output projectName string = foundryProject.name
output projectPrincipalId string = foundryProject.identity.principalId
output projectEndpoint string = projectEndpoint
output chatDeploymentName string = chatModelDeployment.name
output raiPolicyId string = blockingRaiPolicy.id
output raiPolicyName string = blockingRaiPolicy.name
