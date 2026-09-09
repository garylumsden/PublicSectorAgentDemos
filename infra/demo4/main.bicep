targetScope = 'subscription'

@minLength(1)
@maxLength(64)
param environmentName string

@description('Azure region for the standalone preview resources.')
param location string = 'swedencentral'

@description('Object ID that receives package deployment access to the application.')
param deployerPrincipalId string = ''

@description('Client ID of the Entra application registration that protects the assessment API.')
param applicationApiClientId string = ''

@secure()
@description('Client secret of the Entra application registration that protects the web application.')
param applicationWebClientSecret string = ''

@description('Deployed hosted-agent Responses endpoint, when available.')
param hostedAgentEndpoint string = ''

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-11-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: {
    environment: environmentName
    SecurityControl: 'Ignore'
    workload: 'demo4-cross-government-control-preview'
    previewFixtureDataOnly: 'true'
  }
}

var validatedApplicationWebClientSecret = !empty(applicationWebClientSecret)
  ? applicationWebClientSecret
  : fail('applicationWebClientSecret is required for Demo 4 EasyAuth.')

var validatedApplicationApiClientId = !empty(applicationApiClientId)
  ? applicationApiClientId
  : fail('applicationApiClientId is required for Demo 4 EasyAuth.')

// EasyAuth treats an empty allowedPrincipals list as no restriction. Demo 4 must
// restrict sign-in to configured users, so an absent principal fails the deployment.
var validatedDeployerPrincipalId = !empty(deployerPrincipalId)
  ? deployerPrincipalId
  : fail('deployerPrincipalId is required so Demo 4 sign-in stays restricted to configured users.')

module resources './resources.bicep' = {
  scope: resourceGroup
  params: {
    environmentName: environmentName
    location: location
    deployerPrincipalId: validatedDeployerPrincipalId
    applicationApiClientId: validatedApplicationApiClientId
    applicationWebClientSecret: validatedApplicationWebClientSecret
    allowedUserPrincipalIds: [
      validatedDeployerPrincipalId
    ]
    hostedAgentEndpoint: hostedAgentEndpoint
  }
}

output AZURE_LOCATION string = location
output AZURE_RESOURCE_GROUP string = resourceGroup.name
output AZURE_SUBSCRIPTION_ID string = subscription().subscriptionId
output AZURE_TENANT_ID string = tenant().tenantId
output AZURE_AI_MODEL_DEPLOYMENT_NAME string = resources.outputs.chatDeploymentName
output MEMORY_STORE_EMBEDDING_MODEL_DEPLOYMENT_NAME string = resources.outputs.embeddingDeploymentName
output FOUNDRY_PROJECT_ENDPOINT string = resources.outputs.projectEndpoint
output AZURE_AI_PROJECT_ID string = resources.outputs.projectId
output DEMO4_APPLICATION_NAME string = resources.outputs.applicationName
output DEMO4_APPLICATION_IDENTITY_CLIENT_ID string = resources.outputs.applicationIdentityClientId
output DEMO4_APPLICATION_IDENTITY_PRINCIPAL_ID string = resources.outputs.applicationIdentityPrincipalId
output APPLICATIONINSIGHTS_RESOURCE_ID string = resources.outputs.applicationInsightsResourceId
output APPLICATIONINSIGHTS_NAME string = resources.outputs.applicationInsightsName
output APPLICATIONINSIGHTS_PROJECT_CONNECTION_NAME string = resources.outputs.applicationInsightsConnectionName
