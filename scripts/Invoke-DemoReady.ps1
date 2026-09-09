<#
.SYNOPSIS
    Deploys selected demonstrations, starts selected local applications, and starts the Presenter.

.DESCRIPTION
    With no selection arguments in an interactive console, this command starts a guided flow.
    With no selection arguments in automation, it preserves the original four-demo deployment.
    Use scripts\Test-DemoReady.ps1 for tests, builds, and deep validation.
#>
[CmdletBinding()]
param(
    [switch]$Demo1,

    [Alias('Act')]
    [switch]$Demo2,

    [Alias('Council')]
    [switch]$Demo3,

    [Alias('Hosted')]
    [switch]$Demo4,

    [switch]$Patriots,

    [switch]$TokensAndCredits,

    [switch]$All,

    [switch]$NonInteractive,

    # Legacy override. Only src\PublicSectorAgentDemos.Demo2.Act is accepted.
    [string]$DefraRepoPath,

    # Legacy override. Only src\PublicSectorAgentDemos.Demo3.Coordinate is accepted.
    [string]$AssuranceBoardRepoPath,

    [string]$PatriotsRepoPath,

    [string]$TokensAndCreditsRepoPath,

    # Legacy Patriots selection alias.
    [switch]$IncludePatriots,

    [ValidatePattern('^[a-z][a-z0-9-]{0,62}[a-z0-9]$')]
    [string]$DefraEnvironmentName,

    [ValidatePattern('^[a-z][a-z0-9-]{0,62}[a-z0-9]$')]
    [string]$AssuranceBoardEnvironmentName,

    [ValidatePattern('^[a-z][a-z0-9-]{1,18}[a-z0-9]$')]
    [string]$EnvironmentName = 'psad-demos',

    [ValidatePattern('^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$')]
    [string]$SubscriptionId,

    [ValidatePattern('^[a-z0-9]+$')]
    [string]$Demo1Location = 'switzerlandnorth',

    [ValidatePattern('^[a-z0-9]+$')]
    [string]$HostedLocation = 'swedencentral',

    [ValidatePattern('^[a-z0-9]+$')]
    [string]$Demo2Location = 'swedencentral',

    [ValidatePattern('^[a-z0-9]+$')]
    [string]$CouncilLocation = 'swedencentral',

    [string]$SetupPath,

    [string]$ReportPath,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArguments
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'DemoReady\Common.ps1')
. (Join-Path $PSScriptRoot 'DemoReady\Console.ps1')
. (Join-Path $PSScriptRoot 'DemoReady\External.ps1')
. (Join-Path $PSScriptRoot 'DemoReady\TokensAndCredits.ps1')
. (Join-Path $PSScriptRoot 'DemoReady\Patriots.ps1')
. (Join-Path $PSScriptRoot 'DemoReady\Guided.ps1')
. (Join-Path $PSScriptRoot 'DemoReady\Entra.ps1')
. (Join-Path $PSScriptRoot 'DemoReady\Owned.ps1')
. (Join-Path $PSScriptRoot 'DemoReady\Presenter.ps1')
. (Join-Path $PSScriptRoot 'Stop-DemoReady.ps1')

