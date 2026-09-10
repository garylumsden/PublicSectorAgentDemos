# Optional Patriots local runtime.
# Dot-source this file after Common.ps1 and External.ps1.

function Resolve-DemoReadyPatriots {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [AllowEmptyString()][string]$ParameterPath,
        [Parameter(Mandatory)][hashtable]$SetupRepositories,
        [AllowEmptyString()][string]$ParameterAzdEnvironmentName,
        [switch]$Selected
    )

    if (-not $Selected) {
        return [pscustomobject]@{
            Status = 'not-configured'
            Repository = $null
            GitBaseline = $null
            GitStatusPreserved = $null
        }
    }
    $repository = Resolve-DemoReadyExternalRepository `
        -Definition (Get-DemoReadyExternalRepositoryDefinition).patriots `
        -RepositoryRoot $RepositoryRoot `
        -ParameterPath $ParameterPath `
        -SetupRepositories $SetupRepositories `
        -ParameterAzdEnvironmentName $ParameterAzdEnvironmentName `
        -CloneIfMissing
    try {
        $baseline = Get-DemoReadyGitStatus -RepositoryPath $repository.Path
        return [pscustomobject]@{
            Status = 'discovered'
            Repository = $repository
            GitBaseline = $baseline
            GitStatusPreserved = $null
        }
    }
    catch {
        if ($repository.CreatedByThisRun -and (Test-Path -LiteralPath $repository.Path)) {
            Remove-Item -LiteralPath $repository.Path -Recurse -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Start-DemoReadyPatriots {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][pscustomobject]$Integration,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$LogRoot,
        [Parameter(Mandatory)][string]$ScriptRoot,
        [Parameter(Mandatory)][AllowEmptyCollection()][Collections.Generic.List[object]]$Processes,
        [object[]]$PreservedProcesses = @(),
        [string[]]$SensitiveValues = @()
    )

    if ($Integration.Status -cne 'discovered') {
        return
    }
    if (@($PreservedProcesses | Where-Object name -ceq 'external-patriots').Count -gt 0) {
        throw 'A prior Patriots process record needs an identity review.'
    }

    $repository = $Integration.Repository
    try {
        Assert-DemoReadyPortsFree -Ports @(5081, 7081)
        $project = Join-Path $repository.Path 'src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj'
        Invoke-DemoReadyProjectBuild `
            -RepositoryPath $repository.Path `
            -ProjectPath $project `
            -LogRoot $LogRoot `
            -LogName 'patriots' `
            -SensitiveValues $SensitiveValues
        Assert-DemoReadyGitStatusPreserved `
            -RepositoryPath $repository.Path `
            -Baseline $Integration.GitBaseline `
            -DisplayName $repository.DisplayName
        $record = Start-DemoReadyProcess `
            -Name 'external-patriots' `
            -FilePath 'dotnet' `
            -Arguments @(
                'run', '--project', $project,
                '--no-build', '--no-restore', '--no-launch-profile'
            ) `
            -WorkingDirectory $repository.Path `
            -Environment @{
                ASPNETCORE_URLS = 'https://localhost:7081;http://localhost:5081'
                AZURE_TOKEN_CREDENTIALS = 'AzureCliCredential'
            } `
            -LogDirectory $LogRoot `
            -CommandMarker $project `
            -Endpoints @('http://localhost:5081/', 'https://localhost:7081/') `
            -ScriptRoot $ScriptRoot `
            -SensitiveValues $SensitiveValues
        $record.optionalExternalOwner = $RepositoryRoot
        $Processes.Add($record)
        Wait-DemoReadyEndpoint 'http://localhost:5081/' -TimeoutSeconds 600
        Wait-DemoReadyEndpoint 'https://localhost:7081/' -SkipCertificateCheck
        if ($record.process.HasExited) {
            throw 'The recorded Patriots supervisor exited.'
        }
        $Integration.Status = 'ready'
    }
    finally {
        Assert-DemoReadyGitStatusPreserved `
            -RepositoryPath $repository.Path `
            -Baseline $Integration.GitBaseline `
            -DisplayName $repository.DisplayName
        $Integration.GitStatusPreserved = $true
    }
}
