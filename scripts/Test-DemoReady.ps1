<#
.SYNOPSIS
    Runs the engineering validation of this repository. It never deploys and never starts the
    presentation.

.DESCRIPTION
    scripts\Invoke-DemoReady.ps1 is the ready-to-present command. It performs no test run and no
    deep validation. This script owns those checks.

    Default checks, with no Azure sign-in:
      1. The automation harness for the orchestration contract.
      2. Restore, build, and test of PublicSectorAgentDemos.slnx through the Microsoft package
         feed proxy.
      3. Compilation of every retained Bicep template and parameter file.
      4. Build of the bundled Demo 2 and council applications.

    Optional checks, behind explicit switches:
      -RunAzurePreview   Runs 'azd provision --preview' for the four owned
                         environments. It reports the planned change counts and fails on a
                         planned delete.
      -RunCloudTests     Runs the Demo 1 rehearsal and the Demo 4 cloud integration tests
                         against the already deployed owned environments.

    This script never runs an azd deployment or teardown command. It never starts a demo
    application and it never generates the Presenter catalog.
#>
[CmdletBinding()]
param(
    # Legacy paths must match the bundles. External overrides are not accepted.
    [string]$DefraRepoPath,

    [string]$AssuranceBoardRepoPath,

    # Path to the secret-free external repository setup file.
    [string]$SetupPath,

    # Base name of the repository-owned azd environments.
    [ValidatePattern('^[a-z][a-z0-9-]{1,18}[a-z0-9]$')]
    [string]$EnvironmentName = 'psad-demos',

    [ValidatePattern('^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$')]
    [string]$SubscriptionId,

    [ValidatePattern('^[a-z0-9]+$')]
    [string]$HostedLocation = 'swedencentral',

    [Alias('SkipExternalBuild')]
    [switch]$SkipBundledBuild,

    [switch]$RunAzurePreview,

    [switch]$RunCloudTests,

    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'DemoReady\Common.ps1')
. (Join-Path $PSScriptRoot 'DemoReady\Console.ps1')
. (Join-Path $PSScriptRoot 'DemoReady\External.ps1')
. (Join-Path $PSScriptRoot 'DemoReady\Owned.ps1')
. (Join-Path $PSScriptRoot 'Stop-DemoReady.ps1')

