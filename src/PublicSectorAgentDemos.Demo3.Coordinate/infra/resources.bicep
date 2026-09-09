// ============================================================================
// Agent Council – Resource module (resource-group scope)
// Foundry resource + Foundry project + gpt-5 (memory) + reasoning + fast + Embeddings
// + Cosmos DB (NoSQL, serverless) + Storage + App Insights + RBAC
// Uses Microsoft.CognitiveServices/accounts/projects (no Hub required)
// ============================================================================

// --- Parameters ------------------------------------------------------------

@description('Azure region for all resources')
param location string

@description('Name of the AZD environment – used for resource naming')
param environmentName string

@description('Object ID of the deploying user')
param userPrincipalId string

@description('Reasoning model (deep debate personas) deployment + model name, e.g. gpt-5.4')
param reasoningModelName string = 'gpt-5.4'

@description('Reasoning model version')
param reasoningModelVersion string = '2026-03-05'

@description('Reasoning model capacity (thousands of tokens per minute)')
param reasoningCapacity int = 100

@description('Fast model (moderator / debate bids) deployment + model name, e.g. gpt-5-mini')
param fastModelName string = 'gpt-5-mini'

@description('Fast model version')
param fastModelVersion string = '2025-08-07'

@description('Fast model capacity (thousands of tokens per minute)')
param fastCapacity int = 1000

@description('Foundry IQ knowledge base MCP RemoteTool project-connection name (also the KB name).')
param knowledgeBaseConnectionName string = 'kbgcknowledgebase'

@description('Azure AI Search SKU. basic capacity is often exhausted per-region; standard is the safe default and supports the semantic ranker that Foundry IQ agentic retrieval needs.')
param searchSkuName string = 'standard'

@description('Azure AI Search location. Defaults to the main location; override if the chosen SKU lacks capacity in-region.')
param searchServiceLocation string = location

@description('Resource tags applied to all resources')
param tags object

@description('Web grounding provider attached to council agents: webiq (default) or foundryiq')
param groundingProvider string = 'webiq'

@description('Web IQ MCP connection name surfaced to agents via WEBIQ_CONNECTION_NAME')
param webIqConnectionName string = 'webiq'

@description('Web IQ MCP endpoint (api.microsoft.ai). Tool: web. Limited-access — supply when onboarded.')
param webIqMcpUrl string = 'https://api.microsoft.ai/v3/mcp'

@description('Web IQ API key (limited access). Stored in Key Vault; empty disables the Web IQ connection.')
@secure()
param webIqApiKey string = ''

// --- Content safety (RAI) policy -------------------------------------------
// A custom content-filter policy bound to every chat model deployment. Thresholds default to
// 'Medium' = Microsoft.Default behaviour, so the template is unchanged out of the box. A derived
// scenario relaxes a category via `azd env set` only (e.g. COUNCIL_CONTENT_VIOLENCE_THRESHOLD=High).
// NOTE: thresholds LESS restrictive than Microsoft.Default require the subscription to be approved
// for modified content filters (Azure OpenAI Limited Access), else the raiPolicies deploy is rejected.

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

var uniqueSuffix = uniqueString(resourceGroup().id, environmentName)
var aiServicesName = '${environmentName}-ais-${take(uniqueSuffix, 6)}'
var aiProjectName = '${environmentName}-project'
var embeddingDeploymentName = 'text-embedding-3-small'
var cosmosDbAccountName = '${environmentName}-cosmos-${take(uniqueSuffix, 6)}'
var cosmosDbDatabaseName = 'governance-council'
var storageAccountName = replace('${take(environmentName, 10)}st${take(uniqueSuffix, 8)}', '-', '')
var appInsightsName = '${environmentName}-appi-${take(uniqueSuffix, 6)}'
var logAnalyticsName = '${environmentName}-log-${take(uniqueSuffix, 6)}'
var searchServiceName = '${environmentName}-srch-${take(uniqueSuffix, 6)}'
var keyVaultName = 'kv${take(uniqueSuffix, 16)}'
var hasWebIqKey = !empty(webIqApiKey)

