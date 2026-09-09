# Guided selection and deployment-plan helpers.
# Dot-source this file after Common.ps1 and Console.ps1.

function Get-DemoReadyDemoLabel {
    <#
    .SYNOPSIS
        Returns the customer-facing label of every selectable item.
    #>
    [CmdletBinding()]
    param()

    return [ordered]@{
        Demo1 = 'Demo 1 - Foundation and Ground'
        Demo2 = 'Demo 2 - Act'
        Demo3 = 'Demo 3 - Cross-Government Council'
        Demo4 = 'Demo 4 - Hosted'
        Patriots = 'Patriots local application'
        TokensAndCredits = 'Tokens and Credits local application'
    }
}

function Test-DemoReadyInteractiveConsole {
    [CmdletBinding()]
    param()

    return [Environment]::UserInteractive -and -not [Console]::IsInputRedirected
}

function Get-DemoReadyGuidedStepPlan {
    <#
    .SYNOPSIS
        Returns the guided steps in the exact order that startup performs them.
    .DESCRIPTION
        This is the single source of the guided step numbers. Every guided
        section heading derives its number from this order, so a displayed
        number cannot disagree with the interaction sequence.
    #>
    [CmdletBinding()]
    param()

    return [ordered]@{
        Subscription = 'Azure subscription'
        Selection = 'Select demonstrations'
        Locations = 'Select Azure locations'
        Plan = 'Review the complete plan'
    }
}

function Write-DemoReadyGuidedSection {
    <#
    .SYNOPSIS
        Writes a guided section heading numbered from the guided step plan.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Key)

    $plan = Get-DemoReadyGuidedStepPlan
    $keys = @($plan.Keys)
    $index = $keys.IndexOf($Key)
    if ($index -lt 0) {
        throw "The guided step '$Key' is not defined in the guided step plan."
    }
    Write-DemoReadySection -Title $plan[$Key] -Step ($index + 1) -TotalSteps $keys.Count
}

function Get-DemoReadySelection {
    [CmdletBinding()]
    param(
        [switch]$Demo1,
        [switch]$Demo2,
        [switch]$Demo3,
        [switch]$Demo4,
        [switch]$Patriots,
        [switch]$TokensAndCredits,
        [switch]$All,
        [switch]$IncludePatriots,
        [switch]$NonInteractive,
        [string[]]$RemainingArguments = @(),
        [bool]$InteractiveConsole = (Test-DemoReadyInteractiveConsole)
    )

    $literalAll = $false
    foreach ($argument in @($RemainingArguments)) {
        if ([string]::IsNullOrEmpty([string]$argument)) {
            continue
        }
        if ($argument -ceq '--all') {
            $literalAll = $true
            continue
        }
        throw "The remaining argument '$argument' is not supported. Use --all only."
    }

    $hasExplicitSelection = $Demo1 -or $Demo2 -or $Demo3 -or $Demo4 -or
        $Patriots -or $TokensAndCredits -or $All -or $IncludePatriots -or $literalAll
    $guided = -not $NonInteractive -and $InteractiveConsole -and -not $hasExplicitSelection
    $selectAll = $All -or $literalAll

    $selection = [ordered]@{
        Demo1 = [bool]($selectAll -or $Demo1)
        Demo2 = [bool]($selectAll -or $Demo2)
        Demo3 = [bool]($selectAll -or $Demo3)
        Demo4 = [bool]($selectAll -or $Demo4)
        Patriots = [bool]($selectAll -or $Patriots -or $IncludePatriots)
        TokensAndCredits = [bool]($selectAll -or $TokensAndCredits)
    }

    if (-not $guided -and -not $hasExplicitSelection) {
        # Preserve the original argument-free automation behavior.
        $selection.Demo1 = $true
        $selection.Demo2 = $true
        $selection.Demo3 = $true
        $selection.Demo4 = $true
    }

    return [pscustomobject]@{
        Guided = $guided
        HasExplicitSelection = [bool]$hasExplicitSelection
        Items = $selection
    }
}