if ($MyInvocation.InvocationName -eq '.') {
    return
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runtimeRoot = Join-Path $repositoryRoot '.demo-ready'
$logRoot = Join-Path $runtimeRoot 'logs\validation'
$schemaPath = Join-Path $PSScriptRoot 'DemoReady\repositories.v1.schema.json'
$setupFilePath = [string]::IsNullOrWhiteSpace($SetupPath) `
    ? (Join-Path $runtimeRoot 'repositories.local.json') `
    : [IO.Path]::GetFullPath($SetupPath)

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $runtimeRoot 'validation.json'
}
else {
    $ReportPath = [IO.Path]::GetFullPath($ReportPath)
}
Assert-DemoReadyReportPath `
    -RepositoryRoot $repositoryRoot `
    -RuntimeRoot $runtimeRoot `
    -ReportPath $ReportPath
$null = New-Item -ItemType Directory -Path $logRoot -Force

$checks = [ordered]@{}
$sensitiveValues = [Collections.Generic.List[string]]::new()
$environmentNames = Get-DemoReadyEnvironmentNames -BaseName $EnvironmentName
$contexts = [ordered]@{
    Demo1 = Join-Path $repositoryRoot 'deploy\demo1'
    Demo2 = Join-Path $repositoryRoot 'src\PublicSectorAgentDemos.Demo2.Act'
    Council = Join-Path $repositoryRoot 'src\PublicSectorAgentDemos.Demo3.Coordinate'
    Demo4 = Join-Path $repositoryRoot 'deploy\demo4'
}
$validationPhase = 'prerequisites'

try {

# ---------------------------------------------------------------- prerequisites
$requiredTools = @('git', 'dotnet', 'pwsh', 'az')
if ($RunAzurePreview -or $RunCloudTests) {
    $requiredTools += 'azd'
}
foreach ($tool in $requiredTools) {
    if ($null -eq (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "The required command '$tool' is not installed."
    }
}
# The solution build replaces assemblies that a started demo application holds open. Fail here
# with the remedy instead of failing later inside MSBuild.
$processPath = Join-Path $runtimeRoot 'processes.json'
if (Test-Path -LiteralPath $processPath -PathType Leaf) {
    $recordedProcesses = Get-Content -LiteralPath $processPath -Raw | ConvertFrom-Json
    $liveProcesses = @(@($recordedProcesses.processes) | Where-Object {
        $null -ne $_ -and
        (Test-DemoReadyOwnedProcess -Record $_ -RepositoryRoot $repositoryRoot) -and
        [int]$_.pid -gt 0 -and
        $null -ne (Get-Process -Id ([int]$_.pid) -ErrorAction SilentlyContinue)
    })
    if ($liveProcesses.Count -gt 0) {
        throw (
            "The demonstration is running with $($liveProcesses.Count) tracked process(es). " +
            'Run scripts\Stop-DemoReady.ps1 first, because the build cannot replace an assembly ' +
            'that a started application holds.')
    }
}
Assert-DemoReadyNuGetProxy -RepositoryRoot $repositoryRoot
$checks['packageFeedProxy'] = 'passed'

# ------------------------------------------------------------- automation tests
$validationPhase = 'automation-tests'
Write-DemoReadyStep 'Running the orchestration automation harness.'
Invoke-DemoReadyNative `
    -FilePath 'pwsh' `
    -Arguments @(
        '-NoProfile', '-File',
        (Join-Path $repositoryRoot 'tests\automation\Invoke-DemoReady.Tests.ps1')
    ) `
    -WorkingDirectory $repositoryRoot `
    -LogPath (Join-Path $logRoot 'automation-tests.log')
$checks['automationTests'] = 'passed'

# --------------------------------------------------- repository build and tests
$validationPhase = 'repository-tests'
Write-DemoReadyStep 'Restoring, building, and testing the solution.'
Invoke-DemoReadyNative `
    -FilePath 'dotnet' `
    -Arguments @('restore', 'PublicSectorAgentDemos.slnx', '--configfile', 'NuGet.config') `
    -WorkingDirectory $repositoryRoot `
    -LogPath (Join-Path $logRoot 'restore.log')
Invoke-DemoReadyNative `
    -FilePath 'dotnet' `
    -Arguments @('build', 'PublicSectorAgentDemos.slnx', '--no-restore') `
    -WorkingDirectory $repositoryRoot `
    -LogPath (Join-Path $logRoot 'build.log')
Invoke-DemoReadyNative `
    -FilePath 'dotnet' `
    -Arguments @('test', 'PublicSectorAgentDemos.slnx', '--no-build', '--no-restore') `
    -WorkingDirectory $repositoryRoot `
    -LogPath (Join-Path $logRoot 'tests.log')
$checks['solutionTests'] = 'passed'

# ------------------------------------------------------------ Bicep compilation
$validationPhase = 'bicep'
Write-DemoReadyStep 'Compiling every retained Bicep template and parameter file.'
Assert-DemoReadyBicep `
    -RepositoryRoot $repositoryRoot `
    -ParameterEnvironment @{
        AZURE_ENV_NAME = $environmentNames.Demo4
        AZURE_LOCATION = $HostedLocation
        AZURE_PRINCIPAL_ID = '11111111-1111-1111-1111-111111111111'
    } `
    -SensitiveValues $sensitiveValues
$checks['bicep'] = 'passed'