// Built-in role definition IDs
var cognitiveServicesUserRoleId = 'a97b65f3-24c7-4388-baec-2e87135dc908'
var azureAIUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d'
var azureAIProjectManagerRoleId = 'b8b15564-4fa6-4a59-ab12-03e1d9594795'
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var searchServiceContributorRoleId = '7ca78c08-252a-4471-8644-bb5ff32d4ba0'
var searchIndexDataContributorRoleId = '8ebe5a00-799e-43f5-93ac-243d3dce84a7'
var searchIndexDataReaderRoleId = '1407120a-92aa-4202-b7e9-c0e197c71c8f'
var logAnalyticsContributorRoleId = '92aaf0da-9dab-42b6-94a3-d43ce8d16293'
var monitoringContributorRoleId = '749f88d5-cbae-40b8-bcfc-e573ddc772fa'
var cognitiveServicesOpenAIUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
var storageBlobDataOwnerRoleId = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'
// Cognitive Services Contributor — includes raiPolicies read/write, needed by the runtime so a
// presenter can toggle the content-filter strictness per deliberation (control-plane policy update).
var cognitiveServicesContributorRoleId = '25fbc0a9-bd7c-42a3-aa1a-3b75d497ee68'

// Cosmos DB built-in data contributor role (scope-level RBAC)
var cosmosDbDataContributorRoleId = '00000000-0000-0000-0000-000000000002'

// ============================================================================
// AI Services + Model Deployments + Foundry Project
// ============================================================================

// --- Foundry resource (AI Services account) --------------------------------

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

// --- Custom content-safety (RAI) policy ------------------------------------
// Bound to every chat deployment below. Defaults (all 'Medium') match Microsoft.Default, so the
// template ships unchanged; scenarios relax a category via azd env (see main.parameters.json).

resource contentPolicy 'Microsoft.CognitiveServices/accounts/raiPolicies@2025-09-01' = {
  parent: aiServices
  name: contentPolicyName
  properties: {
    basePolicyName: 'Microsoft.Default'
    mode: 'Default'
    contentFilters: [
      { name: 'Hate', source: 'Prompt', severityThreshold: hateSeverityThreshold, blocking: true, enabled: true }
      { name: 'Hate', source: 'Completion', severityThreshold: hateSeverityThreshold, blocking: true, enabled: true }
      { name: 'Sexual', source: 'Prompt', severityThreshold: sexualSeverityThreshold, blocking: true, enabled: true }
      { name: 'Sexual', source: 'Completion', severityThreshold: sexualSeverityThreshold, blocking: true, enabled: true }
      { name: 'Violence', source: 'Prompt', severityThreshold: violenceSeverityThreshold, blocking: true, enabled: true }
      { name: 'Violence', source: 'Completion', severityThreshold: violenceSeverityThreshold, blocking: true, enabled: true }
      { name: 'Selfharm', source: 'Prompt', severityThreshold: selfHarmSeverityThreshold, blocking: true, enabled: true }
      { name: 'Selfharm', source: 'Completion', severityThreshold: selfHarmSeverityThreshold, blocking: true, enabled: true }
    ]
  }
}

// --- Model deployments (sequential modules to avoid ETag race conditions) ----

module embeddingDeployment 'model-deployment.bicep' = {
  name: 'deploy-embedding'
  dependsOn: [contentPolicy]
  params: {
    aiServicesName: aiServices.name
    deploymentName: embeddingDeploymentName
    modelName: 'text-embedding-3-small'
    modelVersion: '1'
    skuCapacity: 30
  }
}

// Per-task model tiers (see CouncilModels): reasoning model for the debating personas,
// fast model for the moderator + debate bids. Chained dependsOn keeps deployments serial so
// they don't race ETags or burst past per-account quota. Names are parameterised — switch the
// defaults (gpt-5 / gpt-5-mini) to whatever your region/quota supports.
module reasoningDeployment 'model-deployment.bicep' = {
  name: 'deploy-reasoning'
  dependsOn: [embeddingDeployment]
  params: {
    aiServicesName: aiServices.name
    deploymentName: reasoningModelName
    modelName: reasoningModelName
    modelVersion: reasoningModelVersion
    skuCapacity: reasoningCapacity
    raiPolicyName: contentPolicy.name
  }
}

