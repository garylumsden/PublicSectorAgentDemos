$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$savedApplicationInsightsResourceId = $env:APPLICATIONINSIGHTS_RESOURCE_ID
try {
    if (-not [string]::IsNullOrWhiteSpace($env:DEMO4_APPLICATIONINSIGHTS_RESOURCE_ID)) {
        $env:APPLICATIONINSIGHTS_RESOURCE_ID = $env:DEMO4_APPLICATIONINSIGHTS_RESOURCE_ID
    }
    & (Join-Path $repositoryRoot 'infra\demo4\hooks\configure-hosted-agent.ps1') `
        -AzdProjectRoot $repositoryRoot
}
finally {
    $env:APPLICATIONINSIGHTS_RESOURCE_ID = $savedApplicationInsightsResourceId
}
