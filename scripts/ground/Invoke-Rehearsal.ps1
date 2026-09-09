[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [int]$Runs = 5,
    [ValidateRange(0, 100)]
    [double]$MinimumSuccessRate = 100,
    [ValidatePattern('^MPM-[0-9]{3}$')]
    [string]$ScenarioId,
    [string]$ResultsPath,
    [string]$RepositoryRoot = (Join-Path (Join-Path $PSScriptRoot '..') '..')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:RUN_DEMO1_CLOUD_INTEGRATION -ne 'true' -or
    [string]::IsNullOrWhiteSpace($env:AZURE_AI_FOUNDRY_ENDPOINT) -or
    [string]::IsNullOrWhiteSpace($env:DEMO1_CITATION_IDENTITY_PATH)) {
    throw 'Set RUN_DEMO1_CLOUD_INTEGRATION=true, AZURE_AI_FOUNDRY_ENDPOINT, and DEMO1_CITATION_IDENTITY_PATH before the rehearsal.'
}

$root = [System.IO.Path]::GetFullPath($RepositoryRoot)
$identityPath = [System.IO.Path]::IsPathRooted($env:DEMO1_CITATION_IDENTITY_PATH) `
    ? $env:DEMO1_CITATION_IDENTITY_PATH `
    : (Join-Path $root $env:DEMO1_CITATION_IDENTITY_PATH)
if (-not (Test-Path -LiteralPath $identityPath -PathType Leaf)) {
    throw "DEMO1_CITATION_IDENTITY_PATH does not identify a file: $identityPath"
}

$project = Join-Path $root 'tests\PublicSectorAgentDemos.Demo1.CloudIntegrationTests\PublicSectorAgentDemos.Demo1.CloudIntegrationTests.csproj'
$results = @()
$previousScenario = $env:DEMO1_SCENARIO_ID
$previousResultsPath = $env:DEMO1_REHEARSAL_RESULTS_PATH
if ($ScenarioId) {
    $env:DEMO1_SCENARIO_ID = $ScenarioId
}
if ($ResultsPath) {
    $env:DEMO1_REHEARSAL_RESULTS_PATH = [System.IO.Path]::GetFullPath($ResultsPath)
}

try {
    for ($run = 1; $run -le $Runs; $run++) {
        $started = [DateTimeOffset]::UtcNow
        & dotnet test $project `
            --no-restore `
            --filter 'Category=CloudIntegration' `
            --verbosity quiet
        $succeeded = $LASTEXITCODE -eq 0
        $results += [pscustomobject]@{
            run = $run
            succeeded = $succeeded
            durationSeconds = [Math]::Round(([DateTimeOffset]::UtcNow - $started).TotalSeconds, 2)
        }
    }
}
finally {
    $env:DEMO1_SCENARIO_ID = $previousScenario
    $env:DEMO1_REHEARSAL_RESULTS_PATH = $previousResultsPath
}

$successCount = @($results | Where-Object succeeded).Count
$successRate = [Math]::Round(($successCount / $Runs) * 100, 2)
$report = [pscustomobject]@{
    generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    runs = $Runs
    successes = $successCount
    failures = $Runs - $successCount
    successRatePercent = $successRate
    minimumSuccessRatePercent = $MinimumSuccessRate
    attemptsPerAgentPerScenario = 1
    factualAccuracy = 'Review the saved answers against the fixture rubric; a pass is not proof of correctness.'
    results = $results
}

$report | ConvertTo-Json -Depth 5
if ($successRate -lt $MinimumSuccessRate) {
    throw "Demo 1 rehearsal success rate was $successRate%, below the required $MinimumSuccessRate%."
}
