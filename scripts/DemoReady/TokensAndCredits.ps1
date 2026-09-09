# Optional external runtime. No azd commands or external configuration writes.

function Resolve-DemoReadyTokensAndCredits {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [AllowEmptyString()][string]$ParameterPath,
        [Parameter(Mandatory)][hashtable]$SetupRepositories,
        [switch]$Selected
    )

    if ($PSBoundParameters.ContainsKey('Selected') -and -not $Selected) {
        return [pscustomobject]@{
            Status = 'not-configured'
            Repository = $null
            GitBaseline = $null
            GitStatusPreserved = $null
        }
    }
    $repository = $null
    try {
        $repository = Resolve-DemoReadyExternalRepository `
            -Definition (Get-DemoReadyExternalRepositoryDefinition).tokensAndCredits `
            -RepositoryRoot $RepositoryRoot -ParameterPath $ParameterPath `
            -SetupRepositories $SetupRepositories `
            -CloneIfMissing:$Selected `
            -AllowAbsent:(-not $Selected)
        if ($null -eq $repository) {
            return [pscustomobject]@{
                Status = 'not-configured'
                Repository = $null
                GitBaseline = $null
                GitStatusPreserved = $null
            }
        }
        $baseline = Get-DemoReadyGitStatus -RepositoryPath $repository.Path
        return [pscustomobject]@{ Status = 'discovered'; Repository = $repository; GitBaseline = $baseline; GitStatusPreserved = $null }
    }
    catch {
        if ($null -ne $repository -and $repository.CreatedByThisRun -and
            (Test-Path -LiteralPath $repository.Path)) {
            Remove-Item -LiteralPath $repository.Path -Recurse -Force -ErrorAction SilentlyContinue
        }
        if ($Selected) {
            throw 'Tokens and Credits repository resolution failed. Check the configured path and required files.'
        }
        Write-Warning 'Tokens and Credits discovery failed. Check the configured path and required files. The main presentation can continue.'
        return [pscustomobject]@{
            Status = 'failed'
            Repository = $null
            GitBaseline = $null
            GitStatusPreserved = $null
        }
    }
}

function Wait-DemoReadyTokensHealth {
    [CmdletBinding()]
    param([ValidateRange(1, 120)][int]$TimeoutSeconds = 30)

    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        try {
            $response = Invoke-WebRequest -Uri 'http://localhost:5041/api/embeddings/manifest' `
                -Method Get -TimeoutSec 5 -MaximumRedirection 0
            $manifest = $response.Content | ConvertFrom-Json -AsHashtable
            if ([int]$response.StatusCode -eq 200 -and
                $manifest -is [Collections.IDictionary] -and
                [string]$manifest['origin'] -ceq 'local-static-embedding' -and
                ($manifest['dimensions'] -is [long] -or $manifest['dimensions'] -is [int]) -and
                ($manifest['vocabularyCount'] -is [long] -or $manifest['vocabularyCount'] -is [int]) -and
                $manifest['dimensions'] -gt 0 -and $manifest['dimensions'] -le [int]::MaxValue -and
                $manifest['vocabularyCount'] -gt 0 -and $manifest['vocabularyCount'] -le [int]::MaxValue) {
                return
            }
        }
        catch {
            Write-Verbose 'Tokens and Credits health has no valid local response yet. Retrying within the startup timeout.'
        }
        Start-Sleep -Milliseconds 200
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw 'Tokens and Credits did not return its local embedding manifest. No model request was made.'
}

function Start-DemoReadyTokensAndCredits {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][pscustomobject]$Integration,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$LogRoot,
        [Parameter(Mandatory)][string]$ScriptRoot,
        [Parameter(Mandatory)][AllowEmptyCollection()][Collections.Generic.List[object]]$Processes,
        [object[]]$PreservedProcesses = @(),
        [string[]]$SensitiveValues = @(),
        [switch]$Required
    )

    if ($Integration.Status -cne 'discovered') {
        return
    }
    $repository = $Integration.Repository
    $phase = 'port-check'
    $failure = $null
    try {
        if (@($PreservedProcesses | Where-Object name -ceq 'tokens-and-credits').Count -gt 0) {
            throw 'A prior Tokens and Credits record needs an identity review.'
        }
        Assert-DemoReadyPortsFree -Ports @(5041)
        $project = Join-Path $repository.Path 'src\TokensAndCredits.Web\TokensAndCredits.Web.csproj'
        $phase = 'build'
        Invoke-DemoReadyExternalBuild -Repository $repository -ProjectPath $project `
            -LogRoot $LogRoot -GitBaseline $Integration.GitBaseline -SensitiveValues $SensitiveValues
        $phase = 'process-start'
        $record = Start-DemoReadyProcess -Name 'tokens-and-credits' -FilePath 'dotnet' `
            -Arguments @('run', '--project', $project, '--no-build', '--no-restore', '--no-launch-profile') `
            -WorkingDirectory $repository.Path `
            -Environment @{
                ASPNETCORE_URLS = 'http://localhost:5041'
                DOTNET_ENVIRONMENT = 'Development'
            } `
            -LogDirectory $LogRoot -CommandMarker $project -Endpoints @('http://localhost:5041/') `
            -ScriptRoot $ScriptRoot -OptionalExternalOwner $RepositoryRoot -SensitiveValues $SensitiveValues
        $Processes.Add($record)
        $phase = 'local-health'
        Wait-DemoReadyTokensHealth
        if ($record.process.HasExited) {
            throw 'The recorded Tokens and Credits supervisor exited.'
        }
        $Integration.Status = 'ready'
    }
    catch {
        $failure = $_
        $Integration.Status = 'failed'
        if (-not $Required) {
            Write-Warning "Tokens and Credits failed during $phase. Review its masked logs. The main presentation can continue."
        }
    }
    finally {
        try {
            Assert-DemoReadyGitStatusPreserved -RepositoryPath $repository.Path `
                -Baseline $Integration.GitBaseline -DisplayName $repository.DisplayName
            $Integration.GitStatusPreserved = $true
        }
        catch {
            $Integration.GitStatusPreserved = $false
            $Integration.Status = 'failed'
            $failure = $_
            if (-not $Required) {
                Write-Warning 'Tokens and Credits Git preservation could not be confirmed. Review its checkout. The main presentation can continue.'
            }
        }
    }
    if ($Required -and $null -ne $failure) {
        throw $failure
    }
}
