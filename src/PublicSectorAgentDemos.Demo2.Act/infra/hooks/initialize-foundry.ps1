$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:AZURE_DEV_USER_AGENT = 'microsoft_foundry_skill'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))

foreach ($name in @('AZURE_AI_PROJECT_ENDPOINT', 'DEFRA_TOOLS_URL')) {
    $uri = $null
    if (-not [Uri]::TryCreate([Environment]::GetEnvironmentVariable($name), [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'https' -or $uri.Port -ne 443 -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) {
        throw "$name must be an HTTPS URL without credentials, a query, or a fragment."
    }
}
$projectEndpoint = $env:AZURE_AI_PROJECT_ENDPOINT.TrimEnd('/')
if (([Uri]$projectEndpoint).Host -notlike '*.services.ai.azure.com') {
    throw 'AZURE_AI_PROJECT_ENDPOINT must use the Microsoft Foundry domain.'
}
$tenant = [Guid]::Empty
if (-not [Guid]::TryParse($env:AZURE_TENANT_ID, [ref]$tenant) -or $tenant -eq [Guid]::Empty) {
    throw 'AZURE_TENANT_ID must be a non-empty GUID.'
}
$token = & az account get-access-token --tenant $tenant.ToString() --resource 'https://ai.azure.com' `
    --query accessToken --output tsv --only-show-errors
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
    throw 'Unable to acquire the Foundry bootstrap token.'
}

function Wait-Endpoint {
    param([Parameter(Mandatory)][string]$Uri, [hashtable]$Headers = @{})
    for ($attempt = 1; $attempt -le 18; $attempt++) {
        $response = Invoke-WebRequest -Uri $Uri -Headers $Headers -TimeoutSec 60 `
            -MaximumRedirection 0 -SkipHttpErrorCheck
        if ($response.StatusCode -eq 200) { return }
        if ($response.StatusCode -notin @(401, 403, 404, 408, 429, 500, 502, 503, 504) -or $attempt -eq 18) {
            throw "Demo2 bootstrap readiness failed with HTTP $($response.StatusCode)."
        }
        Write-Host "Waiting for Demo2 service or role propagation ($attempt/18)..."
        Start-Sleep -Seconds 10
    }
}

Wait-Endpoint -Uri "$($env:DEFRA_TOOLS_URL.TrimEnd('/'))/health"
foreach ($name in @('DEFRA_TOOLS_FLOOD_SUPPORT_CONNECTION_NAME', 'AZURE_AI_APPLICATION_INSIGHTS_CONNECTION_NAME')) {
    $connection = [Environment]::GetEnvironmentVariable($name)
    if ($connection -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$') { throw "$name must be a valid connection name." }
    Wait-Endpoint -Uri "$projectEndpoint/connections/$connection`?api-version=v1" `
        -Headers @{ Authorization = "Bearer $token" }
}
$token = $null
$env:AZURE_TOKEN_CREDENTIALS = 'AzureCliCredential'
& dotnet run --project (Join-Path $projectRoot 'infra\bootstrap\Demo2.Bootstrap.csproj') `
    --configuration Release -- $projectRoot
if ($LASTEXITCODE -ne 0) { throw "Demo2 agent bootstrap failed with exit code $LASTEXITCODE." }
Write-Host 'Demo2 flood support agent is ready. Scenario state uses its own Cosmos container; the tools service initializes fictional resource data.'