function Read-DemoReadyYesNo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Prompt,
        [bool]$Default = $false,
        [scriptblock]$InputProvider
    )

    $glyphs = Get-DemoReadyGlyphSet
    $suffix = $Default ? '[Y/n]' : '[y/N]'
    while ($true) {
        $answer = if ($null -eq $InputProvider) {
            Read-Host "  $($glyphs.Arrow) $Prompt $suffix"
        }
        else {
            & $InputProvider "$Prompt $suffix"
        }
        if ([string]::IsNullOrWhiteSpace([string]$answer)) {
            return $Default
        }
        switch ([string]$answer.Trim().ToLowerInvariant()) {
            { $_ -in @('y', 'yes') } { return $true }
            { $_ -in @('n', 'no') } { return $false }
            default { Write-DemoReadyStatus -Status 'warn' -Message 'Enter yes or no.' }
        }
    }
}

function Read-DemoReadyValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Prompt,
        [Parameter(Mandatory)][string]$Default,
        [Parameter(Mandatory)][string]$ValidationPattern,
        [scriptblock]$InputProvider
    )

    $glyphs = Get-DemoReadyGlyphSet
    while ($true) {
        $answer = if ($null -eq $InputProvider) {
            Read-Host "  $($glyphs.Arrow) $Prompt [$Default]"
        }
        else {
            & $InputProvider "$Prompt [$Default]"
        }
        $value = [string]::IsNullOrWhiteSpace([string]$answer) ? $Default : [string]$answer.Trim()
        if ($value -match $ValidationPattern) {
            return $value
        }
        Write-DemoReadyStatus `
            -Status 'warn' `
            -Message 'Enter a valid Azure location name, such as swedencentral.'
    }
}

function Get-DemoReadyAzureSubscriptionSummary {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepositoryRoot)

    $json = Invoke-DemoReadyNative `
        -FilePath 'az' `
        -Arguments @(
            'account', 'show', '--query',
            '{id:id,name:name,state:state,tenantId:tenantId}', '--output', 'json'
        ) `
        -WorkingDirectory $RepositoryRoot `
        -CaptureOutput `
        -PreserveCapturedOutput `
        -Quiet
    $account = $json | ConvertFrom-Json
    if ([string]$account.state -cne 'Enabled' -or
        [string]$account.id -notmatch '^[0-9a-fA-F-]{36}$' -or
        [string]$account.tenantId -notmatch '^[0-9a-fA-F-]{36}$') {
        throw 'Azure CLI authentication did not return an enabled subscription.'
    }
    $id = [string]$account.id
    $tenantId = [string]$account.tenantId
    return [pscustomobject]@{
        Id = $id
        Name = [string]$account.name
        DisplayId = "********-$($id.Substring($id.Length - 4))"
        DisplayTenantId = "********-$($tenantId.Substring($tenantId.Length - 4))"
    }
}

