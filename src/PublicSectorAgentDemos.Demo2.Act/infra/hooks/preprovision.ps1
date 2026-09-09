$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:AZURE_DEV_USER_AGENT = 'microsoft_foundry_skill'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

if ($env:AZURE_ENV_NAME -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,63}$') {
    throw 'AZURE_ENV_NAME must contain 1-64 letters, digits, or hyphens and start with a letter or digit.'
}
foreach ($name in @('AZURE_SUBSCRIPTION_ID', 'AZURE_PRINCIPAL_ID')) {
    $value = [Guid]::Empty
    if (-not [Guid]::TryParse([Environment]::GetEnvironmentVariable($name), [ref]$value) -or $value -eq [Guid]::Empty) {
        throw "$name must be a non-empty GUID."
    }
}
foreach ($name in @('AZURE_AUTHORIZED_USER_OBJECT_IDS', 'AZURE_ADMIN_USER_OBJECT_IDS', 'AZURE_TOOLS_AUTHORIZED_PRINCIPAL_OBJECT_IDS')) {
    $list = [Environment]::GetEnvironmentVariable($name)
    if ([string]::IsNullOrWhiteSpace($list)) { continue }
    $ids = $list.Split(',')
    $maximum = if ($name -eq 'AZURE_TOOLS_AUTHORIZED_PRINCIPAL_OBJECT_IDS') { 12 } else { 6 }
    if ($ids.Count -gt $maximum) { throw "$name accepts at most $maximum object IDs." }
    foreach ($id in $ids) {
        $value = [Guid]::Empty
        if (-not [Guid]::TryParse($id.Trim(), [ref]$value) -or $value -eq [Guid]::Empty) {
            throw "$name must contain comma-separated non-empty GUIDs."
        }
    }
}
if ([string]::IsNullOrWhiteSpace($env:AZURE_LOCATION)) {
    throw 'AZURE_LOCATION is required. Select an Azure location with the required service and model availability.'
}

$tenantOutput = & az account show --subscription $env:AZURE_SUBSCRIPTION_ID --query tenantId --output tsv --only-show-errors
$tenant = [Guid]::Empty
if ($LASTEXITCODE -ne 0 -or -not [Guid]::TryParse([string]$tenantOutput, [ref]$tenant) -or $tenant -eq [Guid]::Empty) {
    throw 'Azure CLI must be signed in to the selected subscription.'
}
$tokenOutput = & azd auth token --tenant-id $tenant.ToString() --scope 'https://ai.azure.com/.default' `
    --environment $env:AZURE_ENV_NAME --cwd $projectRoot --output json --no-prompt
if ($LASTEXITCODE -ne 0) { throw 'Unable to acquire an azd Foundry token for the selected subscription tenant.' }
function Assert-DeploymentToken {
    param([Parameter(Mandatory)][string]$Token, [Parameter(Mandatory)][string]$Client)
    $parts = $Token.Split('.')
    if ($parts.Count -ne 3) { throw "$Client returned an invalid token format." }
    $payload = $parts[1].Replace('-', '+').Replace('_', '/')
    $payload = $payload.PadRight($payload.Length + (4 - $payload.Length % 4) % 4, '=')
    $claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
    # These claims bind deployment identity selection; Azure services still validate every inbound token.
    if ([string]$claims.tid -ne $tenant.ToString() -or [string]$claims.oid -ne $env:AZURE_PRINCIPAL_ID) {
        throw "$Client must match AZURE_PRINCIPAL_ID and the selected subscription tenant."
    }
}
Assert-DeploymentToken -Token ($tokenOutput | ConvertFrom-Json).token -Client 'azd'
$tokenOutput = $null
$cliToken = & az account get-access-token --tenant $tenant.ToString() --resource 'https://management.azure.com/' `
    --query accessToken --output tsv --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Unable to acquire the Azure CLI deployment token.' }
Assert-DeploymentToken -Token $cliToken -Client 'Azure CLI'
$cliToken = $null
$env:AZURE_TENANT_ID = $tenant.ToString()
& azd env set AZURE_TENANT_ID $env:AZURE_TENANT_ID --environment $env:AZURE_ENV_NAME --cwd $projectRoot | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Unable to persist the selected subscription tenant in the azd environment.' }
Write-Host 'Demo2 deployment identity and tenant are configured. No other demo bootstrap is invoked.'