module fastDeployment 'model-deployment.bicep' = {
  name: 'deploy-fast'
  dependsOn: [reasoningDeployment]
  params: {
    aiServicesName: aiServices.name
    deploymentName: fastModelName
    modelName: fastModelName
    modelVersion: fastModelVersion
    skuCapacity: fastCapacity
    raiPolicyName: contentPolicy.name
  }
}

module nanoDeployment 'model-deployment.bicep' = {
  name: 'deploy-nano'
  dependsOn: [fastDeployment]
  params: {
    aiServicesName: aiServices.name
    deploymentName: 'gpt-5-nano'
    modelName: 'gpt-5-nano'
    modelFormat: 'OpenAI'
    modelVersion: '2025-08-07'
    skuCapacity: fastCapacity
    raiPolicyName: contentPolicy.name
  }
}

// --- Foundry project (child of AI Services account) ------------------------

resource aiProject 'Microsoft.CognitiveServices/accounts/projects@2025-09-01' = {
  parent: aiServices
  name: aiProjectName
  dependsOn: [nanoDeployment]
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'Agent Council'
    description: 'Foundry project for the Agent Council — a configurable multi-agent deliberation template'
  }
}

// ============================================================================
// Foundry IQ Knowledge Base — RemoteTool project connection (MCP)
// ============================================================================
// Points the Foundry project at the Knowledge Base MCP endpoint on Azure AI Search. The connection
// is just a pointer + auth (ProjectManagedIdentity) — it can be created before the KB exists; the
// KB itself is self-provisioned by the app at first boot (KnowledgeBaseManager). The app reads this
// connection name via KNOWLEDGE_BASE_CONNECTION_NAME to attach the knowledge-base MCP tool to agents.
// The Project MI already has Search Index Data Reader (searchDataReaderProjectRole below).

resource kbMcpConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-09-01' = {
  parent: aiProject
  name: knowledgeBaseConnectionName
  properties: {
    category: 'RemoteTool'
    authType: 'ProjectManagedIdentity'
    target: 'https://${searchService.name}.search.windows.net/knowledgebases/${knowledgeBaseConnectionName}/mcp?api-version=2026-05-01-preview'
    // audience MUST be a top-level connection property — the agent runtime reads it to fetch the
    // MCP access token. Nesting it under metadata leaves properties.audience null and the agent
    // fails every knowledge_base tool call with HTTP 400 "Missing required query parameter 'audience'".
    audience: 'https://search.azure.com/'
    isSharedToAll: true
    metadata: {
      ApiType: 'Azure'
    }
  }
}



// ============================================================================
// Key Vault — holds the Web IQ API key (identity-based read by the project MI)
// ============================================================================
resource keyVault 'Microsoft.KeyVault/vaults@2024-11-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
  }
}

resource webIqSecret 'Microsoft.KeyVault/vaults/secrets@2024-11-01' = if (hasWebIqKey) {
  parent: keyVault
  name: 'webiq-api-key'
  properties: {
    value: webIqApiKey
  }
}

@description('Key Vault Secrets User — project MI reads the Web IQ key (keyless app config)')
resource kvSecretsProjectMIRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, aiProject.id, keyVaultSecretsUserRoleId)
  scope: keyVault
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Web IQ — RemoteTool project connection (MCP). Created only when a key is supplied. Web IQ expects
// the key in an `x-apikey` header → CustomKeys. The same key is stored in Key Vault (project MI reads
// it). Alternative: bind the project MI app/client ID in the Web IQ portal and switch to AAD with
// audience https://api.microsoft.ai/.default (keyless) — see README.
resource webIqConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-09-01' = if (hasWebIqKey) {
  parent: aiProject
  name: webIqConnectionName
  properties: {
    category: 'RemoteTool'
    authType: 'CustomKeys'
    target: webIqMcpUrl
    isSharedToAll: true
    credentials: {
      keys: {
        'x-apikey': webIqApiKey
      }
    }
    metadata: {
      ApiType: 'Azure'
    }
  }
}
// Azure AI Search resource connection (CognitiveSearch) — surfaces the Foundry IQ knowledge base in
// the portal's Knowledge (Foundry IQ) section. Distinct from kbMcpConnection (the agent runtime KB
// tool): this connects the Search RESOURCE so the portal lists the KBs on it. Uses the admin key.
resource aiSearchConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-09-01' = {
  parent: aiProject
  name: 'aisearch'
  properties: {
    category: 'CognitiveSearch'
    // Identity-based: the project managed identity authenticates to Search (it has Search Service
    // Contributor + Search Index Data Reader below). No admin key — keyless per the demo's zero-keys
    // stance.
    authType: 'AAD'
    target: 'https://${searchService.name}.search.windows.net'
    isSharedToAll: true
    metadata: {
      ApiType: 'Azure'
      ResourceId: searchService.id
      location: searchService.location
    }
  }
}

