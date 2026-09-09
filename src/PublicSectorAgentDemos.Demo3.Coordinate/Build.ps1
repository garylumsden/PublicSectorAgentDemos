[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

# SDK selection starts from the working directory, not the --project path.
Push-Location $PSScriptRoot
try {
    dotnet build .\src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj --configuration $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "Council build failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}
