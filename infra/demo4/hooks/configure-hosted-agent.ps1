[CmdletBinding()]
param(
    [string]$AzdProjectRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:AZURE_DEV_USER_AGENT = 'microsoft_foundry_skill'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$azdProjectRoot = [string]::IsNullOrWhiteSpace($AzdProjectRoot) `
    ? (Resolve-Path (Join-Path $repositoryRoot 'deploy\demo4')).Path `
    : (Resolve-Path $AzdProjectRoot).Path
$agentName = 'demo4-hosted-agent'
$projectId = $env:AZURE_AI_PROJECT_ID
$applicationName = $env:DEMO4_APPLICATION_NAME
$resourceGroup = $env:AZURE_RESOURCE_GROUP
$subscriptionId = $env:AZURE_SUBSCRIPTION_ID
$applicationInsightsResourceId = $env:APPLICATIONINSIGHTS_RESOURCE_ID
$foundryUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d'
$monitoringMetricsPublisherRoleId = '3913510d-42f4-4e42-8a64-420c390055eb'

if ($projectId -notmatch '^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft\.CognitiveServices/accounts/[^/]+/projects/[^/]+$' -or
    $subscriptionId -notmatch '^[0-9a-fA-F-]{36}$' -or
    $applicationInsightsResourceId -notmatch '^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft\.Insights/components/[^/]+$' -or
    [string]::IsNullOrWhiteSpace($applicationName) -or
    [string]::IsNullOrWhiteSpace($resourceGroup)) {
    throw 'The hosted-agent configuration requires valid azd environment outputs.'
}

$agent = $null
for ($attempt = 1; $attempt -le 12 -and $null -eq $agent; $attempt++) {
    $agentJson = & azd ai agent show $agentName `
        --cwd $azdProjectRoot `
        --output json 2>$null
    if ($LASTEXITCODE -eq 0) {
        $agent = $agentJson | ConvertFrom-Json -Depth 30
    }
    elseif ($attempt -lt 12) {
        Start-Sleep -Seconds 10
    }
}
if ($null -eq $agent -or
    [string] $agent.instance_identity.principal_id -notmatch '^[0-9a-fA-F-]{36}$') {
    throw 'The hosted agent did not expose a valid instance identity.'
}

$principalId = [string] $agent.instance_identity.principal_id
$roleDefinitionId = "/subscriptions/$subscriptionId/providers/Microsoft.Authorization/roleDefinitions/$foundryUserRoleId"
$assignment = & az role assignment list `
    --assignee $principalId `
    --scope $projectId `
    --subscription $subscriptionId `
    --query "[?roleDefinitionId=='$roleDefinitionId'].id | [0]" `
    --output tsv `
    --only-show-errors
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the hosted-agent role assignment.'
}
if ([string]::IsNullOrWhiteSpace($assignment)) {
    & az role assignment create `
        --assignee-object-id $principalId `
        --assignee-principal-type ServicePrincipal `
        --role $foundryUserRoleId `
        --scope $projectId `
        --subscription $subscriptionId `
        --output none `
        --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to grant the hosted agent Foundry project access.'
    }
}

$telemetryRoleDefinitionId = "/subscriptions/$subscriptionId/providers/Microsoft.Authorization/roleDefinitions/$monitoringMetricsPublisherRoleId"
$telemetryAssignment = & az role assignment list `
    --assignee $principalId `
    --scope $applicationInsightsResourceId `
    --subscription $subscriptionId `
    --query "[?roleDefinitionId=='$telemetryRoleDefinitionId'].id | [0]" `
    --output tsv `
    --only-show-errors
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to inspect the hosted-agent telemetry role assignment.'
}
if ([string]::IsNullOrWhiteSpace($telemetryAssignment)) {
    & az role assignment create `
        --assignee-object-id $principalId `
        --assignee-principal-type ServicePrincipal `
        --role $monitoringMetricsPublisherRoleId `
        --scope $applicationInsightsResourceId `
        --subscription $subscriptionId `
        --output none `
        --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        throw 'Unable to grant the hosted agent Application Insights publishing access.'
    }
}

function Find-ResponsesEndpoint {
    param([Parameter(Mandatory)] $Value)

    $found = [Collections.Generic.List[string]]::new()
    function Visit {
        param($Node)
        if ($null -eq $Node) {
            return
        }
        if ($Node -is [string]) {
            $uri = $null
            if ([Uri]::TryCreate($Node, [UriKind]::Absolute, [ref] $uri) -and
                $uri.Scheme -eq 'https' -and
                $uri.Port -eq 443 -and
                $uri.Host.EndsWith('.services.ai.azure.com', [StringComparison]::OrdinalIgnoreCase) -and
                $uri.AbsolutePath.EndsWith('/responses', [StringComparison]::Ordinal) -and
                [string]::IsNullOrEmpty($uri.UserInfo) -and
                $uri.Query -ceq '?api-version=v1' -and
                [string]::IsNullOrEmpty($uri.Fragment)) {
                $found.Add($uri.AbsoluteUri)
            }
            return
        }
        if ($Node -is [Collections.IDictionary]) {
            foreach ($entry in $Node.GetEnumerator()) {
                Visit $entry.Value
            }
            return
        }
        if ($Node -is [Collections.IEnumerable]) {
            foreach ($item in $Node) {
                Visit $item
            }
            return
        }
        if ($Node.GetType().IsValueType) {
            return
        }
        foreach ($property in $Node.PSObject.Properties) {
            Visit $property.Value
        }
    }

    Visit $Value
    $unique = @($found | Select-Object -Unique)
    if ($unique.Count -ne 1) {
        throw 'The hosted agent did not expose one valid Responses endpoint.'
    }
    return $unique[0]
}

$responsesEndpoint = Find-ResponsesEndpoint -Value $agent
& az webapp config appsettings set `
    --name $applicationName `
    --resource-group $resourceGroup `
    --subscription $subscriptionId `
    --settings "DEMO4_HOSTED_AGENT_ENDPOINT=$responsesEndpoint" `
    --output none `
    --only-show-errors
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to configure the application Responses endpoint.'
}

Write-Host 'Configured hosted-agent identity, telemetry access, and the application endpoint.'