// Application Insights connection — enables Foundry agent tracing/observability in the project.
// Once App Insights is connected to the project, Foundry logs server-side agent traces
// automatically (no code changes or diagnostic settings needed). The portal "Traces" view needs
// this PROJECT-level connection.
resource appInsightsConnection 'Microsoft.CognitiveServices/accounts/projects/connections@2025-09-01' = {
  parent: aiProject
  name: 'appinsights'
  properties: {
    category: 'AppInsights'
    authType: 'ApiKey'
    target: appInsights.id
    isSharedToAll: true
    credentials: {
      key: appInsights.properties.ConnectionString
    }
    metadata: {
      ApiType: 'Azure'
      ResourceId: appInsights.id
    }
  }
}

resource cosmosDbAccount 'Microsoft.DocumentDB/databaseAccounts@2025-04-15' = {
  name: cosmosDbAccountName
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    databaseAccountOfferType: 'Standard'
    disableLocalAuth: true
    capacity: {
      totalThroughputLimit: 400
    }
    capabilities: [
      {
        name: 'EnableServerless'
      }
      {
        name: 'EnableNoSQLVectorSearch'
      }
    ]
    locations: [
      {
        locationName: location
        failoverPriority: 0
      }
    ]
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
  }
}

resource cosmosDbDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2025-04-15' = {
  parent: cosmosDbAccount
  name: cosmosDbDatabaseName
  properties: {
    resource: {
      id: cosmosDbDatabaseName
    }
  }
}

resource containerAssessments 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2025-04-15' = {
  parent: cosmosDbDatabase
  name: 'assessments'
  properties: {
    resource: {
      id: 'assessments'
      partitionKey: {
        paths: ['/assessmentId']
        kind: 'Hash'
      }
      // Vector index for nexus Top-K retrieval (Cosmos NoSQL vector search) over the persisted
      // text-embedding-3-small (1536-d) assessment embedding. DiskANN requires the vector path to be
      // excluded from the regular index.
      vectorEmbeddingPolicy: {
        vectorEmbeddings: [
          {
            path: '/embedding'
            dataType: 'float32'
            distanceFunction: 'cosine'
            dimensions: 1536
          }
        ]
      }
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [
          {
            path: '/*'
          }
        ]
        excludedPaths: [
          {
            path: '/embedding/*'
          }
          {
            path: '/_etag/?'
          }
        ]
        vectorIndexes: [
          {
            path: '/embedding'
            type: 'diskANN'
          }
        ]
      }
    }
  }
}

resource containerNexuses 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2025-04-15' = {
  parent: cosmosDbDatabase
  name: 'nexuses'
  properties: {
    resource: {
      id: 'nexuses'
      partitionKey: {
        paths: ['/sourceAssessmentId']
        kind: 'Hash'
      }
    }
  }
}

resource containerDossiers 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2025-04-15' = {
  parent: cosmosDbDatabase
  name: 'dossiers'
  properties: {
    resource: {
      id: 'dossiers'
      partitionKey: {
        paths: ['/dossierId']
        kind: 'Hash'
      }
    }
  }
}

resource containerDeliberations 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2025-04-15' = {
  parent: cosmosDbDatabase
  name: 'deliberations'
  properties: {
    resource: {
      id: 'deliberations'
      partitionKey: {
        paths: ['/deliberationId']
        kind: 'Hash'
      }
    }
  }
}