function Confirm-DemoReadyAzureSubscription {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][pscustomobject]$Subscription,
        [scriptblock]$InputProvider
    )

    Write-DemoReadyGuidedSection -Key 'Subscription'
    Write-DemoReadyField -Label 'Subscription name' -Value $Subscription.Name
    Write-DemoReadyField -Label 'Subscription ID' -Value $Subscription.DisplayId
    if ($null -ne $Subscription.PSObject.Properties['DisplayTenantId']) {
        Write-DemoReadyField -Label 'Tenant ID' -Value $Subscription.DisplayTenantId
    }
    Write-DemoReadyStatus `
        -Status 'info' `
        -Message 'Every demonstration deploys into this subscription.'
    Write-Host ''
    if (-not (Read-DemoReadyYesNo `
        -Prompt 'Is this the correct Azure subscription?' `
        -Default $true `
        -InputProvider $InputProvider)) {
        throw 'Select the required Azure subscription, then run this command again.'
    }
}

function Read-DemoReadyGuidedSelection {
    [CmdletBinding()]
    param([scriptblock]$InputProvider)

    Write-DemoReadyGuidedSection -Key 'Selection'
    Write-DemoReadyStatus `
        -Status 'info' `
        -Message 'Press Enter to accept the value in brackets.'
    Write-Host ''
    $selection = [ordered]@{}
    foreach ($item in @(
        [pscustomobject]@{ Key = 'Demo1'; Note = 'Azure deployment'; Default = $true },
        [pscustomobject]@{ Key = 'Demo2'; Note = 'Azure deployment'; Default = $true },
        [pscustomobject]@{ Key = 'Demo3'; Note = 'Azure deployment and local application'; Default = $true },
        [pscustomobject]@{ Key = 'Demo4'; Note = 'Azure deployment'; Default = $true },
        [pscustomobject]@{ Key = 'Patriots'; Note = 'optional, cloned when absent'; Default = $false },
        [pscustomobject]@{ Key = 'TokensAndCredits'; Note = 'optional, cloned when absent'; Default = $false }
    )) {
        $label = (Get-DemoReadyDemoLabel)[$item.Key]
        $selection[$item.Key] = Read-DemoReadyYesNo `
            -Prompt "Include $label ($($item.Note))?" `
            -Default $item.Default `
            -InputProvider $InputProvider
    }
    if (@($selection.Values | Where-Object { $_ }).Count -eq 0) {
        throw 'Select at least one demonstration.'
    }
    return $selection
}

function Read-DemoReadyGuidedLocations {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Collections.IDictionary]$Selection,
        [Parameter(Mandatory)][Collections.IDictionary]$Defaults,
        [scriptblock]$InputProvider
    )

    $locations = [ordered]@{}
    $prompts = @(
        [pscustomobject]@{ Key = 'Demo1'; Label = 'Demo 1' },
        [pscustomobject]@{ Key = 'Demo2'; Label = 'Demo 2 / Act' },
        [pscustomobject]@{ Key = 'Demo3'; Label = 'Demo 3 / Council' },
        [pscustomobject]@{ Key = 'Demo4'; Label = 'Demo 4 / Hosted' }
    )
    if (@($prompts | Where-Object { $Selection[$_.Key] }).Count -gt 0) {
        Write-DemoReadyGuidedSection -Key 'Locations'
        Write-DemoReadyStatus `
            -Status 'info' `
            -Message 'Use a lowercase location name. Model availability differs by location.'
        Write-Host ''
    }
    foreach ($item in $prompts) {
        $locations[$item.Key] = [string]$Defaults[$item.Key]
        if ($Selection[$item.Key]) {
            $locations[$item.Key] = Read-DemoReadyValue `
                -Prompt "Azure location for $($item.Label)" `
                -Default ([string]$Defaults[$item.Key]) `
                -ValidationPattern '^[a-z0-9]+$' `
                -InputProvider $InputProvider
        }
    }
    return $locations
}

function Get-DemoReadyModelCapacityCatalog {
    [CmdletBinding()]
    param()

    return [ordered]@{
        Demo1 = @(
            [pscustomobject]@{ Type = 'Chat'; Model = 'gpt-5-mini'; Sku = 'GlobalStandard'; Capacity = 100 },
            [pscustomobject]@{ Type = 'Embedding'; Model = 'text-embedding-3-small'; Sku = 'GlobalStandard'; Capacity = 30 }
        )
        Demo2 = @(
            [pscustomobject]@{ Type = 'Chat'; Model = 'gpt-5.4-mini'; Sku = 'GlobalStandard'; Capacity = 500 }
        )
        Demo3 = @(
            [pscustomobject]@{ Type = 'Chat'; Model = 'gpt-5.4'; Sku = 'GlobalStandard'; Capacity = 100 },
            [pscustomobject]@{ Type = 'Chat'; Model = 'gpt-5-mini'; Sku = 'GlobalStandard'; Capacity = 1000 },
            [pscustomobject]@{ Type = 'Chat'; Model = 'gpt-5-nano'; Sku = 'GlobalStandard'; Capacity = 1000 },
            [pscustomobject]@{ Type = 'Embedding'; Model = 'text-embedding-3-small'; Sku = 'GlobalStandard'; Capacity = 30 }
        )
        Demo4 = @(
            [pscustomobject]@{ Type = 'Chat'; Model = 'gpt-5.4-mini'; Sku = 'GlobalStandard'; Capacity = 50 },
            [pscustomobject]@{ Type = 'Embedding'; Model = 'text-embedding-3-small'; Sku = 'GlobalStandard'; Capacity = 20 }
        )
    }
}

