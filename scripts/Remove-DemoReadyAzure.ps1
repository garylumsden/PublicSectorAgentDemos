[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [ValidatePattern('^[a-z][a-z0-9-]{1,18}[a-z0-9]$')]
    [string]$EnvironmentName = 'psad-demos',

    [switch]$Demo1,
    [switch]$Demo2,
    [switch]$Demo3,
    [switch]$Demo4,
    [switch]$All,

    [ValidatePattern('^[a-z][a-z0-9-]{0,62}[a-z0-9]$')]
    [string]$Demo1EnvironmentName,

    [ValidatePattern('^[a-z][a-z0-9-]{0,62}[a-z0-9]$')]
    [string]$Demo2EnvironmentName,

    [ValidatePattern('^[a-z][a-z0-9-]{0,62}[a-z0-9]$')]
    [string]$Demo3EnvironmentName,

    [ValidatePattern('^[a-z][a-z0-9-]{0,62}[a-z0-9]$')]
    [string]$Demo4EnvironmentName,

    [Alias('KeepOptionalRepositories')]
    [switch]$KeepPatriotsAndTokensAndCredits,

    [switch]$NonInteractive,

    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'DemoReady\Common.ps1')
. (Join-Path $PSScriptRoot 'DemoReady\Console.ps1')
. (Join-Path $PSScriptRoot 'DemoReady\External.ps1')
. (Join-Path $PSScriptRoot 'DemoReady\Guided.ps1')
. (Join-Path $PSScriptRoot 'DemoReady\Owned.ps1')