// ============================================================================
// Storage Account (identity-based auth, no shared keys)
// ============================================================================

resource storageAccount 'Microsoft.Storage/storageAccounts@2025-06-01' = {
  name: storageAccountName
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    allowSharedKeyAccess: false
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2025-06-01' = {
  parent: storageAccount
  name: 'default'
}

resource blobContainerOriginal 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = {
  parent: blobService
  name: 'dossiers-original'
  properties: {
    publicAccess: 'None'
  }
}

resource blobContainerMarkdown 'Microsoft.Storage/storageAccounts/blobServices/containers@2025-06-01' = {
  parent: blobService
  name: 'dossiers-markdown'
  properties: {
    publicAccess: 'None'
  }
}

// ============================================================================
// Monitoring (Log Analytics + Application Insights)
// ============================================================================

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// The Foundry project's managed identity needs Reader on Application Insights so the Foundry
// portal's tracing view can read this project's traces (Reader role id acdd72a7-...).
@description('Reader – Foundry project MI reads Application Insights traces (Foundry portal tracing).')
resource appInsightsReaderRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsights.id, aiProject.id, 'acdd72a7-3385-48ef-bd42-f606fba81ae7')
  scope: appInsights
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      'acdd72a7-3385-48ef-bd42-f606fba81ae7'
    )
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ============================================================================
// RBAC Role Assignments
// ============================================================================

// --- User → AI Services ----------------------------------------------------

@description('Cognitive Services User – user calls AI Services APIs')
resource cogServicesUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiServices.id, userPrincipalId, cognitiveServicesUserRoleId)
  scope: aiServices
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      cognitiveServicesUserRoleId
    )
    principalId: userPrincipalId
    principalType: 'User'
  }
}

// Cognitive Services Contributor (control plane) — grants raiPolicies read/write so the locally-run
// app (identity = the deploying user) can update the account's custom RAI policy at runtime for the
// per-deliberation content-filter toggle. The deploying user is usually subscription Owner already, but
// this makes the capability explicit and survives a least-privileged user or a future hosted MI.
// For production prefer a least-privilege CUSTOM role limited to
// Microsoft.CognitiveServices/accounts/raiPolicies/read + .../raiPolicies/write instead of this built-in.
resource userRaiPolicyContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiServices.id, userPrincipalId, cognitiveServicesContributorRoleId)
  scope: aiServices
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      cognitiveServicesContributorRoleId
    )
    principalId: userPrincipalId
    principalType: 'User'
  }
}

// --- User → Foundry project ------------------------------------------------

@description('Azure AI User – data plane: user accesses Foundry project agents, conversations, files')
resource aiProjectUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiProject.id, userPrincipalId, azureAIUserRoleId)
  scope: aiProject
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      azureAIUserRoleId
    )
    principalId: userPrincipalId
    principalType: 'User'
  }
}

@description('Azure AI Project Manager – data plane: user manages project, admin/operate page, can assign Azure AI User to others')
resource aiProjectManagerRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiProject.id, userPrincipalId, azureAIProjectManagerRoleId)
  scope: aiProject
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      azureAIProjectManagerRoleId
    )
    principalId: userPrincipalId
    principalType: 'User'
  }
}

// --- Project MI → itself ---------------------------------------------------

@description('Azure AI User – project MI can access Foundry project')
resource aiProjectMIRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiProject.id, 'project-mi', azureAIUserRoleId)
  scope: aiProject
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      azureAIUserRoleId
    )
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- User → Cosmos DB (scope-level RBAC) -----------------------------------

@description('Cosmos DB Built-in Data Contributor – user reads/writes Cosmos DB data')
resource cosmosDbUserRoleAssignment 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2025-04-15' = {
  parent: cosmosDbAccount
  name: guid(cosmosDbAccount.id, userPrincipalId, cosmosDbDataContributorRoleId)
  properties: {
    roleDefinitionId: '${cosmosDbAccount.id}/sqlRoleDefinitions/${cosmosDbDataContributorRoleId}'
    principalId: userPrincipalId
    scope: cosmosDbAccount.id
  }
}