# --------------------------------------------------------------- bundled builds
$validationPhase = 'bundled-build'
if ($SkipBundledBuild) {
    $checks['bundledBuilds'] = 'skipped'
}
else {
    $definitions = Get-DemoReadyExternalRepositoryDefinition
    $setupRepositories = Read-DemoReadySetupFile -Path $setupFilePath -SchemaPath $schemaPath
    $bundledProjects = @(
        [pscustomobject]@{
            Definition = $definitions.defra
            ParameterPath = $DefraRepoPath
            RelativeProject = 'src\Demo2.Web\Demo2.Web.csproj'
        },
        [pscustomobject]@{
            Definition = $definitions.assuranceBoard
            ParameterPath = $AssuranceBoardRepoPath
            RelativeProject = 'src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj'
        }
    )
    $bundledResults = [ordered]@{}
    foreach ($project in $bundledProjects) {
        $repository = Resolve-DemoReadyExternalRepository `
            -Definition $project.Definition `
            -RepositoryRoot $repositoryRoot `
            -ParameterPath $project.ParameterPath `
            -SetupRepositories $setupRepositories `
            -ParameterAzdEnvironmentName ''
        Write-DemoReadyStep "Building $($repository.DisplayName)."
        Invoke-DemoReadyProjectBuild `
            -RepositoryPath $repository.Path `
            -ProjectPath (Join-Path $repository.Path $project.RelativeProject) `
            -LogRoot $logRoot `
            -LogName $repository.Identity
        $bundledResults[$repository.Identity] = [ordered]@{
            repositoryPath = $repository.Path
            pathSource = $repository.PathSource
        }
    }
    $checks['bundledBuilds'] = $bundledResults
}

# ------------------------------------------------------------- Azure sign-in
$validationPhase = 'azure-context'
if ($RunAzurePreview -or $RunCloudTests) {
    $azureContext = Initialize-DemoReadyAzureContext `
        -RepositoryRoot $repositoryRoot `
        -SubscriptionId $SubscriptionId
    $SubscriptionId = $azureContext.SubscriptionId
    $sensitiveValues.Add($azureContext.TenantId)
    $sensitiveValues.Add($SubscriptionId)
    $sensitiveValues.Add($azureContext.PrincipalId)
}

# ------------------------------------------------------------ deployment preview
$validationPhase = 'azure-preview'
if ($RunAzurePreview) {
    $previewResults = [ordered]@{}
    foreach ($context in @(
        [pscustomobject]@{ Name = 'demo1'; Path = $contexts.Demo1; Environment = $environmentNames.Demo1 },
        [pscustomobject]@{ Name = 'demo2'; Path = $contexts.Demo2; Environment = $environmentNames.Demo2 },
        [pscustomobject]@{ Name = 'council'; Path = $contexts.Council; Environment = $environmentNames.Demo3 },
        [pscustomobject]@{ Name = 'demo4'; Path = $contexts.Demo4; Environment = $environmentNames.Demo4 }
    )) {
        Write-DemoReadyStep "Previewing the $($context.Name) deployment."
        $preview = Invoke-DemoReadyAzd `
            -Arguments @(
                'provision', '--preview',
                '--environment', $context.Environment,
                '--no-prompt'
            ) `
            -WorkingDirectory $context.Path `
            -LogPath (Join-Path $logRoot "$($context.Name)-preview.log") `
            -SensitiveValues $sensitiveValues `
            -CaptureOutput `
            -Quiet
        $deleteCount = [regex]::Matches($preview, '(?m)^\s*[-+~*]?\s*Delete\b').Count
        if ($deleteCount -gt 0) {
            throw "The $($context.Name) deployment preview plans $deleteCount delete operation(s)."
        }
        $previewResults[$context.Name] = [ordered]@{
            environment = $context.Environment
            create = [regex]::Matches($preview, '(?m)^\s*[-+~*]?\s*Create\b').Count
            modify = [regex]::Matches($preview, '(?m)^\s*[-+~*]?\s*Modify\b').Count
            delete = $deleteCount
        }
    }
    $checks['azurePreview'] = $previewResults
}
else {
    $checks['azurePreview'] = 'skipped'
}

# ----------------------------------------------------------------- cloud tests
$validationPhase = 'cloud-tests'
if ($RunCloudTests) {
    $demo1Values = Get-DemoReadyAzdValues $contexts.Demo1 $environmentNames.Demo1 $sensitiveValues
    $demo1TestEnvironment = @{}
    foreach ($entry in $demo1Values.GetEnumerator()) {
        $demo1TestEnvironment[$entry.Key] = [string]$entry.Value
    }
    $demo1TestEnvironment.RUN_DEMO1_CLOUD_INTEGRATION = 'true'
    Write-DemoReadyStep 'Running the Demo 1 cloud rehearsal.'
    Invoke-DemoReadyNative `
        -FilePath 'pwsh' `
        -Arguments @(
            '-NoProfile', '-File',
            (Join-Path $repositoryRoot 'scripts\ground\Invoke-Rehearsal.ps1'),
            '-RepositoryRoot', $repositoryRoot
        ) `
        -WorkingDirectory $repositoryRoot `
        -LogPath (Join-Path $logRoot 'demo1-rehearsal.log') `
        -Environment $demo1TestEnvironment `
        -SensitiveValues $sensitiveValues

    $demo4Values = Get-DemoReadyAzdValues $contexts.Demo4 $environmentNames.Demo4 $sensitiveValues
    $demo4Secret = [string]$demo4Values['DEMO4_WEB_CLIENT_SECRET']
    if (-not [string]::IsNullOrWhiteSpace($demo4Secret) -and
        -not $sensitiveValues.Contains($demo4Secret)) {
        # The cloud tests receive the whole Demo 4 environment. Mask its client credential first.
        $sensitiveValues.Add($demo4Secret)
    }
    $demo4TestEnvironment = @{}
    foreach ($entry in $demo4Values.GetEnumerator()) {
        $demo4TestEnvironment[$entry.Key] = [string]$entry.Value
    }
    $demo4TestEnvironment.RUN_DEMO4_CLOUD_INTEGRATION = 'true'
    # The hosted agent publishes its responses endpoint under an agent-prefixed azd name. The
    # cloud tests read DEMO4_HOSTED_AGENT_ENDPOINT and skip in silence when it is absent, so the
    # value is mapped here and every required value is checked before the run.
    if ([string]::IsNullOrWhiteSpace($demo4TestEnvironment['DEMO4_HOSTED_AGENT_ENDPOINT'])) {
        $demo4TestEnvironment['DEMO4_HOSTED_AGENT_ENDPOINT'] =
            [string]$demo4Values['AGENT_DEMO4_HOSTED_AGENT_RESPONSES_ENDPOINT']
    }
    foreach ($name in @(
        'DEMO4_HOSTED_AGENT_ENDPOINT',
        'FOUNDRY_PROJECT_ENDPOINT',
        'AZURE_AI_MODEL_DEPLOYMENT_NAME'
    )) {
        if ([string]::IsNullOrWhiteSpace($demo4TestEnvironment[$name])) {
            throw (
                "The '$($environmentNames.Demo4)' azd environment does not publish $name. " +
                'The Demo 4 cloud tests would skip in silence.')
        }
    }
    if ($demo4TestEnvironment['DEMO4_HOSTED_AGENT_ENDPOINT'] -notmatch
        '^https://[^/]+\.services\.ai\.azure\.com/.+/responses\?api-version=v1$') {
        throw 'The Demo 4 hosted-agent responses endpoint is invalid.'
    }
    Write-DemoReadyStep 'Running the Demo 4 cloud integration tests.'
    Invoke-DemoReadyNative `
        -FilePath 'dotnet' `
        -Arguments @(
            'test',
            'tests\PublicSectorAgentDemos.Demo4.Tests\PublicSectorAgentDemos.Demo4.Tests.csproj',
            '--no-restore',
            '--filter', 'Category=CloudIntegration'
        ) `
        -WorkingDirectory $repositoryRoot `
        -LogPath (Join-Path $logRoot 'demo4-cloud-tests.log') `
        -Environment $demo4TestEnvironment `
        -SensitiveValues $sensitiveValues
    $checks['cloudTests'] = 'passed'
}
else {
    $checks['cloudTests'] = 'skipped'
}

$validationPhase = 'report'
Write-DemoReadyJsonAtomic `
    -Path $ReportPath `
    -Value ([ordered]@{
        version = 1
        generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
        status = 'passed'
        environments = [ordered]@{
            demo1 = $environmentNames.Demo1
            demo2 = $environmentNames.Demo2
            council = $environmentNames.Demo3
            demo4 = $environmentNames.Demo4
        }
        checks = $checks
        deployed = $false
        presentationStarted = $false
    })
Write-Host "Demo-ready validation passed. Report: $ReportPath"
}
catch {
    Write-DemoReadyJsonAtomic `
        -Path $ReportPath `
        -Value ([ordered]@{
            version = 1
            generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
            status = 'failed'
            phase = $validationPhase
            checks = $checks
            error = 'The validation phase failed. Review the masked logs.'
        })
    throw
}
