using './main.bicep'

param environmentName = readEnvironmentVariable('AZURE_ENV_NAME', 'demo4-preview')
param location = readEnvironmentVariable('AZURE_LOCATION', 'swedencentral')
param deployerPrincipalId = readEnvironmentVariable('AZURE_PRINCIPAL_ID', '')
param applicationApiClientId = readEnvironmentVariable('DEMO4_API_CLIENT_ID', '')
param applicationWebClientSecret = readEnvironmentVariable('DEMO4_WEB_CLIENT_SECRET', '')
param hostedAgentEndpoint = readEnvironmentVariable('AGENT_DEMO4_HOSTED_AGENT_RESPONSES_ENDPOINT', '')