// --- User → Storage --------------------------------------------------------

@description('Storage Blob Data Owner – user has full data plane admin on blob storage')
resource storageUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, userPrincipalId, storageBlobDataOwnerRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      storageBlobDataOwnerRoleId
    )
    principalId: userPrincipalId
    principalType: 'User'
  }
}

// --- Project MI → Storage --------------------------------------------------

@description('Storage Blob Data Contributor – project MI reads/writes blob data')
resource storageProjectMIRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, aiProject.id, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      storageBlobDataContributorRoleId
    )
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- Project MI → AI Services (invoke models for server-side workflows) ------

@description('Cognitive Services User – project MI invokes models when running Foundry workflows')
resource cogServicesProjectMIRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiServices.id, aiProject.id, cognitiveServicesUserRoleId)
  scope: aiServices
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- AI Services MI → Storage (Foundry file storage) -------------------------

@description('Storage Blob Data Contributor – AI Services MI reads/writes Foundry-managed files')
resource storageAiServicesMIRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, aiServices.id, storageBlobDataContributorRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: aiServices.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ============================================================================
// Azure AI Search (required for Foundry IQ knowledge base)
// ============================================================================

resource searchService 'Microsoft.Search/searchServices@2025-05-01' = {
  name: searchServiceName
  location: searchServiceLocation
  tags: tags
  sku: {
    name: searchSkuName
  }
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    replicaCount: 1
    partitionCount: 1
    semanticSearch: 'free'
    publicNetworkAccess: 'enabled'
    authOptions: {
      aadOrApiKey: {
        aadAuthFailureMode: 'http401WithBearerChallenge'
      }
    }
  }
}

// --- User → Search Service RBAC -------------------------------------------

@description('Search Service Contributor – user manages search service')
resource searchContribUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, userPrincipalId, searchServiceContributorRoleId)
  scope: searchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchServiceContributorRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

@description('Search Index Data Contributor – user writes/manages indexes')
resource searchDataContribUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, userPrincipalId, searchIndexDataContributorRoleId)
  scope: searchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexDataContributorRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

@description('Search Index Data Reader – user reads search indexes')
resource searchDataReaderUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, userPrincipalId, searchIndexDataReaderRoleId)
  scope: searchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexDataReaderRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

// --- AI Services MI → Search Service RBAC ----------------------------------

@description('Search Index Data Reader – AI Services MI reads search indexes')
resource searchDataReaderAiServicesRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, aiServices.id, searchIndexDataReaderRoleId)
  scope: searchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexDataReaderRoleId)
    principalId: aiServices.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

@description('Search Index Data Contributor – AI Services MI writes search indexes')
resource searchDataContribAiServicesRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, aiServices.id, searchIndexDataContributorRoleId)
  scope: searchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexDataContributorRoleId)
    principalId: aiServices.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

@description('Search Service Contributor – AI Services MI manages search service')
resource searchContribAiServicesRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, aiServices.id, searchServiceContributorRoleId)
  scope: searchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchServiceContributorRoleId)
    principalId: aiServices.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- Project MI → Search Service RBAC --------------------------------------

@description('Search Index Data Reader – project MI reads search indexes')
resource searchDataReaderProjectRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, aiProject.id, searchIndexDataReaderRoleId)
  scope: searchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexDataReaderRoleId)
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

@description('Search Index Data Contributor – project MI writes search indexes')
resource searchDataContribProjectRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, aiProject.id, searchIndexDataContributorRoleId)
  scope: searchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchIndexDataContributorRoleId)
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

@description('Search Service Contributor – project MI manages search service')
resource searchContribProjectRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, aiProject.id, searchServiceContributorRoleId)
  scope: searchService
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', searchServiceContributorRoleId)
    principalId: aiProject.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- Search Service MI → AI Services (agentic retrieval LLM calls) ----------

@description('Cognitive Services User – Search MI calls LLM for KB agentic retrieval query planning and reranking')
resource searchMICogServicesUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiServices.id, searchService.id, cognitiveServicesUserRoleId)
  scope: aiServices
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesUserRoleId)
    principalId: searchService.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// Foundry IQ KB answer synthesis calls Azure OpenAI chat/embedding deployments under the Search MI.
