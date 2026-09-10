// ============================================================================
// Agent Council – Azure AI Foundry Infrastructure (AZD-ready)
// Deploys: AI Services + models (gpt-5 memory, gpt-5.4 reasoning, gpt-5-mini fast, embeddings) + Foundry Project
//          + Cosmos DB (NoSQL, serverless) + Storage + App Insights + RBAC
// ============================================================================

targetScope = 'subscription'

// --- Parameters (AZD auto-populates these) ---------------------------------

@minLength(1)
@maxLength(64)
@description('Name of the AZD environment – used for resource naming')
param environmentName string

@description('Azure region for all resources')
param location string

@description('Object ID of the deploying user (AZD sets AZURE_PRINCIPAL_ID)')
param userPrincipalId string

@description('Reasoning model (deep debate personas) deployment + model name')
param reasoningModelName string = 'gpt-5.4'

@description('Reasoning model version')
param reasoningModelVersion string = '2026-03-05'

@description('Reasoning model capacity (thousands of tokens per minute)')
@minValue(1)
param reasoningCapacity int = 100

@description('Fast model (moderator / debate bids) deployment + model name')
param fastModelName string = 'gpt-5-mini'

@description('Fast model version')
param fastModelVersion string = '2025-08-07'

@description('Fast model capacity (thousands of tokens per minute)')
@minValue(1)
param fastCapacity int = 1000

@description('Foundry IQ knowledge base MCP RemoteTool project-connection name (also the KB name).')
param knowledgeBaseConnectionName string = 'kbgcknowledgebase'

@description('Azure AI Search SKU. Basic supports the semantic ranker, managed identity, and the Foundry IQ agentic retrieval limits required by this demo.')
param searchSkuName string = 'basic'

@description('Azure AI Search location. Defaults to the main location; override if the chosen SKU lacks capacity in-region.')
param searchServiceLocation string = location

@description('Web grounding provider attached to council agents: webiq (default) or foundryiq')
param groundingProvider string = 'webiq'

@description('Web IQ MCP connection name')
param webIqConnectionName string = 'webiq'

@description('Web IQ MCP endpoint (api.microsoft.ai). Limited access.')
param webIqMcpUrl string = 'https://api.microsoft.ai/v3/mcp'

@description('Web IQ API key (limited access). Stored in Key Vault; empty disables the Web IQ connection.')
@secure()
param webIqApiKey string = ''

// --- Content safety (RAI) policy — overridable per scenario via azd env -----
// Thresholds default to 'Medium' (= Microsoft.Default), so the template is unchanged out of the box.

@description('Name of the custom content-safety (RAI) policy bound to chat model deployments.')
param contentPolicyName string = 'agent-council-content-policy'

@allowed(['Low', 'Medium', 'High'])
@description('Hate harm-category severity threshold to block at (Medium = Microsoft.Default).')
param hateSeverityThreshold string = 'Medium'

@allowed(['Low', 'Medium', 'High'])
@description('Sexual harm-category severity threshold to block at (Medium = Microsoft.Default).')
param sexualSeverityThreshold string = 'Medium'

@allowed(['Low', 'Medium', 'High'])
@description('Violence harm-category severity threshold to block at (Medium = Microsoft.Default).')
param violenceSeverityThreshold string = 'Medium'

@allowed(['Low', 'Medium', 'High'])
@description('Self-harm harm-category severity threshold to block at (Medium = Microsoft.Default).')
param selfHarmSeverityThreshold string = 'Medium'

// --- Variables -------------------------------------------------------------

var tags = {
  'azd-env-name': environmentName
  project: 'governance-council'
  SecurityControl: 'Ignore'
  CostControl: 'Ignore'
}

var resourceGroupName = 'rg-${environmentName}'

// --- Resource Group --------------------------------------------------------

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: resourceGroupName
  location: location
  tags: tags
}

// --- Module: all resources into the resource group -------------------------

module resources 'resources.bicep' = {
  name: 'resources'
  scope: rg
  params: {
    location: location
    environmentName: environmentName
    userPrincipalId: userPrincipalId
    reasoningModelName: reasoningModelName
    reasoningModelVersion: reasoningModelVersion
    reasoningCapacity: reasoningCapacity
    fastModelName: fastModelName
    fastModelVersion: fastModelVersion
    fastCapacity: fastCapacity
    knowledgeBaseConnectionName: knowledgeBaseConnectionName
    searchSkuName: searchSkuName
    searchServiceLocation: searchServiceLocation
    groundingProvider: groundingProvider
    webIqConnectionName: webIqConnectionName
    webIqMcpUrl: webIqMcpUrl
    webIqApiKey: webIqApiKey
    contentPolicyName: contentPolicyName
    hateSeverityThreshold: hateSeverityThreshold
    sexualSeverityThreshold: sexualSeverityThreshold
    violenceSeverityThreshold: violenceSeverityThreshold
    selfHarmSeverityThreshold: selfHarmSeverityThreshold
    tags: tags
  }
}

// --- AZD Outputs (written to .env automatically) ---------------------------

output AZURE_AI_FOUNDRY_ENDPOINT string = resources.outputs.aiFoundryProjectEndpoint
output AZURE_AI_SERVICES_ENDPOINT string = resources.outputs.aiServicesEndpoint
output AZURE_OPENAI_ENDPOINT string = resources.outputs.aiServicesOpenAIEndpoint
output AZURE_AI_PROJECT_NAME string = resources.outputs.aiProjectName
output AZURE_RESOURCE_GROUP string = rg.name
output AI_SERVICES_RESOURCE_NAME string = resources.outputs.aiServicesResourceName
output EMBEDDING_DEPLOYMENT_NAME string = resources.outputs.embeddingDeploymentName
// Fast mode uses gpt-5-mini for debate and synthesis, with gpt-5-nano for routing and bids.
output COUNCIL_REASONING_MODEL string = resources.outputs.fastDeploymentName
output COUNCIL_SYNTHESIS_MODEL string = resources.outputs.fastDeploymentName
output COUNCIL_FAST_MODEL string = resources.outputs.nanoDeploymentName
output COUNCIL_EMBEDDING_MODEL string = resources.outputs.embeddingDeploymentName
output KNOWLEDGE_BASE_CONNECTION_NAME string = resources.outputs.knowledgeBaseConnectionName
output WEBIQ_CONNECTION_NAME string = resources.outputs.webIqConnectionName
output WEBIQ_MCP_URL string = resources.outputs.webIqMcpUrl
output COUNCIL_GROUNDING_PROVIDER string = resources.outputs.groundingProvider
output COSMOS_DB_ENDPOINT string = resources.outputs.cosmosDbEndpoint
output COSMOS_DB_DATABASE_NAME string = resources.outputs.cosmosDbDatabaseName
output STORAGE_ACCOUNT_NAME string = resources.outputs.storageAccountName
output SEARCH_SERVICE_ENDPOINT string = resources.outputs.searchServiceEndpoint
output SEARCH_SERVICE_NAME string = resources.outputs.searchServiceName
output APPINSIGHTS_CONNECTION_STRING string = resources.outputs.appInsightsConnectionString
output RAI_POLICY_NAME string = resources.outputs.raiPolicyName