if ($MyInvocation.InvocationName -eq '.') {
    return
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runtimeRoot = Join-Path $repositoryRoot '.demo-ready'
$logRoot = Join-Path $runtimeRoot 'logs'
$catalogPath = Join-Path $runtimeRoot 'sessions.generated.json'
$processPath = Join-Path $runtimeRoot 'processes.json'
$entraStatePath = Join-Path $runtimeRoot "entra-apps.$EnvironmentName.json"
$schemaPath = Join-Path $PSScriptRoot 'DemoReady\repositories.v1.schema.json'
$setupFilePath = [string]::IsNullOrWhiteSpace($SetupPath) `
    ? (Join-Path $runtimeRoot 'repositories.local.json') `
    : [IO.Path]::GetFullPath($SetupPath)
$presenterProject = 'src\PublicSectorAgentDemos.Presenter\PublicSectorAgentDemos.Presenter.csproj'
$demo1WebProject = Join-Path $repositoryRoot 'src\PublicSectorAgentDemos.Demo1.Web\PublicSectorAgentDemos.Demo1.Web.csproj'
$demo1WebUrl = 'http://localhost:5090/'
$assuranceHttpUrl = 'http://localhost:5080/'
$assuranceHttpsUrl = 'https://localhost:7080/'
$patriotsHttpUrl = 'http://localhost:5081/'
$patriotsHttpsUrl = 'https://localhost:7081/'
$presenterUrl = 'http://localhost:5088/'
$demoDossierRoot = Join-Path $repositoryRoot 'demo-dossiers'
$assuranceDossierPath = Join-Path $demoDossierRoot 'cross-government\dossier-shared-ai-service.md'
$patriotsDossierPath = Join-Path $demoDossierRoot 'patriots\01-puppet-president.md'

if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $runtimeRoot 'readiness.json'
}
else {
    $ReportPath = [IO.Path]::GetFullPath($ReportPath)
}
Assert-DemoReadyReportPath `
    -RepositoryRoot $repositoryRoot `
    -RuntimeRoot $runtimeRoot `
    -ReportPath $ReportPath
$null = New-Item -ItemType Directory -Path $logRoot -Force

$selectionResult = Get-DemoReadySelection `
    -Demo1:$Demo1 `
    -Demo2:$Demo2 `
    -Demo3:$Demo3 `
    -Demo4:$Demo4 `
    -Patriots:$Patriots `
    -TokensAndCredits:$TokensAndCredits `
    -All:$All `
    -IncludePatriots:$IncludePatriots `
    -NonInteractive:$NonInteractive `
    -RemainingArguments $RemainingArguments
$selection = $selectionResult.Items

Write-DemoReadyBanner `
    -Title 'Public Sector Agent Demos - demo ready' `
    -Lines @(
        'Deploys the selected demonstrations, then starts them and the Presenter.',
        $selectionResult.Guided `
            ? 'Guided setup. Nothing is deployed until you confirm the plan.' `
            : 'Selection supplied by argument. Review the plan below.'
    )

$locations = [ordered]@{
    Demo1 = $Demo1Location
    Demo2 = $Demo2Location
    Demo3 = $CouncilLocation
    Demo4 = $HostedLocation
}
$demoReadyPhase = 'initializing'
$environmentNames = Get-DemoReadyEnvironmentNames -BaseName $EnvironmentName
$optionalCheckoutCreated = @{}
$optionalCheckoutPaths = @{}
$processes = [Collections.Generic.List[object]]::new()
$preservedProcesses = @()
$processStateManaged = $false
$sensitiveValues = [Collections.Generic.List[string]]::new()

Write-DemoReadyJsonAtomic `
    -Path $ReportPath `
    -Value ([ordered]@{
        version = 1
        generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
        status = 'in-progress'
        phase = $demoReadyPhase
        environmentName = $EnvironmentName
        environments = $environmentNames
        selections = $selection
    })

try {
    $demoReadyPhase = 'prerequisites'
    foreach ($tool in @('git', 'dotnet', 'pwsh', 'az', 'azd')) {
        if ($null -eq (Get-Command $tool -ErrorAction SilentlyContinue)) {
            throw "The required command '$tool' is not installed."
        }
    }

    $repositoryBaseline = Get-DemoReadyGitStatus -RepositoryPath $repositoryRoot
    $setupRepositories = Read-DemoReadySetupFile -Path $setupFilePath -SchemaPath $schemaPath
    $definitions = Get-DemoReadyExternalRepositoryDefinition
    if ((-not [string]::IsNullOrWhiteSpace($DefraEnvironmentName) -and
            $DefraEnvironmentName -cne $environmentNames.Demo2) -or
        (-not [string]::IsNullOrWhiteSpace($AssuranceBoardEnvironmentName) -and
            $AssuranceBoardEnvironmentName -cne $environmentNames.Demo3)) {
        throw 'Legacy environment overrides must match the derived Demo 2 and council names.'
    }

    $contexts = [ordered]@{
        Demo1 = Join-Path $repositoryRoot 'deploy\demo1'
        Demo2 = Join-Path $repositoryRoot 'src\PublicSectorAgentDemos.Demo2.Act'
        Council = Join-Path $repositoryRoot 'src\PublicSectorAgentDemos.Demo3.Coordinate'
        Demo4 = Join-Path $repositoryRoot 'deploy\demo4'
    }
    $preflightContexts = [ordered]@{
        Demo1 = $contexts.Demo1
        Demo2 = $contexts.Demo2
        Demo3 = $contexts.Council
        Demo4 = $contexts.Demo4
    }

    $demoReadyPhase = 'azure-readiness'
    $subscriptionSummary = $null
    if ($selectionResult.Guided) {
        $subscriptionSummary = Get-DemoReadyAzureSubscriptionSummary -RepositoryRoot $repositoryRoot
        Confirm-DemoReadyAzureSubscription -Subscription $subscriptionSummary
        if (-not [string]::IsNullOrWhiteSpace($SubscriptionId) -and
            $SubscriptionId -cne $subscriptionSummary.Id) {
            throw 'The supplied subscription does not match the confirmed Azure CLI subscription.'
        }
    }
    $azureContext = Initialize-DemoReadyAzureContext `
        -RepositoryRoot $repositoryRoot `
        -SubscriptionId $SubscriptionId
    $SubscriptionId = $azureContext.SubscriptionId
    $principalId = $azureContext.PrincipalId
    $sensitiveValues.Add($azureContext.TenantId)
    $sensitiveValues.Add($SubscriptionId)
    $sensitiveValues.Add($principalId)

    if ($selectionResult.Guided) {
        $selection = Read-DemoReadyGuidedSelection
        $locations = Read-DemoReadyGuidedLocations `
            -Selection $selection `
            -Defaults $locations
    }

    Assert-DemoReadyAzdEnvironmentAccess `
        -Selection $selection `
        -Contexts $preflightContexts `
        -SensitiveValues $sensitiveValues

    $externalPlan = @(
        @(
            [pscustomobject]@{ Key = 'Patriots'; Definition = $definitions.patriots; Path = $PatriotsRepoPath },
            [pscustomobject]@{ Key = 'TokensAndCredits'; Definition = $definitions.tokensAndCredits; Path = $TokensAndCreditsRepoPath }
        ) | Where-Object { $selection[$_.Key] } | ForEach-Object {
            Get-DemoReadyExternalPlanEntry `
                -Definition $_.Definition `
                -RepositoryRoot $repositoryRoot `
                -ParameterPath $_.Path `
                -SetupRepositories $setupRepositories
        }
    )
    foreach ($entry in $externalPlan) {
        $identity = [string]$entry.Identity
        $optionalCheckoutCreated[$identity] = $false
        $optionalCheckoutPaths[$identity] = [string]$entry.Path
    }
    $capacityPlan = Show-DemoReadyDeploymentPlan `
        -Selection $selection `
        -Locations $locations `
        -EnvironmentNames $environmentNames `
        -Subscription ($selectionResult.Guided ? $subscriptionSummary : $null) `
        -ExternalPlan $externalPlan
    if ($selectionResult.Guided) {
        Write-Host ''
        if (-not (Read-DemoReadyYesNo -Prompt 'Continue with this deployment plan?' -Default $false)) {
            throw 'The deployment plan was not confirmed.'
        }
        Write-Host ''
        Write-DemoReadyStatus -Status 'ok' -Message 'Plan confirmed. Starting the deployment.'
    }

    Write-DemoReadyJsonAtomic `
        -Path $ReportPath `
        -Value ([ordered]@{
            version = 1
            generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
            status = 'in-progress'
            phase = 'plan-confirmed'
            environmentName = $EnvironmentName
            environments = $environmentNames
            selections = $selection
            locations = $locations
            external = [ordered]@{
                patriots = [ordered]@{
                    repositoryPath = $optionalCheckoutPaths['patriots']
                    clonedByThisRun = [bool]$optionalCheckoutCreated['patriots']
                }
                tokensAndCredits = [ordered]@{
                    repositoryPath = $optionalCheckoutPaths['tokensAndCredits']
                    clonedByThisRun = [bool]$optionalCheckoutCreated['tokensAndCredits']
                }
            }
        })

    # Authentication, environment validation, and plan confirmation complete before any stop.
    if (Test-Path -LiteralPath $processPath -PathType Leaf) {
        $restartNames = [Collections.Generic.List[string]]::new()
        $restartNames.Add('presenter')
        if ($selection.Demo1) { $restartNames.Add('demo1-comparison') }
        if ($selection.Demo3) { $restartNames.Add('cross-government-coordinate') }
        if ($selection.Patriots) { $restartNames.Add('external-patriots') }
        if ($selection.TokensAndCredits) { $restartNames.Add('tokens-and-credits') }
        & (Join-Path $PSScriptRoot 'Stop-DemoReady.ps1') `
            -StatePath $processPath `
            -OwnedOnly:$false `
            -RequireManagedIdentity `
            -Name @($restartNames)
        $remainingProcessState = Get-Content -LiteralPath $processPath -Raw | ConvertFrom-Json
        $blockedNames = @($remainingProcessState.processes | Where-Object {
            [string]$_.name -cin @($restartNames)
        })
        if ($blockedNames.Count -gt 0) {
            throw 'One or more selected process records could not be safely stopped.'
        }
        $preservedProcesses = @($remainingProcessState.processes)
    }
    $processStateManaged = $true

    $ports = [Collections.Generic.List[int]]::new()
    $ports.Add(5088)
    if ($selection.Demo1) { $ports.Add(5090) }
    if ($selection.Demo3) {
        $ports.Add(5080)
        $ports.Add(7080)
    }
    if ($selection.Patriots) {
        $ports.Add(5081)
        $ports.Add(7081)
    }
    if ($selection.TokensAndCredits) { $ports.Add(5041) }
    Assert-DemoReadyPortsFree -Ports @($ports)
    Assert-DemoReadyNuGetProxy -RepositoryRoot $repositoryRoot

    $demoReadyPhase = 'bundle-resolution'
    Write-DemoReadySection -Title 'Resolve repositories and environments'
    $defra = $null
    if ($selection.Demo2) {
        $defra = Resolve-DemoReadyExternalRepository `
            -Definition $definitions.defra `
            -RepositoryRoot $repositoryRoot `
            -ParameterPath $DefraRepoPath `
            -SetupRepositories $setupRepositories `
            -ParameterAzdEnvironmentName $DefraEnvironmentName
        $contexts.Demo2 = $defra.Path
    }
    $assurance = $null
    $assuranceProject = ''
    if ($selection.Demo3) {
        $assurance = Resolve-DemoReadyExternalRepository `
            -Definition $definitions.assuranceBoard `
            -RepositoryRoot $repositoryRoot `
            -ParameterPath $AssuranceBoardRepoPath `
            -SetupRepositories $setupRepositories `
            -ParameterAzdEnvironmentName $AssuranceBoardEnvironmentName
        $contexts.Council = $assurance.Path
        $assuranceProject = Join-Path $assurance.Path 'src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj'
    }
    $patriotsIntegration = Resolve-DemoReadyPatriots `
        -RepositoryRoot $repositoryRoot `
        -ParameterPath $PatriotsRepoPath `
        -SetupRepositories $setupRepositories `
        -Selected:$selection.Patriots
    if ($null -ne $patriotsIntegration.Repository) {
        $optionalCheckoutPaths['patriots'] = $patriotsIntegration.Repository.Path
        $createdProperty = $patriotsIntegration.Repository.PSObject.Properties['CreatedByThisRun']
        $optionalCheckoutCreated['patriots'] =
            $null -ne $createdProperty -and [bool]$createdProperty.Value
    }
    $tokensIntegration = Resolve-DemoReadyTokensAndCredits `
        -RepositoryRoot $repositoryRoot `
        -ParameterPath $TokensAndCreditsRepoPath `
        -SetupRepositories $setupRepositories `
        -Selected:$selection.TokensAndCredits
    if ($null -ne $tokensIntegration.Repository) {
        $optionalCheckoutPaths['tokensAndCredits'] = $tokensIntegration.Repository.Path
        $createdProperty = $tokensIntegration.Repository.PSObject.Properties['CreatedByThisRun']
        $optionalCheckoutCreated['tokensAndCredits'] =
            $null -ne $createdProperty -and [bool]$createdProperty.Value
    }

    $externalRepositories = @(
        $patriotsIntegration.Repository,
        $tokensIntegration.Repository
    ) | Where-Object { $null -ne $_ }
    $gitBaselines = @{}
    foreach ($repository in $externalRepositories) {
        $baseline = $repository.Identity -ceq 'patriots' `
            ? $patriotsIntegration.GitBaseline `
            : $tokensIntegration.GitBaseline
        $gitBaselines[$repository.Identity] = $baseline
        Write-DemoReadyStep "Resolved $($repository.DisplayName) from $($repository.PathSource)."
    }

    $demoReadyPhase = 'environment-setup'
    if ($selection.Demo1) {
        $null = Initialize-DemoReadyAzdEnvironment `
            $contexts.Demo1 $environmentNames.Demo1 $locations.Demo1 $SubscriptionId $principalId
    }
    if ($selection.Demo2) {
        $null = Initialize-DemoReadyAzdEnvironment `
            $contexts.Demo2 $environmentNames.Demo2 $locations.Demo2 $SubscriptionId $principalId
    }
    if ($selection.Demo3) {
        $null = Initialize-DemoReadyAzdEnvironment `
            $contexts.Council $environmentNames.Demo3 $locations.Demo3 $SubscriptionId $principalId
        Initialize-DemoReadyCouncilGrounding `
            -ContextPath $contexts.Council `
            -EnvironmentName $environmentNames.Demo3 `
            -SensitiveValues $sensitiveValues
    }
    if ($selection.Demo4) {
        $null = Initialize-DemoReadyAzdEnvironment `
            $contexts.Demo4 $environmentNames.Demo4 $locations.Demo4 $SubscriptionId $principalId
    }

    $demo1Values = @{}
    $demo4Values = @{}
    $hostedAgent = $null
    $demo1AppInsights = ''
    $actEndpoint = ''
    $assuranceValues = @{}
    $assuranceConfiguration = $null
    $hostedEndpoint = ''

    if ($selection.Demo4) {
        $demoReadyPhase = 'entra-applications'
        $demo4Application = Initialize-DemoReadyDemo4Application `
            -ContextPath $contexts.Demo4 `
            -AzdEnvironmentName $environmentNames.Demo4 `
            -EnvironmentBaseName $EnvironmentName `
            -RepositoryRoot $repositoryRoot `
            -RuntimeRoot $runtimeRoot `
            -StatePath $entraStatePath `
            -SensitiveValues $sensitiveValues
    }

    if ($selection.Demo1) {
        $demoReadyPhase = 'demo1-deployment'
        Invoke-DemoReadyAzd `
            -Arguments @('provision', '--environment', $environmentNames.Demo1, '--no-prompt') `
            -WorkingDirectory $contexts.Demo1 `
            -LogPath (Join-Path $logRoot 'demo1-provision.log') `
            -SensitiveValues $sensitiveValues
        $demo1Values = Get-DemoReadyAzdValues `
            $contexts.Demo1 $environmentNames.Demo1 $sensitiveValues
        $demoReadyPhase = 'demo1-data'
        $citationIdentityPath = Join-Path $runtimeRoot 'demo1-citation-identity.json'
        Publish-DemoReadyDemo1Agents `
            -RepositoryRoot $repositoryRoot `
            -Demo1Values $demo1Values `
            -CitationIdentityPath $citationIdentityPath
        $demo1Values.DEMO1_CITATION_IDENTITY_PATH = $citationIdentityPath
        Set-DemoReadyAzdValue `
            $contexts.Demo1 `
            $environmentNames.Demo1 `
            'DEMO1_CITATION_IDENTITY_PATH' `
            $citationIdentityPath `
            $sensitiveValues
        $demo1AppInsights = Get-DemoReadyApplicationInsightsConnectionString `
            -ResourceId ([string]$demo1Values.APPLICATIONINSIGHTS_RESOURCE_ID) `
            -RepositoryRoot $repositoryRoot `
            -SensitiveValues $sensitiveValues
        $sensitiveValues.Add($demo1AppInsights)
    }

    if ($selection.Demo2) {
        $demoReadyPhase = 'demo2-deployment'
        Invoke-DemoReadyAzdWithPackageRestoreRetry `
            -Arguments @('up', '--environment', $environmentNames.Demo2, '--no-prompt') `
            -WorkingDirectory $contexts.Demo2 `
            -LogPath (Join-Path $logRoot 'demo2-up.log') `
            -SensitiveValues $sensitiveValues
        $demo2Values = Get-DemoReadyAzdValues `
            $contexts.Demo2 $environmentNames.Demo2 $sensitiveValues
        $actWebUrl = [string]$demo2Values['DEMO2_WEB_URL']
        if ([string]::IsNullOrWhiteSpace($actWebUrl)) {
            throw "The '$($environmentNames.Demo2)' environment does not publish DEMO2_WEB_URL."
        }
        $actUri = [Uri]$actWebUrl
        if ($actUri.Scheme -ne 'https' -or -not [string]::IsNullOrEmpty($actUri.UserInfo)) {
            throw 'The Act web URL must use HTTPS without credentials.'
        }
        $actEndpoint = $actUri.AbsoluteUri.TrimEnd('/')
    }

    if ($selection.Demo3) {
        $demoReadyPhase = 'council-deployment'
        Invoke-DemoReadyAzd `
            -Arguments @('provision', '--environment', $environmentNames.Demo3, '--no-prompt') `
            -WorkingDirectory $contexts.Council `
            -LogPath (Join-Path $logRoot 'council-provision.log') `
            -SensitiveValues $sensitiveValues
        $assuranceValues = Get-DemoReadyAzdValues `
            $contexts.Council $environmentNames.Demo3 $sensitiveValues
        $assuranceConfiguration = Assert-DemoReadyCouncilEnvironment `
            -ContextPath $contexts.Council `
            -Values $assuranceValues
    }

    if ($selection.Demo4) {
        $demoReadyPhase = 'demo4-deployment'
        Invoke-DemoReadyAzdWithPackageRestoreRetry `
            -Arguments @('up', '--environment', $environmentNames.Demo4, '--no-prompt') `
            -WorkingDirectory $contexts.Demo4 `
            -LogPath (Join-Path $logRoot 'demo4-up.log') `
            -SensitiveValues $sensitiveValues
        $demo4Values = Get-DemoReadyAzdValues `
            $contexts.Demo4 $environmentNames.Demo4 $sensitiveValues
        Add-DemoReadyEntraRedirectUri `
            -Application $demo4Application `
            -ApplicationName ([string]$demo4Values.DEMO4_APPLICATION_NAME) `
            -RepositoryRoot $repositoryRoot `
            -SensitiveValues $sensitiveValues
        $hostedAgent = Get-DemoReadyHostedAgent `
            -Name 'demo4-hosted-agent' `
            -EnvironmentName $environmentNames.Demo4 `
            -WorkingDirectory $contexts.Demo4 `
            -SensitiveValues $sensitiveValues
        $hostedEndpoint = "https://$([string]$demo4Values.DEMO4_APPLICATION_NAME).azurewebsites.net/"
    }

    $demoReadyPhase = 'local-build'
    Write-DemoReadySection -Title 'Build the local applications'
    $presenterProject = New-DemoReadyPresenterProject `
        -RepositoryRoot $repositoryRoot `
        -RuntimeRoot $runtimeRoot `
        -Selection $selection
    if ($selection.Demo1) {
        Invoke-DemoReadyProjectBuild `
            -RepositoryPath $repositoryRoot `
            -ProjectPath $demo1WebProject `
            -LogRoot $logRoot `
            -LogName 'demo1-comparison' `
            -SensitiveValues $sensitiveValues
    }
    if ($selection.Demo3) {
        Invoke-DemoReadyProjectBuild `
            -RepositoryPath $assurance.Path `
            -ProjectPath $assuranceProject `
            -LogRoot $logRoot `
            -LogName 'council' `
            -SensitiveValues $sensitiveValues
    }
    Invoke-DemoReadyProjectBuild `
        -RepositoryPath $repositoryRoot `
        -ProjectPath $presenterProject `
        -LogRoot $logRoot `
        -LogName 'presenter' `
        -SensitiveValues $sensitiveValues

    $demoReadyPhase = 'local-processes'
    Write-DemoReadySection -Title 'Start the local applications'
    if ($selection.Demo1) {
        $demo1WebEnvironment = Get-DemoReadyDemo1WebEnvironment `
            -Demo1Values $demo1Values `
            -ApplicationInsightsConnectionString $demo1AppInsights
        $processes.Add((Start-DemoReadyProcess `
            -Name 'demo1-comparison' `
            -FilePath 'dotnet' `
            -Arguments @(
                'run', '--project', $demo1WebProject,
                '--no-build', '--no-restore', '--no-launch-profile'
            ) `
            -WorkingDirectory $repositoryRoot `
            -Environment $demo1WebEnvironment `
            -LogDirectory $logRoot `
            -CommandMarker $demo1WebProject `
            -Endpoints @($demo1WebUrl) `
            -ScriptRoot $PSScriptRoot `
            -SensitiveValues $sensitiveValues))
    }

    if ($selection.Demo3) {
        $assuranceEnvironmentValues = @{}
        foreach ($entry in $assuranceValues.GetEnumerator()) {
            $assuranceEnvironmentValues[$entry.Key] = [string]$entry.Value
        }
        $assuranceConnection = [string]$assuranceEnvironmentValues['APPINSIGHTS_CONNECTION_STRING']
        if (-not [string]::IsNullOrWhiteSpace($assuranceConnection)) {
            $sensitiveValues.Add($assuranceConnection)
        }
        $assuranceEnvironmentValues.ASPNETCORE_URLS = 'https://localhost:7080;http://localhost:5080'
        $assuranceEnvironmentValues.AZURE_TOKEN_CREDENTIALS = 'AzureCliCredential'
        $processes.Add((Start-DemoReadyProcess `
            -Name 'cross-government-coordinate' `
            -FilePath 'dotnet' `
            -Arguments @(
                'run', '--project', $assuranceProject,
                '--no-build', '--no-restore', '--no-launch-profile'
            ) `
            -WorkingDirectory $assurance.Path `
            -Environment $assuranceEnvironmentValues `
            -LogDirectory $logRoot `
            -CommandMarker $assuranceProject `
            -Endpoints @($assuranceHttpUrl, $assuranceHttpsUrl) `
            -ScriptRoot $PSScriptRoot `
            -SensitiveValues $sensitiveValues))
    }

    Start-DemoReadyPatriots `
        -Integration $patriotsIntegration `
        -RepositoryRoot $repositoryRoot `
        -LogRoot $logRoot `
        -ScriptRoot $PSScriptRoot `
        -Processes $processes `
        -PreservedProcesses $preservedProcesses `
        -SensitiveValues $sensitiveValues
    Start-DemoReadyTokensAndCredits `
        -Integration $tokensIntegration `
        -RepositoryRoot $repositoryRoot `
        -LogRoot $logRoot `
        -ScriptRoot $PSScriptRoot `
        -Processes $processes `
        -PreservedProcesses $preservedProcesses `
        -SensitiveValues $sensitiveValues `
        -Required:$selection.TokensAndCredits

    $demoReadyPhase = 'presenter-catalog'
    $presenterContext = New-DemoReadyPresenterContext `
        -Demo1Values $demo1Values `
        -Demo4Values $demo4Values `
        -HostedAgent $hostedAgent `
        -ActWebUrl $actEndpoint `
        -AssuranceDossierPath $assuranceDossierPath `
        -AssuranceHttpUrl $assuranceHttpUrl `
        -AssuranceHttpsUrl $assuranceHttpsUrl `
        -PatriotsHttpUrl $patriotsHttpUrl `
        -PatriotsHttpsUrl $patriotsHttpsUrl `
        -PatriotsDossierPath $patriotsDossierPath `
        -PatriotsConfigured:($patriotsIntegration.Status -ceq 'ready') `
        -TokensAndCreditsStatus $tokensIntegration.Status `
        -Selection $selection `
        -RepositoryRoot $repositoryRoot
    $catalog = New-DemoReadyCatalog `
        -SourcePath (Join-Path $repositoryRoot 'config\presenter\sessions.v1.json') `
        -DestinationPath $catalogPath `
        -Context $presenterContext

    $presenterEnvironment = @{
        PRESENTER_SESSION_CATALOG_PATH = $catalogPath
        OTEL_SERVICE_NAME = 'PublicSectorAgentDemos.Presenter'
        AZURE_TOKEN_CREDENTIALS = 'AzureCliCredential'
    }
    if (-not [string]::IsNullOrWhiteSpace($demo1AppInsights)) {
        $presenterEnvironment.APPLICATIONINSIGHTS_CONNECTION_STRING = $demo1AppInsights
    }
    $processes.Add((Start-DemoReadyProcess `
        -Name 'presenter' `
        -FilePath 'dotnet' `
        -Arguments @(
            'run', '--project', $presenterProject,
            '--no-build', '--no-restore', '--no-launch-profile'
        ) `
        -WorkingDirectory $repositoryRoot `
        -Environment $presenterEnvironment `
        -LogDirectory $logRoot `
        -CommandMarker 'PublicSectorAgentDemos.Presenter' `
        -Endpoints @($presenterUrl) `
        -ScriptRoot $PSScriptRoot `
        -SensitiveValues $sensitiveValues))

    $demoReadyPhase = 'readiness'
    Write-DemoReadySection -Title 'Wait for every endpoint'
    if ($selection.Demo1) {
        Wait-DemoReadyEndpoint "$($demo1WebUrl)health" -RequireSuccess
    }
    if ($selection.Demo2) {
        Wait-DemoReadyEndpoint "$actEndpoint/health"
    }
    if ($selection.Demo3) {
        Wait-DemoReadyEndpoint $assuranceHttpUrl -TimeoutSeconds 600
        Wait-DemoReadyEndpoint $assuranceHttpsUrl
        Wait-DemoReadyCouncilInitialization `
            -LogDirectory $logRoot `
            -GroundingProvider ([string]$assuranceValues['COUNCIL_GROUNDING_PROVIDER'])
    }
    if ($selection.Demo4) {
        Wait-DemoReadyEndpoint "$($hostedEndpoint)health"
    }
    Wait-DemoReadyEndpoint $presenterUrl

    Write-DemoReadyJsonAtomic `
        -Path $processPath `
        -Value (ConvertTo-DemoReadyProcessFile `
            -Processes $processes `
            -PreservedProcesses $preservedProcesses)

    $demoReadyPhase = 'final-integrity'
    foreach ($repository in $externalRepositories) {
        Assert-DemoReadyGitStatusPreserved `
            -RepositoryPath $repository.Path `
            -Baseline $gitBaselines[$repository.Identity] `
            -DisplayName $repository.DisplayName
    }
    Assert-DemoReadyGitStatusPreserved `
        -RepositoryPath $repositoryRoot `
        -Baseline $repositoryBaseline `
        -DisplayName 'demo (PublicSectorAgentDemos)'

    $configuredSessions = @($catalog.sessions | Where-Object { $_.configured -ne $false }).Count
    $ownedDeploymentCount = @(
        @(
            $selection.Demo1,
            $selection.Demo2,
            $selection.Demo3,
            $selection.Demo4
        ) | Where-Object { $_ }
    ).Count
    $report = [ordered]@{
        version = 1
        generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
        status = 'ready'
        environmentName = $EnvironmentName
        environments = $environmentNames
        selections = $selection
        locations = $locations
        modelCapacity = [ordered]@{
            deploymentCount = $capacityPlan.DeploymentCount
            aggregateCapacityQuotaUnits = $capacityPlan.AggregateCapacity
            deployments = $capacityPlan.Deployments
        }
        external = [ordered]@{
            patriots = [ordered]@{
                status = $patriotsIntegration.Status
                repositoryPath = $null -eq $patriotsIntegration.Repository `
                    ? $null `
                    : $patriotsIntegration.Repository.Path
                managedByThisRepository = $selection.Patriots
                clonedByThisRun = [bool]$optionalCheckoutCreated['patriots']
                deployedByThisRepository = $false
                gitStatusPreserved = $patriotsIntegration.GitStatusPreserved
            }
            tokensAndCredits = [ordered]@{
                status = $tokensIntegration.Status
                repositoryPath = $null -eq $tokensIntegration.Repository `
                    ? $null `
                    : $tokensIntegration.Repository.Path
                managedByThisRepository = $selection.TokensAndCredits
                clonedByThisRun = [bool]$optionalCheckoutCreated['tokensAndCredits']
                deployedByThisRepository = $false
                gitStatusPreserved = $tokensIntegration.GitStatusPreserved
            }
        }
        endpoints = [ordered]@{
            demo1Comparison = $selection.Demo1 ? $demo1WebUrl : $null
            act = $selection.Demo2 ? "$actEndpoint/" : $null
            crossGovernmentCoordinate = $selection.Demo3 ? $assuranceHttpUrl : $null
            crossGovernmentCoordinateHttps = $selection.Demo3 ? $assuranceHttpsUrl : $null
            hosted = $selection.Demo4 ? $hostedEndpoint : $null
            patriots = $patriotsIntegration.Status -ceq 'ready' ? $patriotsHttpUrl : $null
            patriotsHttps = $patriotsIntegration.Status -ceq 'ready' ? $patriotsHttpsUrl : $null
            tokensAndCredits = $tokensIntegration.Status -ceq 'ready' ? 'http://localhost:5041/' : $null
            presenter = $presenterUrl
        }
        presenter = [ordered]@{
            catalogPath = $catalogPath
            sessionCount = @($catalog.sessions).Count
            configuredSessionCount = $configuredSessions
            extraCount = @($catalog.extras).Count
            ownedDeploymentCount = $ownedDeploymentCount
            requiredSessionCount = $configuredSessions
        }
        legacyFullDeployment = [ordered]@{
            requiredSessionCount = 5
            ownedDeploymentCount = 4
        }
        liveScenario = 'not-verified'
        repositoryStatusPreserved = $true
        validationCommand = 'scripts\Test-DemoReady.ps1'
    }
    Write-DemoReadyJsonAtomic -Path $ReportPath -Value $report

    Write-DemoReadyBanner `
        -Title 'Demo ready' `
        -Lines @("Open the Presenter at $presenterUrl")
    Write-Host ''
    Write-Host '  Endpoints' -ForegroundColor White
    $endpointLabels = [ordered]@{
        demo1Comparison = 'Demo 1 - Foundation and Ground'
        act = 'Demo 2 - Act'
        crossGovernmentCoordinate = 'Demo 3 - Council (HTTP)'
        crossGovernmentCoordinateHttps = 'Demo 3 - Council (HTTPS)'
        hosted = 'Demo 4 - Hosted'
        patriots = 'Patriots (HTTP)'
        patriotsHttps = 'Patriots (HTTPS)'
        tokensAndCredits = 'Tokens and Credits'
        presenter = 'Presenter'
    }
    $endpointRows = [Collections.Generic.List[object]]::new()
    foreach ($entry in $report.endpoints.GetEnumerator()) {
        if ([string]::IsNullOrWhiteSpace([string]$entry.Value)) {
            continue
        }
        $label = $endpointLabels.Contains($entry.Key) `
            ? [string]$endpointLabels[$entry.Key] `
            : [string]$entry.Key
        $endpointRows.Add(@($label, [string]$entry.Value))
    }
    Write-DemoReadyTable -Headers @('Session', 'URL') -Rows @($endpointRows)
    Write-Host ''
    Write-DemoReadyStatus -Status 'ok' -Message "Readiness report: $ReportPath"
    Write-DemoReadyStatus -Status 'info' -Message 'Stop everything with scripts\Stop-DemoReady.ps1.'
    Write-DemoReadyStatus `
        -Status 'info' `
        -Message 'Run scripts\Test-DemoReady.ps1 for tests, builds, and deep validation.'
    Write-Host ''
}
catch {
    if ($processStateManaged) {
        Write-DemoReadyJsonAtomic `
            -Path $processPath `
            -Value (ConvertTo-DemoReadyProcessFile `
                -Processes $processes `
                -PreservedProcesses $preservedProcesses)
    }
    Write-DemoReadyJsonAtomic `
        -Path $ReportPath `
        -Value ([ordered]@{
            version = 1
            generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
            status = 'failed'
            phase = $demoReadyPhase
            environmentName = $EnvironmentName
            environments = $environmentNames
            selections = $selection
            locations = $locations
            external = [ordered]@{
                patriots = [ordered]@{
                    repositoryPath = $optionalCheckoutPaths['patriots']
                    clonedByThisRun = [bool]$optionalCheckoutCreated['patriots']
                }
                tokensAndCredits = [ordered]@{
                    repositoryPath = $optionalCheckoutPaths['tokensAndCredits']
                    clonedByThisRun = [bool]$optionalCheckoutCreated['tokensAndCredits']
                }
            }
            error = 'The demo-ready phase failed. Review the masked logs.'
        })
    Write-Host ''
    Write-DemoReadyStatus -Status 'no' -Message "The '$demoReadyPhase' phase failed." -MessageColor 'Red'
    Write-DemoReadyStatus -Status 'info' -Message "Masked logs: $logRoot"
    Write-DemoReadyStatus -Status 'info' -Message "Failure report: $ReportPath"
    Write-Host ''
    throw
}