function Get-DemoReadyModelCapacityPlan {
    [CmdletBinding()]
    param([Parameter(Mandatory)][Collections.IDictionary]$Selection)

    $catalog = Get-DemoReadyModelCapacityCatalog
    $rows = [Collections.Generic.List[object]]::new()
    $number = 0
    foreach ($demo in @('Demo1', 'Demo2', 'Demo3', 'Demo4')) {
        if (-not $Selection[$demo]) {
            continue
        }
        foreach ($model in @($catalog[$demo])) {
            $number++
            $rows.Add([pscustomobject]@{
                Number = $number
                Demo = $demo
                Type = $model.Type
                Model = $model.Model
                Sku = $model.Sku
                Capacity = [int]$model.Capacity
            })
        }
    }
    return [pscustomobject]@{
        Deployments = @($rows)
        DeploymentCount = $rows.Count
        AggregateCapacity = [int](($rows | Measure-Object -Property Capacity -Sum).Sum ?? 0)
    }
}

function Get-DemoReadyLocalApplicationPlan {
    <#
    .SYNOPSIS
        Returns every local application that startup builds and starts.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][Collections.IDictionary]$Selection)

    $rows = [Collections.Generic.List[object]]::new()
    if ($Selection['Demo1']) {
        $rows.Add([pscustomobject]@{
            Name = 'Demo 1 - Foundation and Ground'
            Endpoints = @('http://localhost:5090/')
        })
    }
    if ($Selection['Demo3']) {
        $rows.Add([pscustomobject]@{
            Name = 'Demo 3 - Cross-Government Council'
            Endpoints = @('http://localhost:5080/', 'https://localhost:7080/')
        })
    }
    if ($Selection['Patriots']) {
        $rows.Add([pscustomobject]@{
            Name = 'Patriots local application'
            Endpoints = @('http://localhost:5081/', 'https://localhost:7081/')
        })
    }
    if ($Selection['TokensAndCredits']) {
        $rows.Add([pscustomobject]@{
            Name = 'Tokens and Credits local application'
            Endpoints = @('http://localhost:5041/')
        })
    }
    $rows.Add([pscustomobject]@{
        Name = 'Presenter'
        Endpoints = @('http://localhost:5088/')
    })
    return @($rows)
}

function Get-DemoReadyExternalPlanEntry {
    <#
    .SYNOPSIS
        Reports the path that startup will use for one optional external repository.

    .DESCRIPTION
        This mirrors the resolution order of Resolve-DemoReadyExternalRepository without
        changing any state. It never clones and never writes.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][pscustomobject]$Definition,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [AllowEmptyString()][string]$ParameterPath = '',
        [Collections.IDictionary]$SetupRepositories = @{}
    )

    $setupEntry = $SetupRepositories.Contains($Definition.Identity) `
        ? $SetupRepositories[$Definition.Identity] `
        : $null
    $candidates = @(
        [pscustomobject]@{ Source = 'command parameter'; Path = $ParameterPath },
        [pscustomobject]@{
            Source = 'environment variable'
            Path = [Environment]::GetEnvironmentVariable($Definition.EnvironmentVariable)
        },
        [pscustomobject]@{
            Source = 'setup file'
            Path = ($null -eq $setupEntry ? '' : [string]$setupEntry.Path)
        },
        [pscustomobject]@{
            Source = 'sibling folder'
            Path = (Join-Path (Split-Path -Parent $RepositoryRoot) $Definition.FolderName)
        }
    )
    $selected = $candidates |
        Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_.Path) } |
        Select-Object -First 1
    $path = [string]$selected.Path
    $present = Test-Path -LiteralPath $path -PathType Container
    $action = $present `
        ? 'Use the existing checkout' `
        : ($selected.Source -ceq 'sibling folder' `
            ? "Clone $($Definition.RepositoryUrl)" `
            : 'Missing. Startup fails until this path exists')
    return [pscustomobject]@{
        Identity = $Definition.Identity
        DisplayName = $Definition.DisplayName
        Path = $path
        PathSource = $selected.Source
        Present = $present
        Action = $action
    }
}

