$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$packageRoot = Join-Path $PSScriptRoot 'agent-package'
$generatedRoots = @('src', 'data', 'skills')
foreach ($relativeRoot in $generatedRoots) {
    $path = Join-Path $packageRoot $relativeRoot
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
}

$sourceDirectories = @(
    'PublicSectorAgentDemos.Demo4.HostedAgents',
    'PublicSectorAgentDemos.Contracts',
    'PublicSectorAgentDemos.Identity',
    'PublicSectorAgentDemos.Observability'
)
foreach ($sourceDirectory in $sourceDirectories) {
    $source = Join-Path $repositoryRoot "src\$sourceDirectory"
    $destination = Join-Path $packageRoot "src\$sourceDirectory"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $source -File -Filter '*.cs' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }
}

$contractDestination = Join-Path $packageRoot 'src\contracts'
New-Item -ItemType Directory -Path $contractDestination -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'contracts') -File -Filter '*.cs' |
    ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $contractDestination
    }

$fixtureDestination = Join-Path $packageRoot 'data\hosted\v2'
New-Item -ItemType Directory -Path $fixtureDestination -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'data\hosted\v2') -File -Filter '*.json' |
    ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $fixtureDestination
    }

$skillNames = @('case-pattern-guidance', 'case-context-guidance')
foreach ($skillName in $skillNames) {
    $skillDestination = Join-Path $packageRoot "skills\demo4\$skillName\v1"
    New-Item -ItemType Directory -Path $skillDestination -Force | Out-Null
    Copy-Item `
        -LiteralPath (Join-Path $repositoryRoot "skills\demo4\$skillName\v1\SKILL.md") `
        -Destination $skillDestination
}

Copy-Item `
    -LiteralPath (Join-Path $repositoryRoot 'NuGet.config') `
    -Destination $packageRoot

Write-Host 'Prepared the bounded Demo 4 remote-build source package.'
