$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$packageRoot = Join-Path $PSScriptRoot 'application-package'
$generatedSource = Join-Path $packageRoot 'src'
if (Test-Path -LiteralPath $generatedSource) {
    Remove-Item -LiteralPath $generatedSource -Recurse -Force
}

$sourceDirectories = @(
    'PublicSectorAgentDemos.Demo4.Application',
    'PublicSectorAgentDemos.Contracts',
    'PublicSectorAgentDemos.Identity',
    'PublicSectorAgentDemos.Observability'
)
foreach ($sourceDirectory in $sourceDirectories) {
    $source = Join-Path $repositoryRoot "src\$sourceDirectory"
    $destination = Join-Path $generatedSource $sourceDirectory
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath $source -Recurse -File |
        Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and
            $_.Extension -in @('.cs', '.cshtml', '.css', '.js')
        } |
        ForEach-Object {
            $relativePath = [IO.Path]::GetRelativePath($source, $_.FullName)
            $target = Join-Path $destination $relativePath
            $targetParent = Split-Path -Parent $target
            New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $target
    }
}

$contractDestination = Join-Path $generatedSource 'contracts'
New-Item -ItemType Directory -Path $contractDestination -Force | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'contracts') -File -Filter '*.cs' |
    ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $contractDestination
    }

$dataDestination = Join-Path $generatedSource 'PublicSectorAgentDemos.Demo4.Application\Data'
New-Item -ItemType Directory -Path $dataDestination -Force | Out-Null
foreach ($fixtureName in @('fixture-set.json', 'context-fixture-set.json')) {
    Copy-Item `
        -LiteralPath (Join-Path $repositoryRoot "data\hosted\v2\$fixtureName") `
        -Destination (Join-Path $dataDestination $fixtureName)
}

Copy-Item `
    -LiteralPath (Join-Path $repositoryRoot 'NuGet.config') `
    -Destination $packageRoot

Write-Host 'Prepared the bounded Demo 4 application package.'