function Get-DemoReadyPlannedAction {
    <#
    .SYNOPSIS
        Returns the ordered actions that startup performs after confirmation.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Collections.IDictionary]$Selection,
        [object[]]$ExternalPlan = @()
    )

    $actions = [Collections.Generic.List[string]]::new()
    $actions.Add('Stop the local applications that this command restarts.')
    foreach ($entry in @($ExternalPlan)) {
        if (-not $entry.Present) {
            $actions.Add("Clone $($entry.DisplayName) into $($entry.Path).")
        }
    }
    $actions.Add('Create or reuse the azd environment of every selected demonstration.')
    if ($Selection['Demo4']) {
        $actions.Add('Create or reuse the Demo 4 Microsoft Entra application registration.')
    }
    if ($Selection['Demo1']) {
        $actions.Add('Provision Demo 1 in Azure, then publish its Foundation and Ground agents.')
    }
    if ($Selection['Demo2']) {
        $actions.Add('Provision and deploy Demo 2 Act in Azure.')
    }
    if ($Selection['Demo3']) {
        $actions.Add('Provision Demo 3 Council in Azure and configure its grounding.')
    }
    if ($Selection['Demo4']) {
        $actions.Add('Provision and deploy Demo 4 Hosted in Azure.')
    }
    $actions.Add('Build the selected local applications and the Presenter.')
    $actions.Add('Start the selected local applications and the Presenter.')
    $actions.Add('Wait until every selected endpoint answers.')
    $actions.Add('Write the readiness report and the Presenter catalog.')
    return @($actions)
}

