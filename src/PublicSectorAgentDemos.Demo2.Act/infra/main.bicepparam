using './main.bicep'

var toolsAuthorizedPrincipalObjectIdsValue = readEnvironmentVariable('AZURE_TOOLS_AUTHORIZED_PRINCIPAL_OBJECT_IDS', '')
var authorizedUserObjectIdsValue = readEnvironmentVariable('AZURE_AUTHORIZED_USER_OBJECT_IDS', '')
var adminUserObjectIdsValue = readEnvironmentVariable('AZURE_ADMIN_USER_OBJECT_IDS', '')
var defaultLocation = readEnvironmentVariable('AZURE_LOCATION', 'switzerlandnorth')

param environmentName = readEnvironmentVariable('AZURE_ENV_NAME')
param deployerPrincipalId = readEnvironmentVariable('AZURE_PRINCIPAL_ID')
param aiLocation = readEnvironmentVariable('AZURE_AI_LOCATION', defaultLocation)
param appLocation = readEnvironmentVariable('AZURE_APP_LOCATION', defaultLocation)
param appServiceHostNameSuffix = readEnvironmentVariable('AZURE_APP_SERVICE_HOSTNAME_SUFFIX', 'azurewebsites.net')
param accountSkuName = readEnvironmentVariable('AZURE_AI_ACCOUNT_SKU_NAME', 'S0')
param reasoningEffort = 'low'
param authorizedUserObjectIds = empty(authorizedUserObjectIdsValue)
  ? []
  : map(split(authorizedUserObjectIdsValue, ','), objectId => trim(objectId))
param adminUserObjectIds = empty(adminUserObjectIdsValue)
  ? []
  : map(split(adminUserObjectIdsValue, ','), objectId => trim(objectId))
param toolsAuthorizedPrincipalObjectIds = empty(toolsAuthorizedPrincipalObjectIdsValue)
  ? []
  : map(split(toolsAuthorizedPrincipalObjectIdsValue, ','), objectId => trim(objectId))

param chatDeployment = {
  name: 'gpt-5.4-mini'
  modelName: 'gpt-5.4-mini'
  modelVersion: '2026-03-17'
  skuName: 'GlobalStandard'
  capacity: 500
}