// Cognitive Services OpenAI User grants the data-plane inference rights those calls require.
@description('Cognitive Services OpenAI User – Search MI invokes AOAI deployments for KB answer synthesis / vectorization')
resource searchMICogServicesOpenAIUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiServices.id, searchService.id, cognitiveServicesOpenAIUserRoleId)
  scope: aiServices
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAIUserRoleId)
    principalId: searchService.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- User → AI Services: OpenAI Contributor (data plane admin for models) ----

@description('Cognitive Services OpenAI User – data plane: invoke models, completions, embeddings')
resource cogServicesOpenAIUserRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(aiServices.id, userPrincipalId, cognitiveServicesOpenAIUserRoleId)
  scope: aiServices
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', cognitiveServicesOpenAIUserRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

// --- User → Log Analytics: Contributor (monitoring admin) --------------------

@description('Log Analytics Contributor – user has full admin on Log Analytics workspace')
resource logAnalyticsContribRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(logAnalytics.id, userPrincipalId, logAnalyticsContributorRoleId)
  scope: logAnalytics
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', logAnalyticsContributorRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

// --- User → App Insights: Monitoring Contributor (traces, metrics admin) -----

@description('Monitoring Contributor – user has full admin on App Insights traces and metrics')
resource monitoringContribRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsights.id, userPrincipalId, monitoringContributorRoleId)
  scope: appInsights
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringContributorRoleId)
    principalId: userPrincipalId
    principalType: 'User'
  }
}

// --- AI Services MI → Cosmos DB (Foundry thread/conversation storage) --------

resource cosmosDbAiServicesRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2025-04-15' = {
  parent: cosmosDbAccount
  name: guid(cosmosDbAccount.id, aiServices.id, cosmosDbDataContributorRoleId)
  properties: {
    roleDefinitionId: '${cosmosDbAccount.id}/sqlRoleDefinitions/${cosmosDbDataContributorRoleId}'
    principalId: aiServices.identity.principalId
    scope: cosmosDbAccount.id
  }
}

// --- Project MI → Cosmos DB (agent conversations) ----------------------------

resource cosmosDbProjectRole 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2025-04-15' = {
  parent: cosmosDbAccount
  name: guid(cosmosDbAccount.id, aiProject.id, cosmosDbDataContributorRoleId)
  properties: {
    roleDefinitionId: '${cosmosDbAccount.id}/sqlRoleDefinitions/${cosmosDbDataContributorRoleId}'
    principalId: aiProject.identity.principalId
    scope: cosmosDbAccount.id
  }
}

// --- Outputs ---------------------------------------------------------------

output aiServicesEndpoint string = aiServices.properties.endpoint
output aiServicesOpenAIEndpoint string = 'https://${aiServicesName}.openai.azure.com/'
output aiFoundryProjectEndpoint string = '${aiServices.properties.endpoint}api/projects/${aiProjectName}'
output aiProjectName string = aiProject.name
output aiServicesResourceName string = aiServices.name
output embeddingDeploymentName string = embeddingDeployment.outputs.deploymentName
output reasoningDeploymentName string = reasoningDeployment.outputs.deploymentName
output fastDeploymentName string = fastDeployment.outputs.deploymentName
output nanoDeploymentName string = nanoDeployment.outputs.deploymentName
output knowledgeBaseConnectionName string = knowledgeBaseConnectionName
output webIqConnectionName string = hasWebIqKey ? webIqConnectionName : ''
output webIqMcpUrl string = webIqMcpUrl
output groundingProvider string = groundingProvider
output keyVaultName string = keyVault.name
output cosmosDbEndpoint string = cosmosDbAccount.properties.documentEndpoint
output cosmosDbDatabaseName string = cosmosDbDatabase.name
output storageAccountName string = storageAccount.name
output searchServiceEndpoint string = 'https://${searchService.name}.search.windows.net'
output searchServiceName string = searchService.name
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output raiPolicyName string = contentPolicy.name