function Show-DemoReadyDeploymentPlan {
    <#
    .SYNOPSIS
        Shows the complete summary of everything that startup will do.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Collections.IDictionary]$Selection,
        [Parameter(Mandatory)][Collections.IDictionary]$Locations,
        [Collections.IDictionary]$EnvironmentNames,
        [pscustomobject]$Subscription,
        [object[]]$ExternalPlan = @()
    )

    $labels = Get-DemoReadyDemoLabel
    $azureDemos = @('Demo1', 'Demo2', 'Demo3', 'Demo4')

    Write-DemoReadyGuidedSection -Key 'Plan'

    if ($null -ne $Subscription) {
        Write-Host '  Azure target' -ForegroundColor White
        Write-DemoReadyField -Label 'Subscription name' -Value $Subscription.Name
        Write-DemoReadyField -Label 'Subscription ID' -Value $Subscription.DisplayId
        Write-Host ''
    }

    Write-Host '  Selection' -ForegroundColor White
    foreach ($key in $labels.Keys) {
        $included = [bool]$Selection[$key]
        $detail = $included -and $key -in $azureDemos `
            ? " in $($Locations[$key])" `
            : ''
        Write-DemoReadyStatus `
            -Status ($included ? 'ok' : 'pending') `
            -Message ("{0}{1}{2}" -f
                ($included ? 'Include: ' : 'Skip:    '),
                $labels[$key],
                $detail) `
            -MessageColor ($included ? '' : 'DarkGray')
    }

    $azureRows = [Collections.Generic.List[object]]::new()
    foreach ($key in $azureDemos) {
        if (-not $Selection[$key]) {
            continue
        }
        $environmentName = ($null -ne $EnvironmentNames -and $EnvironmentNames.Contains($key)) `
            ? [string]$EnvironmentNames[$key] `
            : ''
        $azureRows.Add(@(
            $labels[$key],
            [string]$Locations[$key],
            $environmentName,
            ([string]::IsNullOrWhiteSpace($environmentName) ? '' : "rg-$environmentName")
        ))
    }
    if ($azureRows.Count -gt 0) {
        Write-Host ''
        Write-Host '  Azure environments created or reused' -ForegroundColor White
        Write-DemoReadyTable `
            -Headers @('Demonstration', 'Location', 'azd environment', 'Resource group') `
            -Rows @($azureRows)
    }

    $capacityPlan = Get-DemoReadyModelCapacityPlan -Selection $Selection
    if ($capacityPlan.DeploymentCount -gt 0) {
        Write-Host ''
        Write-Host '  Azure AI model deployments' -ForegroundColor White
        Write-DemoReadyTable `
            -Headers @('#', 'Demonstration', 'Type', 'Model', 'SKU', 'Capacity') `
            -Align @('right', 'left', 'left', 'left', 'left', 'right') `
            -Rows @($capacityPlan.Deployments | ForEach-Object {
                , @(
                    [string]$_.Number,
                    [string]$labels[$_.Demo],
                    [string]$_.Type,
                    [string]$_.Model,
                    [string]$_.Sku,
                    [string]$_.Capacity
                )
            })
        Write-Host ''
        Write-DemoReadyStatus `
            -Status 'warn' `
            -Message "Model deployments: $($capacityPlan.DeploymentCount)"
        Write-DemoReadyStatus `
            -Status 'warn' `
            -Message "Aggregate capacity/quota units: $($capacityPlan.AggregateCapacity)"
        Write-DemoReadyStatus `
            -Status 'warn' `
            -Message 'Capacity is requested deployment capacity, in thousands of tokens per minute.'
        Write-DemoReadyStatus `
            -Status 'warn' `
            -Message 'Availability depends on the region and subscription quota.'
    }

    $localApplications = Get-DemoReadyLocalApplicationPlan -Selection $Selection
    Write-Host ''
    Write-Host '  Local applications started on this machine' -ForegroundColor White
    Write-DemoReadyTable `
        -Headers @('Application', 'Endpoints') `
        -Rows @($localApplications | ForEach-Object {
            , @([string]$_.Name, (@($_.Endpoints) -join '  '))
        })

    if (@($ExternalPlan).Count -gt 0) {
        Write-Host ''
        Write-Host '  Optional external repositories' -ForegroundColor White
        foreach ($entry in @($ExternalPlan)) {
            Write-DemoReadyStatus `
                -Status ($entry.Present ? 'ok' : 'warn') `
                -Message "$($entry.DisplayName): $($entry.Action)"
            Write-DemoReadyField -Label 'Path' -Value $entry.Path -LabelWidth 8 -Indent 6
            Write-DemoReadyField -Label 'Source' -Value $entry.PathSource -LabelWidth 8 -Indent 6
        }
    }

    Write-Host ''
    Write-Host '  Totals' -ForegroundColor White
    Write-DemoReadyField -Label 'Azure deployments' -Value ([string]$azureRows.Count)
    Write-DemoReadyField -Label 'Model deployments' -Value ([string]$capacityPlan.DeploymentCount)
    Write-DemoReadyField -Label 'Capacity/quota units' -Value ([string]$capacityPlan.AggregateCapacity)
    Write-DemoReadyField -Label 'Local applications' -Value ([string]@($localApplications).Count)

    Write-Host ''
    Write-Host '  Actions in order' -ForegroundColor White
    $number = 0
    foreach ($action in (Get-DemoReadyPlannedAction -Selection $Selection -ExternalPlan $ExternalPlan)) {
        $number++
        Write-DemoReadyOrderedStep -Number $number -Message $action
    }

    Write-Host ''
    Write-Host '  Not performed by this command' -ForegroundColor White
    foreach ($excluded in @(
        'No Azure resource is deleted and no environment is torn down.',
        'No test suite and no deep validation run. Use scripts\Test-DemoReady.ps1.',
        'No commit, no push, and no change to any external repository.'
    )) {
        Write-DemoReadyStatus -Status 'no' -Message $excluded -MessageColor 'DarkGray'
    }
    Write-DemoReadyRule
    return $capacityPlan
}

