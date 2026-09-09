// ============================================================================
// Model Deployment module — deploys a single model to an AI Services account
// Called sequentially from resources.bicep to avoid ETag race conditions
// ============================================================================

@description('Name of the AI Services account')
param aiServicesName string

@description('Deployment name (e.g. gpt-5)')
param deploymentName string

@description('Model name (e.g. gpt-5)')
param modelName string

@description('Model format')
param modelFormat string = 'OpenAI'

@description('Model version (leave empty for latest)')
param modelVersion string = ''

@description('SKU name')
param skuName string = 'GlobalStandard'

@description('SKU capacity')
param skuCapacity int

@description('Custom RAI (content-safety) policy to bind to this deployment. Empty ⇒ account default.')
param raiPolicyName string = ''

resource aiServices 'Microsoft.CognitiveServices/accounts@2025-09-01' existing = {
  name: aiServicesName
}

resource deployment 'Microsoft.CognitiveServices/accounts/deployments@2025-09-01' = {
  parent: aiServices
  name: deploymentName
  sku: {
    name: skuName
    capacity: skuCapacity
  }
  properties: {
    model: {
      format: modelFormat
      name: modelName
      version: !empty(modelVersion) ? modelVersion : null
    }
    raiPolicyName: !empty(raiPolicyName) ? raiPolicyName : null
  }
}

output deploymentName string = deployment.name