if ($MyInvocation.InvocationName -eq '.') {
    return
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$runtimeRoot = Join-Path $repositoryRoot '.demo-ready'
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
$report = (Test-Path -LiteralPath $ReportPath -PathType Leaf) `
    ? (Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json) `
    : $null
$reportEnvironmentProperty = $null -eq $report `
    ? $null `
    : $report.PSObject.Properties['environmentName']
$reportEnvironment = $null -eq $reportEnvironmentProperty `
    ? '' `
    : [string]$reportEnvironmentProperty.Value
if (-not $PSBoundParameters.ContainsKey('EnvironmentName') -and
    -not [string]::IsNullOrWhiteSpace($reportEnvironment)) {
    $EnvironmentName = $reportEnvironment
}
$environmentNames = Get-DemoReadyEnvironmentNames -BaseName $EnvironmentName
$subscriptionSummary = Get-DemoReadyAzureSubscriptionSummary -RepositoryRoot $repositoryRoot
$reportSubscriptionProperty = $null -eq $report `
    ? $null `
    : $report.PSObject.Properties['subscriptionId']
$reportSubscriptionId = $null -eq $reportSubscriptionProperty `
    ? '' `
    : [string]$reportSubscriptionProperty.Value
$reportMatchesEnvironment = -not [string]::IsNullOrWhiteSpace($reportEnvironment) -and
    $reportEnvironment -ceq $EnvironmentName -and
    -not [string]::IsNullOrWhiteSpace($reportSubscriptionId) -and
    $reportSubscriptionId -ceq $subscriptionSummary.Id
$interactive = -not $NonInteractive -and (Test-DemoReadyInteractiveConsole)
$hasSelection = $Demo1 -or $Demo2 -or $Demo3 -or $Demo4 -or $All
$selection = [ordered]@{
    Demo1 = [bool]($All -or $Demo1)
    Demo2 = [bool]($All -or $Demo2)
    Demo3 = [bool]($All -or $Demo3)
    Demo4 = [bool]($All -or $Demo4)
}
$selectionsProperty = $null -eq $report -or -not $reportMatchesEnvironment `
    ? $null `
    : $report.PSObject.Properties['selections']
$recordedSelection = [ordered]@{
    Demo1 = $false
    Demo2 = $false
    Demo3 = $false
    Demo4 = $false
}
if ($null -ne $selectionsProperty) {
    foreach ($key in @($recordedSelection.Keys)) {
        $property = $selectionsProperty.Value.PSObject.Properties[$key]
        if ($null -ne $property) {
            $recordedSelection[$key] = [bool]$property.Value
        }
    }
}
Write-DemoReadyBanner `
    -Title 'Public Sector Agent Demos - teardown' `
    -Lines @(
        'Stops local applications and removes selected owned or startup-created Azure deployments.',
        ($interactive `
            ? 'Guided teardown. Nothing is removed until you confirm the complete plan.' `
            : 'Selection supplied by arguments or the readiness report. Review the plan below.')
    )
Write-DemoReadyTeardownSection -Key 'Subscription'
Write-DemoReadyField -Label 'Subscription name' -Value $subscriptionSummary.Name
Write-DemoReadyField -Label 'Subscription ID' -Value $subscriptionSummary.DisplayId
Write-DemoReadyField -Label 'Environment base' -Value $EnvironmentName
Write-DemoReadyStatus `
    -Status ($reportMatchesEnvironment ? 'ok' : 'warn') `
    -Message ($reportMatchesEnvironment `
        ? 'A matching readiness report supplies the latest startup state.' `
        : 'No matching readiness report exists. Select the Azure deployments explicitly.')
if ($interactive) {
    Write-Host ''
    if (-not (Read-DemoReadyYesNo `
        -Prompt 'Is this the correct Azure subscription and environment?' `
        -Default $false)) {
        throw 'Select the required Azure subscription or environment, then run teardown again.'
    }
}

if (-not $hasSelection) {
    if ($interactive) {
        $selection = Read-DemoReadyTeardownSelection -Defaults $recordedSelection
    }
    elseif ($null -eq $selectionsProperty) {
        throw 'No matching readiness selection exists. Specify -Demo1, -Demo2, -Demo3, -Demo4, or -All.'
    }
    else {
        $selection = $recordedSelection
    }
}
if ($hasSelection -or -not $interactive) {
    Write-DemoReadyTeardownSection -Key 'Selection'
    foreach ($key in @($selection.Keys)) {
        Write-DemoReadyStatus `
            -Status ($selection[$key] ? 'ok' : 'pending') `
            -Message ($selection[$key] `
                ? "Remove $((Get-DemoReadyDemoLabel)[$key])." `
                : "Keep $((Get-DemoReadyDemoLabel)[$key]).") `
            -MessageColor ($selection[$key] ? '' : 'DarkGray')
    }
}

$keepOptionalCheckouts = [bool]$KeepPatriotsAndTokensAndCredits
Write-DemoReadyTeardownSection -Key 'Optional'
if ($PSBoundParameters.ContainsKey('KeepPatriotsAndTokensAndCredits')) {
    Write-DemoReadyStatus -Status 'ok' -Message 'Keep Patriots and Tokens and Credits on disk.'
    Write-DemoReadyStatus -Status 'info' -Message 'Disk retention does not retain a startup-created optional Azure environment.'
}
elseif ($interactive) {
    $keepOptionalCheckouts = Read-DemoReadyYesNo `
        -Prompt 'Keep Patriots and Tokens and Credits on disk?' `
        -Default $true
}
else {
    Write-DemoReadyStatus `
        -Status 'warn' `
        -Message 'Remove only optional checkouts that startup cloned and that are fully pushed.'
}
$ownedContexts = @(
    [pscustomobject]@{
        Key = 'Demo1'
        Name = 'Demo 1 - Foundation and Ground'
        Path = Join-Path $repositoryRoot 'deploy\demo1'
        Environment = $Demo1EnvironmentName ? $Demo1EnvironmentName : $environmentNames.Demo1
    },
    [pscustomobject]@{
        Key = 'Demo2'
        Name = 'Demo 2 - Act'
        Path = Join-Path $repositoryRoot 'src\PublicSectorAgentDemos.Demo2.Act'
        Environment = $Demo2EnvironmentName ? $Demo2EnvironmentName : $environmentNames.Demo2
    },
    [pscustomobject]@{
        Key = 'Demo3'
        Name = 'Demo 3 - Cross-Government Council'
        Path = Join-Path $repositoryRoot 'src\PublicSectorAgentDemos.Demo3.Coordinate'
        Environment = $Demo3EnvironmentName ? $Demo3EnvironmentName : $environmentNames.Demo3
    },
    [pscustomobject]@{
        Key = 'Demo4'
        Name = 'Demo 4 - Hosted'
        Path = Join-Path $repositoryRoot 'deploy\demo4'
        Environment = $Demo4EnvironmentName ? $Demo4EnvironmentName : $environmentNames.Demo4
    }
) | Where-Object { $selection[$_.Key] }
$optionalAzureContexts = Get-DemoReadyOptionalAzureTeardownContexts `
    -Report $report `
    -ReportMatchesEnvironment $reportMatchesEnvironment
$definitions = Get-DemoReadyExternalRepositoryDefinition
foreach ($context in $optionalAzureContexts) {
    $definition = [string]$context.Key -ceq 'patriots' `
        ? $definitions.patriots `
        : $definitions.tokensAndCredits
    Assert-DemoReadyExternalRepositoryCheckout `
        -Path $context.Path `
        -Definition $definition
}
$contexts = @($ownedContexts) + @($optionalAzureContexts)

function Remove-DemoReadyOwnedEntraApplication {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$StatePath,
        [Parameter(Mandatory)][string]$EnvironmentBaseName
    )

    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
        Write-DemoReadyStatus -Status 'pending' -Message 'No Demo 4 Microsoft Entra application state exists.'
        return
    }
    $state = Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json
    $application = $state.applications.demo4Api
    $expectedMarker = "public-sector-agent-demos:demo-ready:v1:${EnvironmentBaseName}:demo4-api"
    if ([string]$application.ownershipMarker -cne $expectedMarker -or
        [string]$application.objectId -notmatch '^[0-9a-fA-F-]{36}$' -or
        [string]$application.appId -notmatch '^[0-9a-fA-F-]{36}$') {
        throw 'The Demo 4 Microsoft Entra application state has no valid ownership proof.'
    }

    $json = & az ad app list `
        --filter "appId eq '$([string]$application.appId)'" `
        --output json `
        --only-show-errors
    if ($LASTEXITCODE -ne 0) {
        throw 'Microsoft Entra could not confirm whether the owned Demo 4 application exists. The ownership state was retained.'
    }
    $matches = @($json | ConvertFrom-Json)
    if ($matches.Count -eq 0) {
        Write-DemoReadyStatus -Status 'pending' -Message 'The owned Demo 4 Microsoft Entra application is already absent.'
        Remove-Item -LiteralPath $StatePath -Force
        return
    }
    if ($matches.Count -ne 1) {
        throw 'Microsoft Entra returned an ambiguous result for the owned Demo 4 application.'
    }
    $remote = $matches[0]
    if ([string]$remote.id -cne [string]$application.objectId -or
        [string]$remote.appId -cne [string]$application.appId -or
        [string]$remote.notes -cne $expectedMarker) {
        throw 'The Demo 4 Microsoft Entra application does not match the persisted ownership proof.'
    }
    & az ad app delete --id ([string]$application.appId)
    if ($LASTEXITCODE -ne 0) {
        throw 'The owned Demo 4 Microsoft Entra application could not be deleted.'
    }
    Remove-Item -LiteralPath $StatePath -Force
    Write-DemoReadyStatus -Status 'ok' -Message 'Removed the owned Demo 4 Microsoft Entra application.'
}

function Remove-DemoReadyClonedOptionalRepository {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Entry,
        [Parameter(Mandatory)][string]$FolderName,
        [Parameter(Mandatory)][string]$RepositoryUrl,
        [Parameter(Mandatory)][string]$RepositoriesRoot
    )

    $cloneProperty = $Entry.PSObject.Properties['clonedByThisRun']
    if ($null -eq $cloneProperty -or -not [bool]$cloneProperty.Value) {
        return
    }
    $path = [IO.Path]::GetFullPath([string]$Entry.repositoryPath)
    $expectedPath = [IO.Path]::GetFullPath((Join-Path $RepositoriesRoot $FolderName))
    if (-not $path.Equals($expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove the optional checkout at unexpected path '$path'."
    }
    if (-not (Test-Path -LiteralPath $path -PathType Container)) {
        Write-DemoReadyStatus -Status 'pending' -Message "The optional checkout '$FolderName' is already absent."
        return
    }
    $directory = Get-Item -LiteralPath $path -Force
    if (($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The optional checkout '$path' is a linked directory and was retained."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $path '.git') -PathType Container)) {
        throw "The optional checkout '$path' is not a Git repository."
    }
    $origin = (& git -C $path remote get-url origin).Trim()
    $normalizedOrigin = ($origin -replace '\.git/?$', '').TrimEnd('/')
    $normalizedExpected = ($RepositoryUrl -replace '\.git/?$', '').TrimEnd('/')
    if ($LASTEXITCODE -ne 0 -or $normalizedOrigin -ine $normalizedExpected) {
        throw "The optional checkout '$path' has an unexpected origin."
    }
    $status = & git -C $path status --porcelain
    if ($LASTEXITCODE -ne 0) {
        throw "Git could not verify the optional checkout '$path', so it was retained."
    }
    if (-not [string]::IsNullOrWhiteSpace($status)) {
        throw "The optional checkout '$path' has uncommitted changes and was retained."
    }
    $localRefs = @(& git -C $path for-each-ref --format='%(objectname)|%(refname)' refs/heads refs/tags)
    if ($LASTEXITCODE -ne 0) {
        throw "Git could not inspect every local ref in '$path', so the checkout was retained."
    }
    $remoteRefLines = @(& git -C $path ls-remote --heads --tags origin)
    if ($LASTEXITCODE -ne 0) {
        throw "Git could not inspect the origin refs for '$path', so the checkout was retained."
    }
    $remoteRefs = @{}
    foreach ($line in $remoteRefLines) {
        if ($line -match '^([0-9a-fA-F]{40})\s+(.+)$' -and $matches[2] -notlike '*^{}') {
            $remoteRefs[$matches[2]] = $matches[1]
        }
    }
    foreach ($entry in $localRefs) {
        $parts = $entry -split '\|', 2
        $objectId = $parts[0]
        $ref = $parts[1]
        $remoteRef = $ref.StartsWith('refs/heads/', [StringComparison]::Ordinal) `
            ? "refs/heads/$($ref.Substring('refs/heads/'.Length))" `
            : $ref
        if (-not $remoteRefs.ContainsKey($remoteRef) -or
            [string]$remoteRefs[$remoteRef] -cne $objectId) {
            throw "The optional checkout '$path' has local ref '$ref' that is not identically pushed to origin and was retained."
        }
    }
    Remove-Item -LiteralPath $path -Recurse -Force
    Write-DemoReadyStatus -Status 'ok' -Message "Removed the cloned optional checkout '$FolderName'."
}

function Remove-DemoReadySoftDeletedResources {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$EnvironmentName,
        [Parameter(Mandatory)][string]$ResourceGroupName,
        [Parameter(Mandatory)][string]$WorkingDirectory
    )

    $deletedAccounts = Invoke-DemoReadyNative `
        -FilePath 'az' `
        -Arguments @('cognitiveservices', 'account', 'list-deleted', '--output', 'json') `
        -WorkingDirectory $WorkingDirectory `
        -CaptureOutput `
        -Quiet | ConvertFrom-Json
    foreach ($account in @($deletedAccounts)) {
        $deletedResourceGroup = ''
        if ([string]$account.id -match '/resourceGroups/([^/]+)/') {
            $deletedResourceGroup = $matches[1]
        }
        $tags = $account.PSObject.Properties['tags']
        $azdEnvironment = $null -eq $tags ? '' : [string]$tags.Value.'azd-env-name'
        if ($deletedResourceGroup -ine $ResourceGroupName -or
            $azdEnvironment -cne $EnvironmentName) {
            continue
        }
        Write-DemoReadyStatus -Status 'step' -Message "Purging deleted AI account '$($account.name)'."
        Invoke-DemoReadyNative `
            -FilePath 'az' `
            -Arguments @(
                'cognitiveservices', 'account', 'purge',
                '--name', [string]$account.name,
                '--location', [string]$account.location,
                '--resource-group', $deletedResourceGroup
            ) `
            -WorkingDirectory $WorkingDirectory `
            -Quiet
        Write-DemoReadyStatus -Status 'ok' -Message "Purged deleted AI account '$($account.name)'."
    }

    $deletedVaults = Invoke-DemoReadyNative `
        -FilePath 'az' `
        -Arguments @('keyvault', 'list-deleted', '--output', 'json') `
        -WorkingDirectory $WorkingDirectory `
        -CaptureOutput `
        -Quiet | ConvertFrom-Json
    foreach ($vault in @($deletedVaults)) {
        $properties = $vault.PSObject.Properties['properties']
        $tags = $null -eq $properties ? $null : $properties.Value.PSObject.Properties['tags']
        $azdEnvironment = $null -eq $tags ? '' : [string]$tags.Value.'azd-env-name'
        $vaultId = $null -eq $properties ? '' : [string]$properties.Value.vaultId
        $deletedResourceGroup = $vaultId -match '/resourceGroups/([^/]+)/' ? $matches[1] : ''
        if ($deletedResourceGroup -ine $ResourceGroupName -or
            $azdEnvironment -cne $EnvironmentName) {
            continue
        }
        $location = $null -eq $properties `
            ? [string]$vault.location `
            : [string]$properties.Value.location
        Write-DemoReadyStatus -Status 'step' -Message "Purging deleted Key Vault '$($vault.name)'."
        Invoke-DemoReadyNative `
            -FilePath 'az' `
            -Arguments @('keyvault', 'purge', '--name', [string]$vault.name, '--location', $location) `
            -WorkingDirectory $WorkingDirectory `
            -Quiet
        Write-DemoReadyStatus -Status 'ok' -Message "Purged deleted Key Vault '$($vault.name)'."
    }
}

foreach ($context in $contexts) {
    if (-not (Test-Path -LiteralPath $context.Path -PathType Container)) {
        throw "The $($context.Name) context path '$($context.Path)' does not exist. " +
            'Fix the deployment layout before tearing down Azure resources.'
    }
    $contextAzureYamlPath = Join-Path $context.Path 'azure.yaml'
    if (-not (Test-Path -LiteralPath $contextAzureYamlPath -PathType Leaf)) {
        throw "The $($context.Name) context path '$($context.Path)' has no 'azure.yaml'. " +
            'Fix the deployment layout before tearing down Azure resources.'
    }
}

$processNames = [Collections.Generic.List[string]]::new()
$processNames.Add('presenter')
if ($selection.Demo1) { $processNames.Add('demo1-comparison') }
if ($selection.Demo3) { $processNames.Add('cross-government-coordinate') }
if ($reportMatchesEnvironment -and $null -ne $selectionsProperty) {
    $patriotsSelection = $selectionsProperty.Value.PSObject.Properties['Patriots']
    $tokensSelection = $selectionsProperty.Value.PSObject.Properties['TokensAndCredits']
    if ($null -ne $patriotsSelection -and [bool]$patriotsSelection.Value) {
        $processNames.Add('external-patriots')
    }
    if ($null -ne $tokensSelection -and [bool]$tokensSelection.Value) {
        $processNames.Add('tokens-and-credits')
    }
}

$selectedNames = @($contexts | ForEach-Object { $_.Name }) -join ', '
if ([string]::IsNullOrWhiteSpace($selectedNames)) {
    $selectedNames = 'no owned Azure deployments'
}
$identityAction = $selection.Demo4 `
    ? 'remove the owned Demo 4 Microsoft Entra application' `
    : 'keep the Demo 4 Microsoft Entra application'
$optionalAction = $keepOptionalCheckouts `
    ? 'keep optional checkouts' `
    : 'remove optional checkouts cloned by the recorded startup run'
Show-DemoReadyTeardownPlan `
    -Selection $selection `
    -Contexts @($contexts) `
    -ProcessNames @($processNames) `
    -Subscription $subscriptionSummary `
    -KeepOptionalCheckouts $keepOptionalCheckouts `
    -Report ($reportMatchesEnvironment ? $report : $null)
if ($interactive -and -not $WhatIfPreference) {
    Write-Host ''
    if (-not (Read-DemoReadyYesNo `
        -Prompt 'Continue with this teardown plan?' `
        -Default $false)) {
        throw 'The teardown plan was not confirmed.'
    }
    Write-Host ''
    Write-DemoReadyStatus -Status 'ok' -Message 'Plan confirmed. Starting teardown.'
}

$originalConfirmPreference = $ConfirmPreference
if (($interactive -or $NonInteractive) -and -not $PSBoundParameters.ContainsKey('Confirm')) {
    $ConfirmPreference = 'None'
}
try {
    $approved = $PSCmdlet.ShouldProcess(
        $EnvironmentName,
        "stop local applications; remove $selectedNames; $identityAction; $optionalAction")
}
finally {
    $ConfirmPreference = $originalConfirmPreference
}
if (-not $approved) {
    return
}

$processPath = Join-Path $runtimeRoot 'processes.json'
& (Join-Path $PSScriptRoot 'Stop-DemoReady.ps1') `
    -StatePath $processPath `
    -OwnedOnly:$false `
    -RequireManagedIdentity `
    -Name @($processNames)
if (Test-Path -LiteralPath $processPath -PathType Leaf) {
    $remainingState = Get-Content -LiteralPath $processPath -Raw | ConvertFrom-Json
    $remainingRequested = @($remainingState.processes | Where-Object {
        [string]$_.name -cin $processNames
    })
    if ($remainingRequested.Count -gt 0) {
        $names = @($remainingRequested | ForEach-Object { [string]$_.name }) -join ', '
        throw "Teardown retained process records that did not stop safely: $names."
    }
}

Write-DemoReadySection -Title 'Remove Azure deployments'
foreach ($context in $contexts) {
    $resourceGroupName = "rg-$($context.Environment)"
    Write-DemoReadyStatus -Status 'step' -Message "Removing $($context.Name) environment '$($context.Environment)'."
    $resourceGroupExists = Invoke-DemoReadyNative `
        -FilePath 'az' `
        -Arguments @('group', 'exists', '--name', $resourceGroupName, '--output', 'tsv') `
        -WorkingDirectory $context.Path `
        -CaptureOutput `
        -Quiet
    if ([string]$resourceGroupExists.Trim() -ceq 'true') {
        Push-Location $context.Path
        try {
            & azd down `
                --environment $context.Environment `
                --purge `
                --force `
                --no-prompt
            if ($LASTEXITCODE -ne 0) {
                throw "azd down failed for '$($context.Environment)' with exit code $LASTEXITCODE."
            }
        }
        finally {
            Pop-Location
        }
    }
    else {
        Write-DemoReadyStatus `
            -Status 'pending' `
            -Message "Resource group '$resourceGroupName' is already absent. Checking soft-deleted resources."
    }
    Remove-DemoReadySoftDeletedResources `
        -EnvironmentName $context.Environment `
        -ResourceGroupName $resourceGroupName `
        -WorkingDirectory $context.Path
    Write-DemoReadyStatus -Status 'ok' -Message "Removed and purged $($context.Name)."
}

if ($selection.Demo4) {
    Write-DemoReadySection -Title 'Remove owned identity'
    Remove-DemoReadyOwnedEntraApplication `
        -StatePath (Join-Path $runtimeRoot "entra-apps.$EnvironmentName.json") `
        -EnvironmentBaseName $EnvironmentName
}

Write-DemoReadySection -Title 'Clean local startup state'
if ($keepOptionalCheckouts) {
    Write-DemoReadyStatus -Status 'info' -Message 'Kept Patriots and Tokens and Credits on disk.'
}
elseif ($reportMatchesEnvironment -and $null -ne $report -and $null -ne $report.external) {
    $repositoriesRoot = Split-Path -Parent $repositoryRoot
    if ($null -ne $report.external.patriots) {
        Remove-DemoReadyClonedOptionalRepository `
            -Entry $report.external.patriots `
            -FolderName 'azure-ai-mgs-patriots' `
            -RepositoryUrl 'https://github.com/garylumsden/azure-ai-mgs-patriots.git' `
            -RepositoriesRoot $repositoriesRoot
    }
    if ($null -ne $report.external.tokensAndCredits) {
        Remove-DemoReadyClonedOptionalRepository `
            -Entry $report.external.tokensAndCredits `
            -FolderName 'tokens-and-credits' `
            -RepositoryUrl 'https://github.com/garylumsden/tokens-and-credits.git' `
            -RepositoriesRoot $repositoriesRoot
    }
}
if ($reportMatchesEnvironment) {
    foreach ($path in @(
        $ReportPath,
        (Join-Path $runtimeRoot 'sessions.generated.json')
    )) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}
else {
    Write-DemoReadyStatus `
        -Status 'warn' `
        -Message 'Retained readiness and Presenter state because it does not match this environment.'
}
if (Test-Path -LiteralPath $processPath -PathType Leaf) {
    $finalProcessState = Get-Content -LiteralPath $processPath -Raw | ConvertFrom-Json
    if (@($finalProcessState.processes).Count -eq 0) {
        Remove-Item -LiteralPath $processPath -Force
        Write-DemoReadyStatus -Status 'ok' -Message 'Removed the empty process state.'
    }
    else {
        $retainedNames = @($finalProcessState.processes | ForEach-Object { [string]$_.name }) -join ', '
        Write-DemoReadyStatus `
            -Status 'warn' `
            -Message "Retained unrelated or protected process state: $retainedNames."
    }
}
Write-DemoReadyStatus -Status 'ok' -Message 'Removed generated readiness and Presenter state.'
Write-DemoReadyBanner `
    -Title 'Teardown complete' `
    -Lines @(
        "Removed $($contexts.Count) Azure deployment(s).",
        ($keepOptionalCheckouts `
            ? 'Optional checkouts remain on disk.' `
            : 'Startup-created optional checkouts were removed when safe.')
    )