function Get-DemoReadyTeardownStepPlan {
    [CmdletBinding()]
    param()

    return [ordered]@{
        Subscription = 'Confirm Azure target'
        Selection = 'Select deployments to remove'
        Optional = 'Choose optional checkout handling'
        Plan = 'Review the complete teardown plan'
    }
}

function Write-DemoReadyTeardownSection {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Key)

    $plan = Get-DemoReadyTeardownStepPlan
    $keys = @($plan.Keys)
    $index = $keys.IndexOf($Key)
    if ($index -lt 0) {
        throw "The teardown step '$Key' is not defined."
    }
    Write-DemoReadySection -Title $plan[$Key] -Step ($index + 1) -TotalSteps $keys.Count
}

function Read-DemoReadyTeardownSelection {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Collections.IDictionary]$Defaults,
        [scriptblock]$InputProvider
    )

    Write-DemoReadyTeardownSection -Key 'Selection'
    Write-DemoReadyStatus `
        -Status 'info' `
        -Message 'Press Enter to accept the selection recorded by the latest successful or failed startup run.'
    Write-Host ''
    $selection = [ordered]@{}
    foreach ($key in @('Demo1', 'Demo2', 'Demo3', 'Demo4')) {
        $selection[$key] = Read-DemoReadyYesNo `
            -Prompt "Remove $((Get-DemoReadyDemoLabel)[$key])?" `
            -Default ([bool]$Defaults[$key]) `
            -InputProvider $InputProvider
    }
    return $selection
}

function Show-DemoReadyTeardownPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Collections.IDictionary]$Selection,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Contexts,
        [Parameter(Mandatory)][AllowEmptyCollection()][string[]]$ProcessNames,
        [Parameter(Mandatory)][pscustomobject]$Subscription,
        [Parameter(Mandatory)][bool]$KeepOptionalCheckouts,
        [AllowNull()][object]$Report
    )

    Write-DemoReadyTeardownSection -Key 'Plan'
    Write-Host '  Azure target' -ForegroundColor White
    Write-DemoReadyField -Label 'Subscription name' -Value $Subscription.Name
    Write-DemoReadyField -Label 'Subscription ID' -Value $Subscription.DisplayId
    Write-Host ''

    Write-Host '  Azure deployments removed' -ForegroundColor White
    $rows = @($Contexts | ForEach-Object {
        , @($_.Name, $_.Environment, "rg-$($_.Environment)")
    })
    Write-DemoReadyTable `
        -Headers @('Demonstration', 'azd environment', 'Resource group') `
        -Rows $rows
    Write-Host ''

    Write-Host '  Local applications stopped' -ForegroundColor White
    $processLabels = [ordered]@{
        presenter = 'Presenter'
        'demo1-comparison' = 'Demo 1 - Foundation and Ground'
        'cross-government-coordinate' = 'Demo 3 - Cross-Government Council'
        'external-patriots' = 'Patriots'
        'tokens-and-credits' = 'Tokens and Credits'
    }
    foreach ($name in $ProcessNames) {
        $label = $processLabels.Contains($name) ? $processLabels[$name] : $name
        Write-DemoReadyStatus -Status 'ok' -Message $label
    }
    Write-DemoReadyStatus -Status 'warn' -Message 'Abort before Azure teardown if any listed process cannot be verified and stopped.'
    Write-Host ''

    Write-Host '  Microsoft Entra application' -ForegroundColor White
    Write-DemoReadyStatus `
        -Status ($Selection.Demo4 ? 'ok' : 'pending') `
        -Message ($Selection.Demo4 `
            ? 'Remove the owned Demo 4 application after exact ownership verification.' `
            : 'Keep the Demo 4 application because Demo 4 is not selected.')
    Write-Host ''

    Write-Host '  Optional checkouts' -ForegroundColor White
    if ($KeepOptionalCheckouts) {
        Write-DemoReadyStatus -Status 'ok' -Message 'Keep Patriots on disk.'
        Write-DemoReadyStatus -Status 'ok' -Message 'Keep Tokens and Credits on disk.'
    }
    else {
        foreach ($entry in @(
            [pscustomobject]@{ Key = 'patriots'; Label = 'Patriots' },
            [pscustomobject]@{ Key = 'tokensAndCredits'; Label = 'Tokens and Credits' }
        )) {
            $state = $null -eq $Report -or $null -eq $Report.external `
                ? $null `
                : $Report.external.PSObject.Properties[$entry.Key]
            $cloned = $null -ne $state -and
                $null -ne $state.Value.PSObject.Properties['clonedByThisRun'] -and
                [bool]$state.Value.clonedByThisRun
            Write-DemoReadyStatus `
                -Status ($cloned ? 'warn' : 'pending') `
                -Message ($cloned `
                    ? "Remove $($entry.Label) only after Git proves every branch and tag is clean and pushed." `
                    : "Keep $($entry.Label); startup did not record creating this checkout.")
        }
    }
    Write-Host ''

    Write-Host '  Actions in order' -ForegroundColor White
    $actions = [Collections.Generic.List[string]]::new()
    $listedProcesses = @($ProcessNames | ForEach-Object {
        $processLabels.Contains($_) ? $processLabels[$_] : $_
    }) -join ', '
    $actions.Add("Verify and stop these managed local applications: $listedProcesses.")
    foreach ($context in $Contexts) {
        $actions.Add("Remove $($context.Environment), then purge its soft-deleted AI accounts and Key Vaults.")
    }
    if ($Selection.Demo4) {
        $actions.Add('Verify and remove the owned Demo 4 Microsoft Entra application.')
    }
    if (-not $KeepOptionalCheckouts) {
        $actions.Add('Remove only optional checkouts that this startup run cloned and that are fully pushed.')
    }
    $actions.Add('Remove matching generated readiness and Presenter state.')
    $number = 0
    foreach ($action in $actions) {
        $number++
        Write-DemoReadyOrderedStep -Number $number -Message $action
    }
    Write-Host ''

    Write-Host '  Not removed' -ForegroundColor White
    Write-DemoReadyStatus -Status 'no' -Message 'No unrelated Azure environment, resource, identity, repository, or process.'
    Write-DemoReadyStatus -Status 'no' -Message 'No optional checkout without exact startup ownership and Git synchronization proof.'
    Write-DemoReadyRule
}

function Assert-DemoReadyAzdEnvironmentAccess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Collections.IDictionary]$Selection,
        [Parameter(Mandatory)][Collections.IDictionary]$Contexts,
        [string[]]$SensitiveValues = @()
    )

    foreach ($key in @('Demo1', 'Demo2', 'Demo3', 'Demo4')) {
        if (-not $Selection[$key]) {
            continue
        }
        $output = Invoke-DemoReadyAzd `
            -Arguments @('env', 'list', '--output', 'json') `
            -WorkingDirectory ([string]$Contexts[$key]) `
            -SensitiveValues $SensitiveValues `
            -CaptureOutput `
            -Quiet
        if (-not [string]::IsNullOrWhiteSpace([string]$output)) {
            try {
                $null = $output | ConvertFrom-Json
            }
            catch {
                throw "azd returned invalid environment data for $key."
            }
        }
    }
}
