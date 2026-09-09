<#
.SYNOPSIS
    Deterministic automation tests for the bundled demo orchestration contract.

.DESCRIPTION
    This repository owns four deployments and the Presenter. Patriots is external.
    These tests never deploy Azure resources and never modify an
    external repository. They mock every external command and write only under
    .demo-ready\tests.
#>
[CmdletBinding()]
param(
    [string[]]$TestFilter = @('*')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$scriptRoot = Join-Path $root 'scripts'
$invokePath = Join-Path $scriptRoot 'Invoke-DemoReady.ps1'
$validatePath = Join-Path $scriptRoot 'Test-DemoReady.ps1'
$stopPath = Join-Path $scriptRoot 'Stop-DemoReady.ps1'
$removeAzurePath = Join-Path $scriptRoot 'Remove-DemoReadyAzure.ps1'
$externalModulePath = Join-Path $scriptRoot 'DemoReady\External.ps1'
$presenterModulePath = Join-Path $scriptRoot 'DemoReady\Presenter.ps1'
$guidedModulePath = Join-Path $scriptRoot 'DemoReady\Guided.ps1'
$patriotsModulePath = Join-Path $scriptRoot 'DemoReady\Patriots.ps1'
$schemaPath = Join-Path $scriptRoot 'DemoReady\repositories.v1.schema.json'
$setupAgentPath = Join-Path $root '.github\agents\demo-setup.agent.md'
$committedCatalogPath = Join-Path $root 'config\presenter\sessions.v1.json'
$testRoot = Join-Path $root ".demo-ready\tests\orchestration-$PID"

$script:failures = [Collections.Generic.List[string]]::new()
$script:executedTests = 0
$script:mockAzdOutput = ''
$script:mockNativeCalls = [Collections.Generic.List[object]]::new()

function Test-Case {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Action
    )

    if (@($script:TestFilter | Where-Object { $Name -like $_ }).Count -eq 0) {
        return
    }
    $script:executedTests++
    try {
        & $Action
        Write-Host "PASS: $Name"
    }
    catch {
        $script:failures.Add("$Name`: $($_.Exception.Message)")
        Write-Host "FAIL: $Name" -ForegroundColor Red
    }
}

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )
    if (-not $Condition) {
        throw $Message
    }
}

function Assert-Equal {
    param(
        [AllowNull()]$Actual,
        [AllowNull()]$Expected,
        [Parameter(Mandatory)][string]$Message
    )
    if ($Actual -cne $Expected) {
        throw "$Message Expected '$Expected' but received '$Actual'."
    }
}

function Get-ThrownMessage {
    # Runs an action that must fail and returns its exact message.
    param([Parameter(Mandatory)][scriptblock]$Action)

    try {
        & $Action
    }
    catch {
        return [string]$_.Exception.Message
    }
    throw 'The action completed but a failure was required.'
}

function Assert-Throws {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$ExpectedFragment,
        [Parameter(Mandatory)][string]$Message
    )

    $actual = Get-ThrownMessage -Action $Action
    Assert-True ($actual.Contains($ExpectedFragment, [StringComparison]::Ordinal)) `
        "$Message Received '$actual'."
}

function New-TestDirectory {
    param([Parameter(Mandatory)][string]$Name)

    $path = Join-Path $testRoot $Name
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $path -Force
    return (Resolve-Path -LiteralPath $path).Path
}

function New-TestFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Content
    )

    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force
    Set-Content -LiteralPath $Path -Value $Content -Encoding utf8NoBOM
    return $Path
}

function New-FakeExternalRepository {
    # Builds a synthetic external repository that satisfies the required-path contract.
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][pscustomobject]$Definition,
        [switch]$OmitGit,
        [string[]]$OmitPaths = @()
    )

    $null = New-Item -ItemType Directory -Path $Path -Force
    if (-not $OmitGit) {
        $null = New-Item -ItemType Directory -Path (Join-Path $Path '.git') -Force
    }
    foreach ($relativePath in $Definition.RequiredPaths) {
        if ($OmitPaths -contains $relativePath) {
            continue
        }
        $null = New-TestFile `
            -Path (Join-Path $Path $relativePath) `
            -Content '<!-- synthetic test fixture -->'
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Get-TestExternalDefinition {
    param([Parameter(Mandatory)][pscustomobject]$Definition)
    $copy = $Definition.PSObject.Copy()
    $copy.PSObject.Properties.Remove('BundledPath')
    return $copy
}

function Invoke-WithMockedFunction {
    # Replaces named functions in this script scope, then restores them exactly.
    param(
        [Parameter(Mandatory)][hashtable]$Functions,
        [Parameter(Mandatory)][scriptblock]$Action
    )

    $saved = @{}
    foreach ($name in @($Functions.Keys)) {
        $command = Get-Command -Name $name -CommandType Function -ErrorAction SilentlyContinue
        $saved[$name] = ($null -eq $command) ? $null : $command.ScriptBlock
        Set-Item -Path "function:script:$name" -Value $Functions[$name]
    }
    try {
        & $Action
    }
    finally {
        foreach ($name in @($Functions.Keys)) {
            if ($null -eq $saved[$name]) {
                Remove-Item -Path "function:script:$name" -Force -ErrorAction SilentlyContinue
            }
            else {
                Set-Item -Path "function:script:$name" -Value $saved[$name]
            }
        }
    }
}

function Use-EnvironmentVariable {
    # Sets process environment variables for one action, then restores them.
    param(
        [Parameter(Mandatory)][hashtable]$Values,
        [Parameter(Mandatory)][scriptblock]$Action
    )

    $saved = @{}
    foreach ($name in @($Values.Keys)) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, [string]$Values[$name], 'Process')
    }
    try {
        & $Action
    }
    finally {
        foreach ($name in @($saved.Keys)) {
            [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process')
        }
    }
}

function Wait-ProcessExit {
    # Process termination is bounded, not instant. Wait for the exact PID to end.
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [ValidateRange(1, 120)][int]$TimeoutSeconds = 20
    )

    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $process -or $process.HasExited) {
            return $true
        }
        Start-Sleep -Milliseconds 200
    }
    return $false
}

function New-GitRepository {    # Creates a local Git repository with no remote and no Azure dependency.
    param([Parameter(Mandatory)][string]$Path)

    $null = New-Item -ItemType Directory -Path $Path -Force
    & git -C $Path init --quiet
    if ($LASTEXITCODE -ne 0) {
        throw "Git could not initialise the test repository '$Path'."
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

$MockInvokeDemoReadyAzd = {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [string]$LogPath,
        [string[]]$SensitiveValues = @(),
        [switch]$CaptureOutput,
        [switch]$PreserveCapturedOutput,
        [switch]$Quiet
    )

    return $script:mockAzdOutput
}

$MockInvokeDemoReadyNative = {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$Arguments = @(),
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [string]$LogPath,
        [hashtable]$Environment = @{},
        [string[]]$SensitiveValues = @(),
        [switch]$CaptureOutput,
        [switch]$PreserveCapturedOutput,
        [switch]$Quiet
    )

    $script:mockNativeCalls.Add([pscustomobject]@{
        FilePath = $FilePath
        Arguments = @($Arguments)
        WorkingDirectory = $WorkingDirectory
        LogPath = $LogPath
    })
}

# Dot-sourcing loads every orchestration function without running the orchestrator.
. $invokePath
. $stopPath

$invokeSource = Get-Content -LiteralPath $invokePath -Raw
$validateSource = Get-Content -LiteralPath $validatePath -Raw
$externalSource = Get-Content -LiteralPath $externalModulePath -Raw
$tokensSource = Get-Content -LiteralPath (Join-Path $scriptRoot 'DemoReady\TokensAndCredits.ps1') -Raw
$presenterSource = Get-Content -LiteralPath $presenterModulePath -Raw
$guidedSource = Get-Content -LiteralPath $guidedModulePath -Raw
$patriotsSource = Get-Content -LiteralPath $patriotsModulePath -Raw
$commonModuleSource = Get-Content -LiteralPath (Join-Path $scriptRoot 'DemoReady\Common.ps1') -Raw
$ownedModuleSource = Get-Content -LiteralPath (Join-Path $scriptRoot 'DemoReady\Owned.ps1') -Raw
$entraModuleSource = Get-Content -LiteralPath (Join-Path $scriptRoot 'DemoReady\Entra.ps1') -Raw
$stopSource = Get-Content -LiteralPath $stopPath -Raw
$removeAzureSource = Get-Content -LiteralPath $removeAzurePath -Raw
$definitions = Get-DemoReadyExternalRepositoryDefinition

# ------------------------------------------------------------- orchestration shape

Test-Case 'Startup exposes the external orchestration parameters' {
    foreach ($required in @(
        '[string]$DefraRepoPath',
        '[string]$AssuranceBoardRepoPath',
        '[string]$PatriotsRepoPath',
        '[string]$TokensAndCreditsRepoPath',
        '[string]$DefraEnvironmentName',
        '[string]$AssuranceBoardEnvironmentName',
        '[string]$SetupPath'
    )) {
        Assert-True ($invokeSource.Contains($required, [StringComparison]::Ordinal)) `
            "The startup script does not expose '$required'."
    }
    Assert-True ($invokeSource.Contains(
            "Join-Path `$runtimeRoot 'repositories.local.json'",
            [StringComparison]::Ordinal)) `
        'The startup script does not default to the ignored setup file.'
}

Test-Case 'Startup exposes no validation-only parameter' {
    foreach ($removed in @('RunCloudValidation', 'SkipLiveRehearsal', 'SkipBuild')) {
        Assert-True (-not $invokeSource.Contains($removed, [StringComparison]::Ordinal)) `
            "The startup script still exposes the validation switch '$removed'."
    }
    foreach ($kept in @(
        '[string]$EnvironmentName',
        '[string]$SubscriptionId',
        '[string]$Demo1Location',
        '[string]$HostedLocation',
        '[string]$ReportPath'
    )) {
        Assert-True ($invokeSource.Contains($kept, [StringComparison]::Ordinal)) `
            "The startup script dropped the required parameter '$kept'."
    }
}

Test-Case 'Selection arguments support individual demos, aliases, and automation defaults' {
        $command = Get-Command $invokePath
        Assert-True ($command.Parameters.Demo2.Aliases -contains 'Act') 'Demo 2 does not expose the Act alias.'
        Assert-True ($command.Parameters.Demo3.Aliases -contains 'Council') 'Demo 3 does not expose the Council alias.'
        Assert-True ($command.Parameters.Demo4.Aliases -contains 'Hosted') 'Demo 4 does not expose the Hosted alias.'
        foreach ($name in @(
            'Demo1', 'Demo2', 'Demo3', 'Demo4', 'Patriots',
            'TokensAndCredits', 'All', 'NonInteractive', 'RemainingArguments'
        )) {
            Assert-True $command.Parameters.ContainsKey($name) "The startup command does not expose -$name."
        }

        $selected = Get-DemoReadySelection -Demo1 -Demo3 -Patriots -InteractiveConsole:$false
        Assert-True $selected.Items.Demo1 'Demo 1 was not selected.'
        Assert-True $selected.Items.Demo3 'Demo 3 was not selected.'
        Assert-True $selected.Items.Patriots 'Patriots was not selected.'
        foreach ($name in @('Demo2', 'Demo4', 'TokensAndCredits')) {
            Assert-True (-not $selected.Items[$name]) "The unrequested $name selection was enabled."
        }

        $legacy = Get-DemoReadySelection -NonInteractive -InteractiveConsole:$false
        foreach ($name in @('Demo1', 'Demo2', 'Demo3', 'Demo4')) {
            Assert-True $legacy.Items[$name] "The legacy full deployment omitted $name."
        }
        Assert-True (-not $legacy.Items.Patriots) 'The legacy full deployment enabled Patriots.'
        Assert-True (-not $legacy.Items.TokensAndCredits) 'The legacy full deployment enabled Tokens and Credits.'

        $guided = Get-DemoReadySelection -InteractiveConsole:$true
        Assert-True $guided.Guided 'An interactive argument-free invocation did not enter the guided flow.'
        Assert-True (-not ($guided.Items.Values -contains $true)) 'The guided flow selected a demo before user input.'

        $emptyBoundArgument = Get-DemoReadySelection `
            -RemainingArguments @('') `
            -InteractiveConsole:$true
        Assert-True $emptyBoundArgument.Guided `
            'An empty ValueFromRemainingArguments binding prevented the guided flow.'
}

Test-Case 'Literal all parsing selects every supported demo and rejects other remaining arguments' {
        $selected = Get-DemoReadySelection `
            -RemainingArguments @('--all') `
            -InteractiveConsole:$false
        foreach ($name in @('Demo1', 'Demo2', 'Demo3', 'Demo4', 'Patriots', 'TokensAndCredits')) {
            Assert-True $selected.Items[$name] "Literal --all omitted $name."
        }
        Assert-Throws `
            -Action {
                Get-DemoReadySelection `
                    -RemainingArguments @('--unknown') `
                    -InteractiveConsole:$false
            } `
            -ExpectedFragment 'Use --all only' `
            -Message 'An unknown remaining argument was accepted.'
}

Test-Case 'The centralized model plan reports exact deployment and capacity totals' {
        $catalog = Get-DemoReadyModelCapacityCatalog
        Assert-Equal @($catalog.Keys).Count 4 'The capacity catalog must contain four Azure demos.'
        $all = Get-DemoReadyModelCapacityPlan -Selection @{
            Demo1 = $true
            Demo2 = $true
            Demo3 = $true
            Demo4 = $true
        }
        Assert-Equal $all.DeploymentCount 9 'The full plan model deployment count is incorrect.'
        Assert-Equal $all.AggregateCapacity 2830 'The full plan aggregate capacity is incorrect.'
        Assert-Equal ($all.Deployments | Where-Object {
            $_.Demo -ceq 'Demo3' -and $_.Model -ceq 'gpt-5-nano'
        }).Capacity 1000 'The Demo 3 nano capacity is incorrect.'
        Assert-True (-not @($all.Deployments | Where-Object {
            $_.Sku -cne 'GlobalStandard'
        })) 'A model deployment has an unexpected SKU.'

        $subset = Get-DemoReadyModelCapacityPlan -Selection @{
            Demo1 = $true
            Demo2 = $false
            Demo3 = $false
            Demo4 = $true
        }
        Assert-Equal $subset.DeploymentCount 4 'The subset model deployment count is incorrect.'
        Assert-Equal $subset.AggregateCapacity 200 'The subset aggregate capacity is incorrect.'
        foreach ($requiredText in @(
            'Capacity is requested deployment capacity',
            'Availability depends on the region and subscription quota',
            'Aggregate capacity/quota units'
        )) {
            Assert-True ($guidedSource.Contains($requiredText, [StringComparison]::Ordinal)) `
                "The guided warning is missing '$requiredText'."
        }
}

Test-Case 'The console renderer keeps every glyph and table cell aligned' {
    $glyphs = Get-DemoReadyGlyphSet
    foreach ($name in @(
        'Ok', 'No', 'Warn', 'Info', 'Pending', 'Arrow', 'Bullet',
        'Horizontal', 'Vertical', 'TopLeft', 'TopRight', 'BottomLeft', 'BottomRight'
    )) {
        Assert-True ($glyphs.Contains($name)) "The glyph set is missing '$name'."
        Assert-Equal ([string]$glyphs[$name]).Length 1 "The glyph '$name' is not a single character."
    }

    $width = Get-DemoReadyConsoleWidth
    Assert-True ($width -ge 60 -and $width -le 100) 'The render width is outside its supported range.'

    $lines = @(Write-DemoReadyTable `
        -Headers @('Model', 'Capacity') `
        -Align @('left', 'right') `
        -Rows @(@('gpt-5-mini', '100'), @('text-embedding-3-small', '30')) `
        6>&1 | ForEach-Object { [string]$_ })
    Assert-Equal $lines.Count 4 'The table did not render a header, a rule, and every row.'
    $lengths = @($lines | ForEach-Object { $_.TrimEnd().Length })
    Assert-Equal @($lengths | Sort-Object -Unique).Count 1 'The table columns are not aligned.'
    Assert-True ($lines[3].EndsWith('  30', [StringComparison]::Ordinal)) `
        'The table did not right-align a numeric column.'

    $flattened = $false
    try {
        Write-DemoReadyTable -Headers @('A', 'B') -Rows @('a', 'b') 6>&1 | Out-Null
    }
    catch {
        $flattened = $_.Exception.Message.Contains('flattened', [StringComparison]::Ordinal)
    }
    Assert-True $flattened 'The table renderer accepted a flattened row.'

    $nested = $false
    try {
        Write-DemoReadyTable -Headers @('A', 'B') -Rows @(, @(, @('a', 'b'))) 6>&1 | Out-Null
    }
    catch {
        $nested = $_.Exception.Message.Contains('nested', [StringComparison]::Ordinal)
    }
    Assert-True $nested 'The table renderer accepted a nested row.'
}

Test-Case 'The plan summary reports environments, resource groups, actions, and exclusions' {
    $selection = [ordered]@{
        Demo1 = $true
        Demo2 = $false
        Demo3 = $true
        Demo4 = $true
        Patriots = $true
        TokensAndCredits = $false
    }
    $locations = [ordered]@{
        Demo1 = 'switzerlandnorth'
        Demo2 = 'swedencentral'
        Demo3 = 'swedencentral'
        Demo4 = 'swedencentral'
    }
    $environmentNames = Get-DemoReadyEnvironmentNames -BaseName 'psad-demos'
    $external = @([pscustomobject]@{
        DisplayName = 'Patriots council (azure-ai-mgs-patriots)'
        Path = 'C:\repos\azure-ai-mgs-patriots'
        PathSource = 'sibling folder'
        Present = $false
        Action = 'Clone https://github.com/garylumsden/azure-ai-mgs-patriots.git'
    })

    $plan = $null
    $rendered = @(& {
        $script:planResult = Show-DemoReadyDeploymentPlan `
            -Selection $selection `
            -Locations $locations `
            -EnvironmentNames $environmentNames `
            -Subscription ([pscustomobject]@{
                Name = 'Demo subscription'
                DisplayId = '********-1234'
                DisplayTenantId = '********-5678'
            }) `
            -ExternalPlan $external
    } 6>&1 | ForEach-Object { [string]$_ })
    $plan = $script:planResult
    $text = $rendered -join "`n"

    Assert-Equal $plan.DeploymentCount 8 'The plan capacity count is incorrect for the selection.'
    foreach ($required in @(
        'Demo subscription',
        '********-1234',
        'psad-demos-demo1',
        'rg-psad-demos-demo1',
        'rg-psad-demos-demo3',
        'rg-psad-demos-demo4',
        'switzerlandnorth',
        'http://localhost:5090/',
        'http://localhost:5081/',
        'http://localhost:5088/',
        'Clone https://github.com/garylumsden/azure-ai-mgs-patriots.git',
        'No Azure resource is deleted',
        'scripts\Test-DemoReady.ps1'
    )) {
        Assert-True ($text.Contains($required, [StringComparison]::Ordinal)) `
            "The plan summary is missing '$required'."
    }
    Assert-True (-not $text.Contains('rg-psad-demos-demo2', [StringComparison]::Ordinal)) `
        'The plan summary shows an unselected demonstration.'
    Assert-True (-not $text.Contains('http://localhost:5041/', [StringComparison]::Ordinal)) `
        'The plan summary shows an unselected local application.'
    Assert-True ($text.Contains('Skip:    Demo 2 - Act', [StringComparison]::Ordinal)) `
        'The plan summary does not state which demonstrations are skipped.'
}

Test-Case 'The planned action list matches the selected work' {
    $actions = Get-DemoReadyPlannedAction `
        -Selection @{
            Demo1 = $true
            Demo2 = $false
            Demo3 = $false
            Demo4 = $false
            Patriots = $false
            TokensAndCredits = $false
        } `
        -ExternalPlan @()
    $joined = $actions -join '|'
    Assert-True ($joined.Contains('Provision Demo 1', [StringComparison]::Ordinal)) `
        'The action list omits the selected Demo 1 deployment.'
    foreach ($excluded in @('Demo 2 Act', 'Demo 3 Council', 'Demo 4 Hosted', 'Entra')) {
        Assert-True (-not $joined.Contains($excluded, [StringComparison]::Ordinal)) `
            "The action list includes unselected work: $excluded."
    }
    Assert-Equal $actions[0] 'Stop the local applications that this command restarts.' `
        'The action list does not begin with the local stop.'
    Assert-Equal $actions[-1] 'Write the readiness report and the Presenter catalog.' `
        'The action list does not end with the readiness report.'
}

Test-Case 'External plan entries report reuse and clone intent without changing anything' {
    $workspace = New-TestDirectory -Name 'external-plan-entry'
    $repositoryRoot = Join-Path $workspace 'PublicSectorAgentDemos'
    $null = New-Item -ItemType Directory -Path $repositoryRoot -Force
    $existing = Join-Path $workspace 'tokens-and-credits'
    $null = New-Item -ItemType Directory -Path $existing -Force

    $reuse = Get-DemoReadyExternalPlanEntry `
        -Definition $definitions.tokensAndCredits `
        -RepositoryRoot $repositoryRoot `
        -SetupRepositories @{}
    Assert-True $reuse.Present 'An existing sibling checkout was not detected.'
    Assert-Equal $reuse.Action 'Use the existing checkout' 'An existing checkout was not reused.'
    Assert-Equal $reuse.PathSource 'sibling folder' 'The reuse path source is incorrect.'

    $clone = Get-DemoReadyExternalPlanEntry `
        -Definition $definitions.patriots `
        -RepositoryRoot $repositoryRoot `
        -SetupRepositories @{}
    Assert-True (-not $clone.Present) 'An absent checkout was reported as present.'
    Assert-True ($clone.Action.StartsWith('Clone ', [StringComparison]::Ordinal)) `
        'An absent sibling checkout is not planned for cloning.'
    Assert-True (-not (Test-Path -LiteralPath $clone.Path)) `
        'The plan created the external checkout.'

    $configured = Get-DemoReadyExternalPlanEntry `
        -Definition $definitions.patriots `
        -RepositoryRoot $repositoryRoot `
        -SetupRepositories @{ patriots = [pscustomobject]@{ Path = $existing } }
    Assert-Equal $configured.PathSource 'setup file' 'The setup file did not take precedence.'
    Assert-Equal $configured.Path $existing 'The configured path was not used.'
}

Test-Case 'Interactive helpers apply defaults, validate input, and mask subscription identifiers' {
        $answers = [Collections.Generic.Queue[string]]::new()
        $answers.Enqueue('maybe')
        $answers.Enqueue('')
        $input = { param($Prompt) return $answers.Dequeue() }
        Assert-True (Read-DemoReadyYesNo -Prompt 'Continue?' -Default $true -InputProvider $input) `
            'The yes or no helper did not apply its default.'

        $locations = [Collections.Generic.Queue[string]]::new()
        $locations.Enqueue('West Europe')
        $locations.Enqueue('swedencentral')
        $locationInput = { param($Prompt) return $locations.Dequeue() }
        Assert-Equal `
            (Read-DemoReadyValue `
                -Prompt 'Location' `
                -Default 'switzerlandnorth' `
                -ValidationPattern '^[a-z0-9]+$' `
                -InputProvider $locationInput) `
            'swedencentral' `
            'The guided location helper accepted an invalid value.'

        $script:mockNativeCalls.Clear()
        Invoke-WithMockedFunction `
            -Functions @{
                'Invoke-DemoReadyNative' = {
                    param(
                        $FilePath, $Arguments, $WorkingDirectory, $LogPath, $Environment,
                        $SensitiveValues, [switch]$CaptureOutput,
                        [switch]$PreserveCapturedOutput, [switch]$Quiet
                    )
                    return '{"id":"11111111-1111-1111-1111-111111111234","name":"Demo subscription","state":"Enabled","tenantId":"22222222-2222-2222-2222-222222222222"}'
                }
            } `
            -Action {
                $summary = Get-DemoReadyAzureSubscriptionSummary -RepositoryRoot $root
                Assert-Equal $summary.DisplayId '********-1234' 'The subscription identifier was not masked.'
                Assert-True (-not $summary.DisplayId.Contains(
                    '11111111-1111-1111-1111-111111111234',
                    [StringComparison]::Ordinal)) 'The displayed subscription identifier was not masked.'
            }
}

Test-Case 'Guided step numbers match the exact order that startup performs them' {
    $plan = Get-DemoReadyGuidedStepPlan
    Assert-Equal (@($plan.Keys) -join ',') 'Subscription,Selection,Locations,Plan' `
        'The guided step plan does not describe the startup interaction order.'

    foreach ($hardcoded in @('-Step 1 -TotalSteps', '-Step 2 -TotalSteps', '-Step 3 -TotalSteps', '-Step 4 -TotalSteps')) {
        Assert-True (-not $guidedSource.Contains($hardcoded, [StringComparison]::Ordinal)) `
            "A guided section still hardcodes its number using '$hardcoded'."
    }

    # The orchestrator must run the guided steps in the numbered order.
    $ordered = @(
        'Write-DemoReadyBanner',
        'Confirm-DemoReadyAzureSubscription',
        'Read-DemoReadyGuidedSelection',
        'Read-DemoReadyGuidedLocations',
        'Show-DemoReadyDeploymentPlan'
    )
    $previous = -1
    foreach ($call in $ordered) {
        $index = $invokeSource.IndexOf($call, [StringComparison]::Ordinal)
        Assert-True ($index -ge 0) "The startup command never calls $call."
        Assert-True ($index -gt $previous) `
            "The startup command calls $call out of the guided order."
        $previous = $index
    }

    # The rendered headings must count upward without a gap or repeat.
    $answers = [Collections.Generic.Queue[string]]::new()
    foreach ($answer in @('y', 'y', 'n', 'n', 'n', 'n', 'n', '')) { $answers.Enqueue($answer) }
    $reply = { param($Prompt) return $answers.Dequeue() }
    $subscription = [pscustomobject]@{
        Name = 'Demo subscription'
        Id = '11111111-1111-1111-1111-111111111234'
        DisplayId = '********-1234'
        DisplayTenantId = '********-2222'
    }
    $rendered = & {
        Write-DemoReadyBanner -Title 'Public Sector Agent Demos - demo ready'
        Confirm-DemoReadyAzureSubscription -Subscription $subscription -InputProvider $reply
        $selection = Read-DemoReadyGuidedSelection -InputProvider $reply
        $locations = Read-DemoReadyGuidedLocations `
            -Selection $selection `
            -Defaults ([ordered]@{
                Demo1 = 'swedencentral'
                Demo2 = 'swedencentral'
                Demo3 = 'swedencentral'
                Demo4 = 'swedencentral'
            }) `
            -InputProvider $reply
        $null = Show-DemoReadyDeploymentPlan `
            -Selection $selection `
            -Locations $locations `
            -EnvironmentNames (Get-DemoReadyEnvironmentNames -BaseName 'psad-demos') `
            -Subscription $subscription
    } 6>&1 | ForEach-Object { [string]$_ }

    $steps = @($rendered |
        Select-String -Pattern 'Step (\d+) of (\d+)' |
        ForEach-Object { [int]$_.Matches[0].Groups[1].Value })
    Assert-Equal ($steps -join ',') '1,2,3,4' `
        'The guided headings did not render in ascending order without a gap or repeat.'

    $lines = @($rendered)
    $bannerIndex = -1
    $firstStepIndex = -1
    for ($index = 0; $index -lt $lines.Count; $index++) {
        if ($bannerIndex -lt 0 -and $lines[$index].Contains('demo ready', [StringComparison]::Ordinal)) {
            $bannerIndex = $index
        }
        if ($firstStepIndex -lt 0 -and $lines[$index].Contains('Step 1 of', [StringComparison]::Ordinal)) {
            $firstStepIndex = $index
        }
    }
    Assert-True ($bannerIndex -ge 0 -and $firstStepIndex -gt $bannerIndex) `
        'The banner did not render before the first guided step.'
}

Test-Case 'Teardown matches the four-step guided startup experience' {
    $plan = Get-DemoReadyTeardownStepPlan
    Assert-Equal (@($plan.Keys) -join ',') 'Subscription,Selection,Optional,Plan' `
        'The teardown guided steps are incomplete or out of order.'

    foreach ($required in @(
        '[switch]$NonInteractive',
        'Test-DemoReadyInteractiveConsole',
        "Write-DemoReadyTeardownSection -Key 'Subscription'",
        'Read-DemoReadyTeardownSelection',
        "Write-DemoReadyTeardownSection -Key 'Optional'",
        'Keep Patriots and Tokens and Credits on disk?',
        'Show-DemoReadyTeardownPlan',
        '-ProcessNames @($processNames)',
        'Continue with this teardown plan?',
        '$WhatIfPreference',
        '($interactive -or $NonInteractive)',
        '$PSCmdlet.ShouldProcess'
    )) {
        Assert-True ($removeAzureSource.Contains($required, [StringComparison]::Ordinal)) `
            "The teardown guided UX is missing '$required'."
    }

    $orderedCalls = @(
        'Write-DemoReadyBanner',
        "Write-DemoReadyTeardownSection -Key 'Subscription'",
        'Read-DemoReadyTeardownSelection',
        "Write-DemoReadyTeardownSection -Key 'Optional'",
        'Show-DemoReadyTeardownPlan',
        'Continue with this teardown plan?',
        '$PSCmdlet.ShouldProcess',
        "'Stop-DemoReady.ps1'"
    )
    $previous = -1
    foreach ($call in $orderedCalls) {
        $index = $removeAzureSource.IndexOf($call, [StringComparison]::Ordinal)
        Assert-True ($index -gt $previous) "Teardown uses '$call' out of the guided order."
        $previous = $index
    }

    $answers = [Collections.Generic.Queue[string]]::new()
    foreach ($answer in @('', 'n', '', 'n')) { $answers.Enqueue($answer) }
    $selection = Read-DemoReadyTeardownSelection `
        -Defaults ([ordered]@{ Demo1 = $true; Demo2 = $true; Demo3 = $true; Demo4 = $true }) `
        -InputProvider { param($Prompt) return $answers.Dequeue() }
    Assert-True $selection.Demo1 'Teardown did not apply the recorded Demo 1 default.'
    Assert-True (-not $selection.Demo2) 'Teardown did not accept the Demo 2 exclusion.'
    Assert-True $selection.Demo3 'Teardown did not apply the recorded Demo 3 default.'
    Assert-True (-not $selection.Demo4) 'Teardown did not accept the Demo 4 exclusion.'

    $rendered = @(Show-DemoReadyTeardownPlan `
        -Selection $selection `
        -Contexts @([pscustomobject]@{
            Name = 'Demo 1 - Foundation and Ground'
            Environment = 'psad-demos-demo1'
        }) `
        -ProcessNames @('presenter', 'demo1-comparison') `
        -Subscription ([pscustomobject]@{ Name = 'Demo subscription'; DisplayId = '********-1234' }) `
        -KeepOptionalCheckouts $true `
        -Report $null `
        6>&1 | ForEach-Object { [string]$_ }) -join "`n"
    foreach ($expected in @(
        'Step 4 of 4',
        'rg-psad-demos-demo1',
        'Keep Patriots on disk.',
        'Actions in order',
        'Not removed'
    )) {
        Assert-True ($rendered.Contains($expected, [StringComparison]::Ordinal)) `
            "The complete teardown plan omits '$expected'."
    }
}

Test-Case 'Startup and validation default to the deployed environment base' {
    foreach ($source in @($invokeSource, $validateSource)) {
        Assert-True ($source.Contains(
                "[string]`$EnvironmentName = 'psad-demos'",
                [StringComparison]::Ordinal)) `
            'A demo-ready command does not default to the deployed psad-demos environment base.'
    }
}

# --------------------------------------------------- normal flow and validation

Test-Case 'The normal startup runs no test suite and no deep validation' {
    foreach ($forbidden in @(
        'dotnet test',
        "'test',",
        'PublicSectorAgentDemos.slnx',
        'Assert-DemoReadyBicep',
        'bicep build',
        '--preview',
        'app-insights query',
        "'app-insights', 'query'",
        'Wait-DemoReadyTelemetry',
        'Assert-DemoReadyApplicationInsights',
        'Assert-DemoReadyWebAppTelemetry',
        'Get-DemoReadyResourceValue',
        'Monitoring Metrics Publisher',
        'role assignment list',
        'cognitiveservices',
        'az policy',
        'policy list',
        'Invoke-Rehearsal.ps1',
        'api/warm-up',
        'Category=CloudIntegration',
        'RUN_DEMO1_CLOUD_INTEGRATION',
        'RUN_DEMO4_CLOUD_INTEGRATION',
        'ArchitectureTests'
    )) {
        Assert-True (-not $invokeSource.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) `
            "The normal startup still performs validation work for '$forbidden'."
    }
    Assert-True ($invokeSource.Contains(
            "Run scripts\Test-DemoReady.ps1 for tests, builds, and deep validation.",
            [StringComparison]::Ordinal)) `
        'The normal startup does not name the separate validation command.'
    Assert-True ($invokeSource.Contains(
            "validationCommand = 'scripts\Test-DemoReady.ps1'",
            [StringComparison]::Ordinal)) `
        'The readiness report does not name the separate validation command.'
}

Test-Case 'The normal startup deploys, prepares data, and starts every application' {
    foreach ($required in @(
        "Join-Path `$repositoryRoot 'deploy\demo1'",
        "Join-Path `$repositoryRoot 'deploy\demo4'",
        "@('provision', '--environment', `$environmentNames.Demo1, '--no-prompt')",
        "@('up', '--environment', `$environmentNames.Demo4, '--no-prompt')",
        'Publish-DemoReadyDemo1Agents',
        'Initialize-DemoReadyDemo4Application',
        'Add-DemoReadyEntraRedirectUri',
        'Get-DemoReadyHostedAgent',
        'New-DemoReadyCatalog',
        'Start-DemoReadyProcess'
    )) {
        Assert-True ($invokeSource.Contains($required, [StringComparison]::Ordinal)) `
            "The normal startup no longer performs the required step '$required'."
    }
    Assert-Equal ([regex]::Matches($invokeSource, 'Start-DemoReadyProcess `')).Count 3 `
        'The normal startup must start Demo 1, council, and Presenter.'
    Assert-Equal ([regex]::Matches($invokeSource, 'Invoke-DemoReadyExternalBuild `')).Count 0 `
        'External builds must stay inside the optional Tokens and Credits integration.'
    Assert-True ($invokeSource.Contains('Invoke-DemoReadyProjectBuild `', [StringComparison]::Ordinal)) `
        'The normal startup does not build the Presenter project before it starts it.'
}

Test-Case 'The separate validation command owns the removed engineering checks' {
    Assert-True (Test-Path -LiteralPath $validatePath -PathType Leaf) `
        'The separate validation command is absent.'
    foreach ($required in @(
        'tests\automation\Invoke-DemoReady.Tests.ps1',
        "@('restore', 'PublicSectorAgentDemos.slnx', '--configfile', 'NuGet.config')",
        "@('build', 'PublicSectorAgentDemos.slnx', '--no-restore')",
        "@('test', 'PublicSectorAgentDemos.slnx', '--no-build', '--no-restore')",
        'Assert-DemoReadyBicep',
        'Invoke-DemoReadyProjectBuild',
        "'provision', '--preview',",
        'Invoke-Rehearsal.ps1',
        "'--filter', 'Category=CloudIntegration'",
        '[switch]$RunAzurePreview',
        '[switch]$RunCloudTests'
    )) {
        Assert-True ($validateSource.Contains($required, [StringComparison]::Ordinal)) `
            "The validation command does not own '$required'."
    }
    foreach ($forbidden in @(
        'Start-DemoReadyProcess',
        'New-DemoReadyCatalog',
        'New-DemoReadyPresenterContext',
        "@('up',",
        "@('deploy',",
        'Initialize-DemoReadyDemo4Application',
        'ASPNETCORE_URLS'
    )) {
        Assert-True (-not $validateSource.Contains($forbidden, [StringComparison]::Ordinal)) `
            "The validation command deploys or starts the presentation through '$forbidden'."
    }
    Assert-Equal ([regex]::Matches($validateSource, "'provision'")).Count 1 `
        'The validation command runs a provision command that is not a preview.'
    Assert-True ($validateSource.Contains('presentationStarted = $false', [StringComparison]::Ordinal)) `
        'The validation report does not state that it started no presentation.'
    Assert-True ($validateSource.Contains('deployed = $false', [StringComparison]::Ordinal)) `
        'The validation report does not state that it deployed nothing.'
}

Test-Case 'The optional Azure checks stay behind explicit switches' {
    foreach ($guard in @(
        'if ($RunAzurePreview) {',
        'if ($RunCloudTests) {',
        'if ($RunAzurePreview -or $RunCloudTests) {'
    )) {
        Assert-True ($validateSource.Contains($guard, [StringComparison]::Ordinal)) `
            "The validation command runs an Azure check without the guard '$guard'."
    }
    Assert-True ($validateSource.IndexOf('Initialize-DemoReadyAzureContext', [StringComparison]::Ordinal) -gt
        $validateSource.IndexOf('Assert-DemoReadyBicep', [StringComparison]::Ordinal)) `
        'The validation command requires an Azure sign-in before its offline checks.'
}

Test-Case 'Parameter validation rejects an unsafe environment name' {
    & pwsh -NoProfile -File $invokePath -EnvironmentName 'INVALID!' *> $null
    Assert-True ($LASTEXITCODE -ne 0) 'The unsafe environment name was accepted.'
}

Test-Case 'Owned environment names cover four isolated deployments' {
    $names = Get-DemoReadyEnvironmentNames -BaseName 'conference-demo'
    Assert-Equal @($names.Keys).Count 4 'The owned environment set must contain four demos.'
    Assert-Equal $names.Demo1 'conference-demo-demo1' 'The Demo 1 environment name is incorrect.'
    Assert-Equal $names.Demo4 'conference-demo-demo4' 'The Demo 4 environment name is incorrect.'
    Assert-Equal $names.Demo2 'conference-demo-demo2' 'The Demo 2 environment name is incorrect.'
    Assert-Equal $names.Demo3 'conference-demo-demo3' 'The Demo 3 environment name is incorrect.'
    foreach ($name in $names.Values) {
        Assert-True ($name.Length -le 32) 'An azd environment name exceeds 32 characters.'
        foreach ($forbidden in @('coordinate', 'act', 'patriots', 'defra')) {
            Assert-True (-not $name.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) `
                "The owned environment name '$name' claims an external demo."
        }
    }
}

Test-Case 'Definitions retain legacy keys and identify bundled defaults' {
    Assert-Equal @($definitions.Keys).Count 4 'The repository definition set is incorrect.'
    Assert-Equal $definitions.defra.FolderName 'DEFRA-AI-Demos' `
        'Act must resolve from the DEFRA-AI-Demos repository.'
    Assert-Equal $definitions.defra.EnvironmentVariable 'PSAD_DEFRA_REPO_PATH' `
        'The Act environment variable is incorrect.'
    Assert-Equal $definitions.defra.BundledPath 'src\PublicSectorAgentDemos.Demo2.Act' 'Demo 2 must default to its bundle.'
    Assert-Equal $definitions.assuranceBoard.BundledPath 'src\PublicSectorAgentDemos.Demo3.Coordinate' 'Council must default to its bundle.'
    Assert-True ($null -eq $definitions.patriots.PSObject.Properties['BundledPath']) 'Patriots must remain external.'
    Assert-Equal $definitions.assuranceBoard.FolderName 'cross-gov-assurance-board-demo' `
        'Cross-Government Coordinate must resolve from cross-gov-assurance-board-demo.'
    Assert-Equal $definitions.assuranceBoard.EnvironmentVariable 'PSAD_ASSURANCE_BOARD_REPO_PATH' `
        'The Cross-Government Coordinate environment variable is incorrect.'
    Assert-Equal $definitions.patriots.FolderName 'azure-ai-mgs-patriots' `
        'Patriots must resolve from azure-ai-mgs-patriots.'
    Assert-Equal $definitions.patriots.EnvironmentVariable 'PSAD_PATRIOTS_REPO_PATH' `
        'The Patriots environment variable is incorrect.'
    Assert-True ($definitions.defra.RequiredPaths -contains 'src\Demo2.Web\Demo2.Web.csproj') `
        'The Act identity check does not require the external Demo2.Web project.'
    Assert-True (
        $definitions.assuranceBoard.RequiredPaths -contains 'data\policies\dossier-shared-ai-service.md'
    ) 'The assurance-board identity check does not require its own dossier.'
    Assert-Equal $definitions.patriots.RepositoryUrl `
        'https://github.com/garylumsden/azure-ai-mgs-patriots.git' `
        'The Patriots clone URL is not the approved repository.'
    Assert-Equal $definitions.tokensAndCredits.RepositoryUrl `
        'https://github.com/garylumsden/tokens-and-credits.git' `
        'The Tokens and Credits clone URL is not the approved repository.'
}

Test-Case 'Optional repositories are never resolved or cloned when unselected' {
    Invoke-WithMockedFunction `
        -Functions @{
            'Resolve-DemoReadyExternalRepository' = { throw 'An unselected repository was resolved.' }
            'Invoke-DemoReadyExternalClone' = { throw 'An unselected repository was cloned.' }
        } `
        -Action {
            $tokens = Resolve-DemoReadyTokensAndCredits `
                -RepositoryRoot $root `
                -ParameterPath '' `
                -SetupRepositories @{} `
                -Selected:$false
            $patriots = Resolve-DemoReadyPatriots `
                -RepositoryRoot $root `
                -ParameterPath '' `
                -SetupRepositories @{} `
                -Selected:$false
            Assert-Equal $tokens.Status 'not-configured' 'Unselected Tokens and Credits did not stay disabled.'
            Assert-Equal $patriots.Status 'not-configured' 'Unselected Patriots did not stay disabled.'
        }
}

Test-Case 'Selected optional repositories clone only to an absent sibling with argument arrays' {
    foreach ($identity in @('patriots', 'tokensAndCredits')) {
        $workspace = New-TestDirectory -Name "clone-$identity"
        $repositoryRoot = Join-Path $workspace 'PublicSectorAgentDemos'
        $null = New-Item -ItemType Directory -Path $repositoryRoot -Force
        $definition = $definitions[$identity]
        $expectedDestination = Join-Path $workspace $definition.FolderName
        $script:cloneCalls = [Collections.Generic.List[object]]::new()
        Invoke-WithMockedFunction `
            -Functions @{
                'Invoke-DemoReadyNative' = {
                    param(
                        $FilePath, $Arguments, $WorkingDirectory, $LogPath, $Environment,
                        $SensitiveValues, $CaptureOutput, $PreserveCapturedOutput, $Quiet
                    )
                    $script:cloneCalls.Add([pscustomobject]@{
                        FilePath = $FilePath
                        Arguments = @($Arguments)
                        WorkingDirectory = $WorkingDirectory
                    })
                    $null = New-FakeExternalRepository `
                        -Path $expectedDestination `
                        -Definition $definition
                }
            } `
            -Action {
                $resolved = Resolve-DemoReadyExternalRepository `
                    -Definition $definition `
                    -RepositoryRoot $repositoryRoot `
                    -ParameterPath '' `
                    -SetupRepositories @{} `
                    -ParameterAzdEnvironmentName '' `
                    -CloneIfMissing
                Assert-Equal $resolved.Path $expectedDestination 'The cloned sibling path is incorrect.'
                Assert-True $resolved.CreatedByThisRun `
                    'A successful optional clone was not recorded as created by this run.'
            }
        Assert-Equal $script:cloneCalls.Count 1 'The selected repository was not cloned exactly once.'
        $call = $script:cloneCalls[0]
        Assert-Equal $call.FilePath 'git' 'The clone did not use Git.'
        Assert-Equal ($call.Arguments -join '|') `
            "clone|--|$($definition.RepositoryUrl)|$expectedDestination" `
            'The clone arguments are not an exact argument array.'
        Assert-Equal $call.WorkingDirectory $workspace 'The clone did not run from the sibling parent.'
    }
}

Test-Case 'Selected optional repositories fall back from a stale setup path to the sibling clone' {
    foreach ($identity in @('patriots', 'tokensAndCredits')) {
        $workspace = New-TestDirectory -Name "stale-setup-$identity"
        $repositoryRoot = Join-Path $workspace 'PublicSectorAgentDemos'
        $null = New-Item -ItemType Directory -Path $repositoryRoot -Force
        $definition = $definitions[$identity]
        $sibling = Join-Path $workspace $definition.FolderName
        $script:cloneCalls = [Collections.Generic.List[object]]::new()
        Invoke-WithMockedFunction `
            -Functions @{
                'Invoke-DemoReadyNative' = {
                    param(
                        $FilePath, $Arguments, $WorkingDirectory, $LogPath, $Environment,
                        $SensitiveValues, $CaptureOutput, $PreserveCapturedOutput, $Quiet
                    )
                    $script:cloneCalls.Add([pscustomobject]@{ Arguments = @($Arguments) })
                    $null = New-FakeExternalRepository -Path $sibling -Definition $definition
                }
            } `
            -Action {
                $resolved = Resolve-DemoReadyExternalRepository `
                    -Definition $definition `
                    -RepositoryRoot $repositoryRoot `
                    -ParameterPath '' `
                    -SetupRepositories @{
                        $identity = [pscustomobject]@{
                            Path = Join-Path $workspace 'removed-checkout'
                            AzdEnvironmentName = ''
                        }
                    } `
                    -ParameterAzdEnvironmentName '' `
                    -CloneIfMissing
                Assert-Equal $resolved.Path $sibling 'A stale optional path did not fall back to the sibling checkout.'
                Assert-True $resolved.CreatedByThisRun `
                    'The stale-path fallback did not record completed clone ownership.'
            }
        Assert-Equal $script:cloneCalls.Count 1 'The stale optional path did not trigger one sibling clone.'

        Assert-Throws `
            -Action {
                Resolve-DemoReadyExternalRepository `
                    -Definition $definition `
                    -RepositoryRoot $repositoryRoot `
                    -ParameterPath (Join-Path $workspace 'mistyped-explicit-path') `
                    -SetupRepositories @{} `
                    -ParameterAzdEnvironmentName '' `
                    -CloneIfMissing
            } `
            -ExpectedFragment 'repository path does not exist' `
            -Message 'A missing explicit optional path silently fell back to the sibling checkout.'
    }
}

Test-Case 'External clone validation rejects existing and incomplete destinations' {
    $workspace = New-TestDirectory -Name 'clone-validation'
    $repositoryRoot = Join-Path $workspace 'PublicSectorAgentDemos'
    $null = New-Item -ItemType Directory -Path $repositoryRoot -Force
    $definition = $definitions.patriots
    $destination = Join-Path $workspace $definition.FolderName
    $null = New-Item -ItemType Directory -Path $destination -Force
    Assert-Throws `
        -Action {
            Invoke-DemoReadyExternalClone `
                -Definition $definition `
                -RepositoryRoot $repositoryRoot
        } `
        -ExpectedFragment 'clone destination already exists' `
        -Message 'An existing clone destination was accepted.'

    Remove-Item -LiteralPath $destination -Recurse -Force
    Invoke-WithMockedFunction `
        -Functions @{
            'Invoke-DemoReadyNative' = {
                param(
                    $FilePath, $Arguments, $WorkingDirectory, $LogPath, $Environment,
                    $SensitiveValues, $CaptureOutput, $PreserveCapturedOutput, $Quiet
                )
                $null = New-Item -ItemType Directory -Path (Join-Path $destination '.git') -Force
            }
        } `
        -Action {
            Assert-Throws `
                -Action {
                    Invoke-DemoReadyExternalClone `
                        -Definition $definition `
                        -RepositoryRoot $repositoryRoot
                } `
                -ExpectedFragment "is missing 'src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj'" `
                -Message 'An incomplete cloned repository was accepted.'
        }
}

# --------------------------------------------------------------- path resolution

Test-Case 'External path resolution prefers parameter, variable, setup file, then sibling' {
    $workspace = New-TestDirectory -Name 'precedence'
    $repositoryRoot = Join-Path $workspace 'PublicSectorAgentDemos'
    $null = New-Item -ItemType Directory -Path $repositoryRoot -Force
    $definition = Get-TestExternalDefinition $definitions.defra
    $parameterPath = New-FakeExternalRepository `
        -Path (Join-Path $workspace 'from-parameter') -Definition $definition
    $variablePath = New-FakeExternalRepository `
        -Path (Join-Path $workspace 'from-variable') -Definition $definition
    $setupFilePath = New-FakeExternalRepository `
        -Path (Join-Path $workspace 'from-setup-file') -Definition $definition
    $siblingPath = New-FakeExternalRepository `
        -Path (Join-Path $workspace $definition.FolderName) -Definition $definition
    $setupRepositories = @{
        defra = [pscustomobject]@{ Path = $setupFilePath; AzdEnvironmentName = '' }
    }

    Use-EnvironmentVariable -Values @{ PSAD_DEFRA_REPO_PATH = $variablePath } -Action {
        $fromParameter = Resolve-DemoReadyExternalRepository `
            -Definition $definition `
            -RepositoryRoot $repositoryRoot `
            -ParameterPath $parameterPath `
            -SetupRepositories $setupRepositories `
            -ParameterAzdEnvironmentName ''
        Assert-Equal $fromParameter.PathSource 'parameter' 'The parameter path was not preferred.'
        Assert-Equal $fromParameter.Path $parameterPath 'The parameter path was not resolved.'

        $fromVariable = Resolve-DemoReadyExternalRepository `
            -Definition $definition `
            -RepositoryRoot $repositoryRoot `
            -ParameterPath '' `
            -SetupRepositories $setupRepositories `
            -ParameterAzdEnvironmentName ''
        Assert-Equal $fromVariable.PathSource 'environment-variable' `
            'The environment variable did not outrank the setup file.'
        Assert-Equal $fromVariable.Path $variablePath 'The environment variable path was not resolved.'
    }

    $fromSetupFile = Resolve-DemoReadyExternalRepository `
        -Definition $definition `
        -RepositoryRoot $repositoryRoot `
        -ParameterPath '' `
        -SetupRepositories $setupRepositories `
        -ParameterAzdEnvironmentName ''
    Assert-Equal $fromSetupFile.PathSource 'setup-file' 'The setup file did not outrank the sibling folder.'
    Assert-Equal $fromSetupFile.Path $setupFilePath 'The setup-file path was not resolved.'

    $fromSibling = Resolve-DemoReadyExternalRepository `
        -Definition $definition `
        -RepositoryRoot $repositoryRoot `
        -ParameterPath '' `
        -SetupRepositories @{} `
        -ParameterAzdEnvironmentName ''
    Assert-Equal $fromSibling.PathSource 'sibling-folder' 'The sibling folder was not used as the last resort.'
    Assert-Equal $fromSibling.Path $siblingPath 'The sibling folder path was not resolved.'
    Assert-True (-not $fromSibling.CreatedByThisRun) `
        'A pre-existing optional checkout was incorrectly recorded as created by this run.'
}

Test-Case 'External path resolution rejects an unknown or invalid repository' {
    $workspace = New-TestDirectory -Name 'invalid-paths'
    $repositoryRoot = Join-Path $workspace 'PublicSectorAgentDemos'
    $null = New-Item -ItemType Directory -Path $repositoryRoot -Force
    $definition = Get-TestExternalDefinition $definitions.assuranceBoard

    Assert-Throws `
        -Action {
            Resolve-DemoReadyExternalRepository `
                -Definition $definition -RepositoryRoot $repositoryRoot `
                -ParameterPath '' -SetupRepositories @{} -ParameterAzdEnvironmentName ''
        } `
        -ExpectedFragment 'repository path is unknown' `
        -Message 'An unresolvable repository did not report the four supported sources.'

    Assert-Throws `
        -Action {
            Resolve-DemoReadyExternalRepository `
                -Definition $definition -RepositoryRoot $repositoryRoot `
                -ParameterPath (Join-Path $workspace 'absent') `
                -SetupRepositories @{} -ParameterAzdEnvironmentName ''
        } `
        -ExpectedFragment 'repository path does not exist' `
        -Message 'A requested path that does not exist was accepted.'

    $notGit = New-FakeExternalRepository `
        -Path (Join-Path $workspace 'not-git') -Definition $definition -OmitGit
    Assert-Throws `
        -Action {
            Resolve-DemoReadyExternalRepository `
                -Definition $definition -RepositoryRoot $repositoryRoot `
                -ParameterPath $notGit -SetupRepositories @{} -ParameterAzdEnvironmentName ''
        } `
        -ExpectedFragment 'is not a Git repository' `
        -Message 'A folder without Git history was accepted as an external repository.'

    $wrongIdentity = New-FakeExternalRepository `
        -Path (Join-Path $workspace 'wrong-identity') `
        -Definition $definition `
        -OmitPaths @('data\policies\dossier-shared-ai-service.md')
    Assert-Throws `
        -Action {
            Resolve-DemoReadyExternalRepository `
                -Definition $definition -RepositoryRoot $repositoryRoot `
                -ParameterPath $wrongIdentity -SetupRepositories @{} -ParameterAzdEnvironmentName ''
        } `
        -ExpectedFragment "is missing 'data\policies\dossier-shared-ai-service.md'" `
        -Message 'A repository missing a required file was accepted.'

    $patriotsAsAssurance = New-FakeExternalRepository `
        -Path (Join-Path $workspace 'azure-ai-mgs-patriots') -Definition $definitions.patriots
    Assert-Throws `
        -Action {
            Resolve-DemoReadyExternalRepository `
                -Definition $definition -RepositoryRoot $repositoryRoot `
                -ParameterPath $patriotsAsAssurance -SetupRepositories @{} `
                -ParameterAzdEnvironmentName ''
        } `
        -ExpectedFragment 'is missing' `
        -Message 'The wrong external repository passed the assurance-board identity check.'
}

# ------------------------------------------------------------------- setup file

Test-Case 'A missing setup file resolves to an empty repository set' {
    $workspace = New-TestDirectory -Name 'setup-absent'
    $repositories = Read-DemoReadySetupFile `
        -Path (Join-Path $workspace 'repositories.local.json') `
        -SchemaPath $schemaPath
    Assert-Equal @($repositories.Keys).Count 0 'An absent setup file did not resolve to an empty set.'
}

Test-Case 'The setup file rejects malformed content' {
    $workspace = New-TestDirectory -Name 'setup-malformed'
    $cases = @(
        [pscustomobject]@{
            Name = 'not-json.json'
            Content = 'this file is not JSON'
            Fragment = 'does not match'
        },
        [pscustomobject]@{
            Name = 'wrong-version.json'
            Content = '{ "version": 2, "repositories": {} }'
            Fragment = 'does not match'
        },
        [pscustomobject]@{
            Name = 'unknown-key.json'
            Content = '{ "version": 1, "repositories": { "unknownDemo": { "path": "C:\\demo" } } }'
            Fragment = 'does not match'
        },
        [pscustomobject]@{
            Name = 'missing-path.json'
            Content = '{ "version": 1, "repositories": { "defra": { "azdEnvironment": "demo" } } }'
            Fragment = 'does not match'
        },
        [pscustomobject]@{
            Name = 'blank-path.json'
            Content = '{ "version": 1, "repositories": { "defra": { "path": "   " } } }'
            Fragment = 'must give a path'
        }
    )
    foreach ($case in $cases) {
        $path = New-TestFile -Path (Join-Path $workspace $case.Name) -Content $case.Content
        Assert-Throws `
            -Action { Read-DemoReadySetupFile -Path $path -SchemaPath $schemaPath } `
            -ExpectedFragment $case.Fragment `
            -Message "The malformed setup file '$($case.Name)' was accepted."
    }
}

Test-Case 'The setup file rejects credential material' {
    $workspace = New-TestDirectory -Name 'setup-secret'
    foreach ($marker in @(
        'InstrumentationKey=00000000-0000-0000-0000-000000000000',
        'AccountKey=synthetic',
        'clientSecret',
        'ApiKey',
        'Bearer synthetic',
        'PRIVATE KEY'
    )) {
        $content = @"
{
  "version": 1,
  "repositories": {
    "defra": { "path": "C:\\demo\\DEFRA-AI-Demos", "azdEnvironment": "demo" }
  },
  "note": "$marker"
}
"@
        $path = New-TestFile -Path (Join-Path $workspace 'repositories.local.json') -Content $content
        Assert-True (Test-DemoReadySetupFileSecret -Content $content) `
            "The secret marker '$marker' was not detected."
        Assert-Throws `
            -Action { Read-DemoReadySetupFile -Path $path -SchemaPath $schemaPath } `
            -ExpectedFragment 'must be secret-free' `
            -Message "The setup file carrying '$marker' was accepted."
    }
}

Test-Case 'The setup file supplies paths and an azd environment name' {
    $workspace = New-TestDirectory -Name 'setup-valid'
    $content = @'
{
  "version": 1,
  "repositories": {
    "defra": { "path": "C:\\demo\\DEFRA-AI-Demos" },
    "assuranceBoard": {
      "path": "C:\\demo\\cross-gov-assurance-board-demo",
      "azdEnvironment": "assurance-demo"
    }
  }
}
'@
    $path = New-TestFile -Path (Join-Path $workspace 'repositories.local.json') -Content $content
    $repositories = Read-DemoReadySetupFile -Path $path -SchemaPath $schemaPath
    Assert-Equal @($repositories.Keys).Count 2 'The setup file did not resolve both repositories.'
    Assert-Equal $repositories['defra'].Path 'C:\demo\DEFRA-AI-Demos' 'The Act path is incorrect.'
    Assert-Equal $repositories['defra'].AzdEnvironmentName '' `
        'An omitted azd environment did not stay empty.'
    Assert-Equal $repositories['assuranceBoard'].AzdEnvironmentName 'assurance-demo' `
        'The setup-file azd environment name is incorrect.'
}

Test-Case 'A parameter overrides the setup-file azd environment' {
    $workspace = New-TestDirectory -Name 'environment-override'
    $repositoryRoot = Join-Path $workspace 'PublicSectorAgentDemos'
    $null = New-Item -ItemType Directory -Path $repositoryRoot -Force
    $definition = Get-TestExternalDefinition $definitions.defra
    $repositoryPath = New-FakeExternalRepository `
        -Path (Join-Path $workspace 'DEFRA-AI-Demos') -Definition $definition
    $setupRepositories = @{
        defra = [pscustomobject]@{ Path = $repositoryPath; AzdEnvironmentName = 'from-setup-file' }
    }

    $fromSetupFile = Resolve-DemoReadyExternalRepository `
        -Definition $definition -RepositoryRoot $repositoryRoot -ParameterPath '' `
        -SetupRepositories $setupRepositories -ParameterAzdEnvironmentName ''
    Assert-Equal $fromSetupFile.AzdEnvironmentName 'from-setup-file' `
        'The setup-file azd environment was not used.'
    Assert-Equal $fromSetupFile.AzdEnvironmentSource 'setup-file' `
        'The setup-file azd environment source is incorrect.'

    $fromParameter = Resolve-DemoReadyExternalRepository `
        -Definition $definition -RepositoryRoot $repositoryRoot -ParameterPath '' `
        -SetupRepositories $setupRepositories -ParameterAzdEnvironmentName 'from-parameter'
    Assert-Equal $fromParameter.AzdEnvironmentName 'from-parameter' `
        'The parameter did not override the setup-file azd environment.'
    Assert-Equal $fromParameter.AzdEnvironmentSource 'parameter' `
        'The parameter azd environment source is incorrect.'

    $withoutName = Resolve-DemoReadyExternalRepository `
        -Definition $definition -RepositoryRoot $repositoryRoot -ParameterPath $repositoryPath `
        -SetupRepositories @{} -ParameterAzdEnvironmentName ''
    Assert-Equal $withoutName.AzdEnvironmentSource 'azd-default' `
        'An unnamed environment does not defer to the azd default.'
}

# ------------------------------------------------------- external azd environment

Test-Case 'External azd environment resolution handles missing, default, and ambiguous states' {
    $repository = [pscustomobject]@{
        Identity = 'assuranceBoard'
        DisplayName = 'Cross-Government Coordinate (cross-gov-assurance-board-demo)'
        Path = $root
        PathSource = 'parameter'
        AzdEnvironmentName = ''
        AzdEnvironmentSource = 'azd-default'
    }
    Invoke-WithMockedFunction `
        -Functions @{ 'Invoke-DemoReadyAzd' = $MockInvokeDemoReadyAzd } `
        -Action {
            $script:mockAzdOutput = '[]'
            Assert-Throws `
                -Action { Resolve-DemoReadyExternalAzdEnvironmentName -Repository $repository } `
                -ExpectedFragment 'has no azd environment' `
                -Message 'A repository with no azd environment was accepted.'

            $script:mockAzdOutput = '[{"Name":"one","IsDefault":false},{"Name":"two","IsDefault":false}]'
            Assert-Throws `
                -Action { Resolve-DemoReadyExternalAzdEnvironmentName -Repository $repository } `
                -ExpectedFragment 'has no default azd environment' `
                -Message 'A repository without a default azd environment was accepted.'

            $script:mockAzdOutput = '[{"Name":"one","IsDefault":true},{"Name":"two","IsDefault":true}]'
            Assert-Throws `
                -Action { Resolve-DemoReadyExternalAzdEnvironmentName -Repository $repository } `
                -ExpectedFragment 'more than one default azd environment' `
                -Message 'An ambiguous default azd environment was accepted.'

            $script:mockAzdOutput = '[{"Name":"one","IsDefault":true},{"Name":"two","IsDefault":false}]'
            Assert-Equal `
                (Resolve-DemoReadyExternalAzdEnvironmentName -Repository $repository) `
                'one' `
                'The single default azd environment was not selected.'

            $named = $repository.PSObject.Copy()
            $named.AzdEnvironmentName = 'two'
            Assert-Equal `
                (Resolve-DemoReadyExternalAzdEnvironmentName -Repository $named) `
                'two' `
                'The requested azd environment was not selected.'

            $missing = $repository.PSObject.Copy()
            $missing.AzdEnvironmentName = 'absent'
            Assert-Throws `
                -Action { Resolve-DemoReadyExternalAzdEnvironmentName -Repository $missing } `
                -ExpectedFragment "has no azd environment named 'absent'" `
                -Message 'A requested azd environment that does not exist was accepted.'
            $script:mockAzdOutput = ''
        }
}

Test-Case 'Azd environment values ignore the CLI update notice only' {
    Invoke-WithMockedFunction `
        -Functions @{ 'Invoke-DemoReadyAzd' = $MockInvokeDemoReadyAzd } `
        -Action {
            $script:mockAzdOutput = @(
                'Update available: 1.32.0 -> 1.33.0 (https://example.invalid/release)'
                'To update, run `winget upgrade Microsoft.Azd`'
                'EXAMPLE_VALUE="expected"'
            ) -join [Environment]::NewLine
            $values = Get-DemoReadyAzdValues -ContextPath $root -EnvironmentName 'test'
            Assert-Equal $values.EXAMPLE_VALUE 'expected' `
                'The azd update notice prevented environment value parsing.'

            $script:mockAzdOutput = 'unexpected advisory text'
            Assert-Throws `
                -Action { Get-DemoReadyAzdValues -ContextPath $root -EnvironmentName 'test' } `
                -ExpectedFragment 'returned an invalid line' `
                -Message 'An unknown azd output line was silently ignored.'
            $script:mockAzdOutput = ''
        }
}

# ------------------------------------------------------------ Git preservation

Test-Case 'A dirty external repository keeps its exact Git baseline' {
    $workspace = New-TestDirectory -Name 'git-preservation'
    $repositoryPath = New-GitRepository -Path (Join-Path $workspace 'external')
    $null = New-TestFile -Path (Join-Path $repositoryPath 'work-in-progress.md') -Content '# dirty'

    $baseline = Get-DemoReadyGitStatus -RepositoryPath $repositoryPath
    Assert-True (-not [string]::IsNullOrWhiteSpace($baseline)) `
        'The synthetic external repository was not dirty.'
    Assert-True ($baseline.Contains('work-in-progress.md', [StringComparison]::Ordinal)) `
        'The Git baseline does not record the existing local change.'

    Assert-DemoReadyGitStatusPreserved `
        -RepositoryPath $repositoryPath `
        -Baseline $baseline `
        -DisplayName 'Synthetic external demo'

    $null = New-TestFile -Path (Join-Path $repositoryPath 'orchestrator-wrote-this.md') -Content '# drift'
    Assert-Throws `
        -Action {
            Assert-DemoReadyGitStatusPreserved `
                -RepositoryPath $repositoryPath `
                -Baseline $baseline `
                -DisplayName 'Synthetic external demo'
        } `
        -ExpectedFragment 'repository Git status changed' `
        -Message 'Actual external repository drift was not detected.'
}

# ----------------------------------------------------------- package feed proxy

Test-Case 'External restores use the Microsoft package feed proxy' {
    $proxyUri = Get-DemoReadyNuGetProxyUri
    Assert-Equal $proxyUri 'https://packagefeedproxy.microsoft.io/nuget/v3/index.json' `
        'The Microsoft package feed proxy URI is incorrect.'

    $workspace = New-TestDirectory -Name 'restore-arguments'
    $withoutConfig = Join-Path $workspace 'no-config'
    $null = New-Item -ItemType Directory -Path $withoutConfig -Force
    $sourceArguments = Get-DemoReadyRestoreArgument `
        -RepositoryPath $withoutConfig `
        -ProjectPath 'src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj'
    Assert-True ($sourceArguments -contains '--source') `
        'A repository without NuGet.config does not force the package feed proxy.'
    Assert-True ($sourceArguments -contains $proxyUri) `
        'A repository without NuGet.config does not use the Microsoft package feed proxy.'

    $withProxyConfig = Join-Path $workspace 'proxy-config'
    $null = New-TestFile `
        -Path (Join-Path $withProxyConfig 'NuGet.config') `
        -Content "<configuration><packageSources><add key=`"proxy`" value=`"$proxyUri`" /></packageSources></configuration>"
    $configArguments = Get-DemoReadyRestoreArgument `
        -RepositoryPath $withProxyConfig `
        -ProjectPath 'src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj'
    Assert-True ($configArguments -contains '--configfile') `
        'An external NuGet.config using the proxy was not reused.'

    $withPublicConfig = Join-Path $workspace 'public-config'
    $null = New-TestFile `
        -Path (Join-Path $withPublicConfig 'NuGet.config') `
        -Content '<configuration><packageSources><add key="nuget" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>'
    Assert-Throws `
        -Action {
            Get-DemoReadyRestoreArgument `
                -RepositoryPath $withPublicConfig `
                -ProjectPath 'src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj'
        } `
        -ExpectedFragment 'must use the Microsoft package feed proxy' `
        -Message 'An external NuGet.config that bypasses the proxy was accepted.'
}

Test-Case 'The external build restores through the proxy and preserves Git status' {
    $workspace = New-TestDirectory -Name 'external-build'
    $repositoryPath = New-GitRepository -Path (Join-Path $workspace 'external')
    $null = New-TestFile -Path (Join-Path $repositoryPath 'existing-local-change.md') -Content '# dirty'
    $projectPath = Join-Path $repositoryPath 'src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj'
    $null = New-TestFile -Path $projectPath -Content '<Project />'
    $baseline = Get-DemoReadyGitStatus -RepositoryPath $repositoryPath
    $repository = [pscustomobject]@{
        Identity = 'assuranceBoard'
        DisplayName = 'Cross-Government Coordinate (cross-gov-assurance-board-demo)'
        Path = $repositoryPath
    }

    $script:mockNativeCalls.Clear()
    Invoke-WithMockedFunction `
        -Functions @{ 'Invoke-DemoReadyNative' = $MockInvokeDemoReadyNative } `
        -Action {
            Invoke-DemoReadyExternalBuild `
                -Repository $repository `
                -ProjectPath $projectPath `
                -LogRoot (Join-Path $workspace 'logs') `
                -GitBaseline $baseline
        }

    Assert-Equal $script:mockNativeCalls.Count 2 'The external build did not restore and build once each.'
    $restore = $script:mockNativeCalls[0]
    $build = $script:mockNativeCalls[1]
    Assert-Equal $restore.FilePath 'dotnet' 'The external restore does not use the .NET CLI.'
    Assert-True ($restore.Arguments -contains (Get-DemoReadyNuGetProxyUri)) `
        'The external restore does not use the Microsoft package feed proxy.'
    Assert-True ($build.Arguments -contains '--no-restore') `
        'The external build repeats the restore.'
    Assert-Equal $build.WorkingDirectory $repositoryPath `
        'The external build does not run inside its own repository.'
    Assert-True (-not $externalSource.Contains('git -C $RepositoryPath checkout', [StringComparison]::Ordinal)) `
        'The external helper can change an external working tree.'

    $current = Get-DemoReadyGitStatus -RepositoryPath $repositoryPath
    Assert-Equal $current $baseline 'The external build changed the external repository.'
}

# -------------------------------------------------------------- external Act

Test-Case 'Act uses the endpoint published by the bundled Demo 2 deployment' {
    Assert-True ([regex]::IsMatch(
            $invokeSource,
            'Get-DemoReadyAzdValues\s+`\s*\r?\n\s*\$contexts\.Demo2\s+\$environmentNames\.Demo2')) `
        'Act must read outputs from the selected owned environment.'
    Assert-True ($invokeSource.Contains("`$demo2Values['DEMO2_WEB_URL']", [StringComparison]::Ordinal)) `
        'Act does not read DEMO2_WEB_URL.'
    Assert-True ($invokeSource.Contains(
            "`$actUri.Scheme -ne 'https'",
            [StringComparison]::Ordinal)) `
        'The external Act endpoint is not required to use HTTPS.'
    Assert-True ($invokeSource.Contains('-ActWebUrl $actEndpoint', [StringComparison]::Ordinal)) `
        'The external Act endpoint does not reach the generated Presenter catalog.'
    foreach ($forbidden in @(
        'deploy\demo2',
        'infra\demo2-act.bicep',
        'ACT_WEB_ENTRA_CLIENT_SECRET',
        'Category=CoordinateSeed'
    )) {
        Assert-True (-not $invokeSource.Contains($forbidden, [StringComparison]::Ordinal)) `
            "The startup script still performs internal work for '$forbidden'."
    }
}

Test-Case 'Startup never deploys or provisions an external repository' {
    foreach ($repositoryVariable in @('$patriots', '$patriotsLink.Repository', '$tokensIntegration.Repository')) {
        foreach ($command in @("'up'", "'provision'", "'deploy'", "'down'")) {
            $forbidden = "-Arguments @($command"
            $index = 0
            while ($true) {
                $index = $invokeSource.IndexOf($forbidden, $index, [StringComparison]::Ordinal)
                if ($index -lt 0) {
                    break
                }
                $window = $invokeSource.Substring(
                    $index,
                    [Math]::Min(400, $invokeSource.Length - $index))
                Assert-True (-not $window.Contains(
                        "-WorkingDirectory $repositoryVariable.Path",
                        [StringComparison]::Ordinal)) `
                    "The startup script runs azd $command inside $repositoryVariable."
                $index += $forbidden.Length
            }
        }
    }
    Assert-True ($invokeSource.Contains('deployedByThisRepository = $false', [StringComparison]::Ordinal)) `
        'The readiness report does not state external deployment ownership.'
    Assert-Equal `
        ([regex]::Matches($invokeSource, 'deployedByThisRepository = \$false')).Count `
        2 `
        'Patriots and Tokens and Credits must never be deployed by this repository.'
}

# ------------------------------------------------------- local council processes

Test-Case 'Council startup preserves restored debate rounds and the engine default' {
    Assert-True (-not $invokeSource.Contains('COUNCIL_DEBATE_MAX_ROUNDS', [StringComparison]::OrdinalIgnoreCase)) `
        'The startup script overrides the restored debate-round setting or the engine default.'
    Assert-True ($invokeSource.Contains(
            '$assuranceEnvironmentValues[$entry.Key] = [string]$entry.Value',
            [StringComparison]::Ordinal)) `
        'The startup script does not preserve the restored council environment values.'
}

Test-Case 'The council applications use their fixed ports and central demo dossiers' {
    Assert-True ($invokeSource.Contains("`$assuranceHttpUrl = 'http://localhost:5080/'", [StringComparison]::Ordinal)) `
        'Cross-Government Coordinate does not use HTTP port 5080.'
    Assert-True ($invokeSource.Contains("`$assuranceHttpsUrl = 'https://localhost:7080/'", [StringComparison]::Ordinal)) `
        'Cross-Government Coordinate does not use HTTPS port 7080.'
    Assert-True ($invokeSource.Contains("`$patriotsHttpUrl = 'http://localhost:5081/'", [StringComparison]::Ordinal)) `
        'Patriots does not use HTTP port 5081.'
    Assert-True ($invokeSource.Contains("`$patriotsHttpsUrl = 'https://localhost:7081/'", [StringComparison]::Ordinal)) `
        'Patriots does not use HTTPS port 7081.'
    Assert-True ($invokeSource.Contains(
            "ASPNETCORE_URLS = 'https://localhost:7080;http://localhost:5080'",
            [StringComparison]::Ordinal)) `
        'The Cross-Government Coordinate process does not bind 5080 and 7080.'
    Assert-True ($patriotsSource.Contains(
            "ASPNETCORE_URLS = 'https://localhost:7081;http://localhost:5081'",
            [StringComparison]::Ordinal)) `
        'Selected Patriots does not bind HTTP 5081 and HTTPS 7081.'
    foreach ($required in @(
        'if ($selection.Demo1) { $ports.Add(5090) }',
        'if ($selection.TokensAndCredits) { $ports.Add(5041) }',
        '$ports.Add(5081)',
        '$ports.Add(7081)'
    )) {
        Assert-True ($invokeSource.Contains($required, [StringComparison]::Ordinal)) `
            "The selected port plan is missing '$required'."
    }
    Assert-True ($invokeSource.Contains("`$presenterUrl = 'http://localhost:5088/'", [StringComparison]::Ordinal)) `
        'The Presenter does not use HTTP port 5088.'
    Assert-True ($invokeSource.Contains('PRESENTER_SESSION_CATALOG_PATH = $catalogPath',
            [StringComparison]::Ordinal)) `
        'The Presenter does not read the generated session catalog.'
    foreach ($endpoint in @(
        'Wait-DemoReadyEndpoint $assuranceHttpUrl',
        'Wait-DemoReadyEndpoint $assuranceHttpsUrl',
        'Wait-DemoReadyEndpoint $presenterUrl'
    )) {
        Assert-True ($invokeSource.Contains($endpoint, [StringComparison]::Ordinal)) `
            "The startup script does not confirm readiness with '$endpoint'."
    }

    Assert-True ($invokeSource.Contains(
            "`$demoDossierRoot = Join-Path `$repositoryRoot 'demo-dossiers'",
            [StringComparison]::Ordinal)) `
        'The startup script does not use the central demo dossier folder.'
    Assert-True ($invokeSource.Contains(
            "Join-Path `$demoDossierRoot 'cross-government\dossier-shared-ai-service.md'",
            [StringComparison]::Ordinal)) `
        'The assurance session does not use its central demo dossier.'
    Assert-True ($invokeSource.Contains(
            "Join-Path `$demoDossierRoot 'patriots\01-puppet-president.md'",
            [StringComparison]::Ordinal)) `
        'The Patriots session does not use its central demo dossier.'
    foreach ($dossierName in @(
        'cross-government\dossier-shared-ai-service.md',
        'cross-government\dossier-case-management-consolidation.md',
        'cross-government\dossier-emergency-housing-triage.md',
        'cross-government\dossier-grants-fraud-analytics.md',
        'cross-government\dossier-shared-identity-verification.md',
        'patriots\01-puppet-president.md',
        'patriots\02-mandatory-trending-topic.md',
        'patriots\03-official-history-patch.md',
        'patriots\04-approved-opinion-of-the-month.md',
        'patriots\05-emoji-context-directive.md',
        'patriots\06-operation-eternal-sunset.md',
        'patriots\07-single-global-ringtone.md',
        'patriots\08-caffeine-compliance-directive.md',
        'patriots\09-single-sanctioned-meme-format.md',
        'patriots\10-retroactive-spoiler-directive.md',
        'patriots\11-national-small-talk-script.md',
        'patriots\12-metal-gear-solace.md',
        'patriots\13-presidential-selection.md',
        'patriots\14-single-sanctioned-anthem.md',
        'patriots\15-combat-simulation-readiness.md',
        'patriots\16-operation-unbroken-circle.md'
    )) {
        Assert-True (Test-Path -LiteralPath (Join-Path $root "demo-dossiers\$dossierName") -PathType Leaf) `
            "The central demo dossier '$dossierName' is missing."
    }
    Assert-Equal @(Get-ChildItem -LiteralPath (Join-Path $root 'demo-dossiers') -File -Filter '*.md').Count `
        1 'Only the dossier index should remain at the root of demo-dossiers.'
    Assert-True ($presenterSource.Contains('[AllowEmptyString()][string]$PatriotsDossierPath',
            [StringComparison]::Ordinal)) `
        'The Presenter context does not take a separate Patriots dossier.'
}

Test-Case 'Local applications use Azure CLI authentication' {
    Assert-Equal `
        ([regex]::Matches($invokeSource, "AZURE_TOKEN_CREDENTIALS\s*=\s*'AzureCliCredential'")).Count `
        2 `
        'The council and Presenter processes do not use deterministic local Azure CLI authentication.'
}

# --------------------------------------------------------------- Presenter seam

Test-Case 'The Presenter context validates the Act URL and both local dossiers' {
    $workspace = New-TestDirectory -Name 'presenter-context'
    $assuranceDossier = New-TestFile `
        -Path (Join-Path $workspace 'cross-gov\data\policies\dossier-shared-ai-service.md') `
        -Content '# synthetic assurance dossier'
    $patriotsDossier = New-TestFile `
        -Path (Join-Path $workspace 'patriots\data\policies\01-puppet-president.md') `
        -Content '# synthetic patriots dossier'
    $arguments = @{
        Demo1Values = @{}
        Demo4Values = @{}
        HostedAgent = [pscustomobject]@{ playground_url = 'https://ai.azure.com/' }
        ActWebUrl = 'https://act.example.invalid'
        AssuranceDossierPath = $assuranceDossier
        AssuranceHttpUrl = 'http://localhost:5080/'
        AssuranceHttpsUrl = 'https://localhost:7080/'
        PatriotsHttpUrl = 'http://localhost:5081/'
        PatriotsHttpsUrl = 'https://localhost:7081/'
        PatriotsDossierPath = $patriotsDossier
        RepositoryRoot = $root
    }

    $context = New-DemoReadyPresenterContext @arguments
    Assert-Equal $context.ActWebUrl 'https://act.example.invalid' 'The Act URL was not normalised.'
    Assert-True ($context.AssuranceDossierPath -cne $context.PatriotsDossierPath) `
        'Both councils resolved to the same dossier.'
    Assert-True ($context.HostedAgentVsCodeUri.StartsWith('vscode://file/', [StringComparison]::Ordinal)) `
        'The hosted-agent VS Code URI does not use the vscode://file scheme.'
    Assert-True ([regex]::IsMatch($context.HostedAgentVsCodeUri, '^vscode://file/[A-Za-z]:/')) `
        'The hosted-agent VS Code URI does not preserve an absolute drive path.'
    Assert-True ($context.HostedAgentVsCodeUri.EndsWith(
            'PublicSectorAgentDemos.Demo4.HostedAgents', [StringComparison]::Ordinal)) `
        'The hosted-agent VS Code URI does not point at the hosted-agent source directory.'
    Assert-True (-not $context.HostedAgentVsCodeUri.Contains('\', [StringComparison]::Ordinal)) `
        'The hosted-agent VS Code URI still contains a backslash.'

    $insecure = $arguments.Clone()
    $insecure.ActWebUrl = 'http://act.example.invalid'
    Assert-Throws `
        -Action { New-DemoReadyPresenterContext @insecure } `
        -ExpectedFragment 'must use HTTPS' `
        -Message 'A plain HTTP Act URL was accepted.'

    $missingPatriots = $arguments.Clone()
    $missingPatriots.PatriotsDossierPath = Join-Path $workspace 'patriots\data\policies\absent.md'
    Assert-Throws `
        -Action { New-DemoReadyPresenterContext @missingPatriots } `
        -ExpectedFragment 'Patriots dossier was not found' `
        -Message 'A missing Patriots dossier was accepted.'

    $missingAssurance = $arguments.Clone()
    $missingAssurance.AssuranceDossierPath = Join-Path $workspace 'cross-gov\data\policies\absent.md'
    Assert-Throws `
        -Action { New-DemoReadyPresenterContext @missingAssurance } `
        -ExpectedFragment 'Cross-Government dossier was not found' `
        -Message 'A missing assurance dossier was accepted.'

    $missingRepositoryRoot = $arguments.Clone()
    $missingRepositoryRoot.RepositoryRoot = Join-Path $workspace 'no-such-repository'
    Assert-Throws `
        -Action { New-DemoReadyPresenterContext @missingRepositoryRoot } `
        -ExpectedFragment 'hosted-agent source directory was not found' `
        -Message 'A missing hosted-agent source directory was accepted.'
}

Test-Case 'The VS Code URI builder replaces backslashes, preserves the drive, and encodes segments' {
    Assert-Equal `
        (ConvertTo-DemoReadyVsCodeFileUri -AbsolutePath 'C:\repo\src\PublicSectorAgentDemos.Demo4.HostedAgents') `
        'vscode://file/C:/repo/src/PublicSectorAgentDemos.Demo4.HostedAgents' `
        'The VS Code URI does not use forward slashes and a preserved drive colon.'
    Assert-Equal `
        (ConvertTo-DemoReadyVsCodeFileUri -AbsolutePath 'C:\repo with space\src') `
        'vscode://file/C:/repo%20with%20space/src' `
        'The VS Code URI does not percent-encode a path segment.'
    Assert-Throws `
        -Action { ConvertTo-DemoReadyVsCodeFileUri -AbsolutePath '\\server\share\repo' } `
        -ExpectedFragment 'absolute Windows drive path' `
        -Message 'A UNC path was accepted as a drive path.'
}

Test-Case 'The generated catalog keeps six ordered sessions and replaces every endpoint' {
    $workspace = New-TestDirectory -Name 'presenter-catalog'
    $assuranceDossier = New-TestFile `
        -Path (Join-Path $workspace 'cross-gov\data\policies\dossier-shared-ai-service.md') `
        -Content '# synthetic assurance dossier'
    $patriotsDossier = New-TestFile `
        -Path (Join-Path $workspace 'patriots\data\policies\01-puppet-president.md') `
        -Content '# synthetic patriots dossier'
    $context = New-DemoReadyPresenterContext `
        -Demo1Values @{
            AZURE_AI_SERVICES_ENDPOINT = 'https://demo.services.ai.azure.com/'
            AZURE_AI_PROJECT_ID = '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-demo1/providers/Microsoft.CognitiveServices/accounts/demo-account/projects/demo-project'
            COUNCIL_FAST_MODEL = 'gpt-test'
        } `
        -Demo4Values @{ DEMO4_APPLICATION_NAME = 'hosted-example' } `
        -HostedAgent ([pscustomobject]@{
            playground_url = 'https://ai.azure.com/nextgen/r/workspace-key,rg-demo4,,hosted-account,hosted-project/build/agents/hosted-agent/build?version=4'
        }) `
        -ActWebUrl 'https://act-external.azurewebsites.net/' `
        -AssuranceDossierPath $assuranceDossier `
        -AssuranceHttpUrl 'http://localhost:5080/' `
        -AssuranceHttpsUrl 'https://localhost:7080/' `
        -PatriotsHttpUrl 'http://localhost:5081/' `
        -PatriotsHttpsUrl 'https://localhost:7081/' `
        -PatriotsDossierPath $patriotsDossier `
        -PatriotsConfigured $true `
        -RepositoryRoot $root

    $destination = Join-Path $workspace 'sessions.generated.json'
    $catalog = New-DemoReadyCatalog `
        -SourcePath $committedCatalogPath `
        -DestinationPath $destination `
        -Context $context

    $order = @($catalog.sessions | ForEach-Object { [string]$_.id })
    Assert-Equal $order.Count 6 'The generated catalog session count is incorrect.'
    Assert-Equal ($order -join ',') `
        'foundation,ground,act,cross-government-coordinate,patriots-coordinate,hosted' `
        'The generated catalog session order is incorrect.'

    $sessions = @{}
    foreach ($session in $catalog.sessions) {
        $sessions[[string]$session.id] = $session
    }
    $agentBaseUrl = 'https://ai.azure.com/nextgen/r/workspace-key,rg-demo1,,demo-account,demo-project/build/agents'
    Assert-Equal $sessions['foundation'].launchUrl `
        'http://localhost:5090/' 'The Foundation launch URL is incorrect.'
    Assert-Equal $sessions['ground'].launchUrl `
        'http://localhost:5090/' 'The Ground launch URL is incorrect.'
    Assert-Equal $sessions['foundation'].links[0].url `
        "$agentBaseUrl/managing-public-money-foundation/build" 'The Foundation auxiliary link is incorrect.'
    Assert-Equal $sessions['ground'].links[0].url `
        "$agentBaseUrl/managing-public-money-ground/build" 'The Ground auxiliary link is incorrect.'
    Assert-Equal $sessions['foundation'].warmUp.endpoint `
        'https://demo.services.ai.azure.com/openai/v1/responses' 'The Foundation warm-up endpoint is incorrect.'
    Assert-Equal $sessions['act'].launchUrl 'https://act-external.azurewebsites.net/' `
        'The Act launch URL does not use the external endpoint.'
    Assert-Equal $sessions['act'].warmUp.endpoint 'https://act-external.azurewebsites.net/health' `
        'The Act warm-up endpoint does not use the external endpoint.'
    Assert-Equal $sessions['cross-government-coordinate'].launchUrl 'http://localhost:5080/dossiers' `
        'The Cross-Government Coordinate launch URL is incorrect.'
    Assert-Equal $sessions['cross-government-coordinate'].warmUp.endpoint 'http://localhost:5080/' `
        'The Cross-Government Coordinate warm-up endpoint is incorrect.'
    Assert-Equal $sessions['cross-government-coordinate'].input (
        "Upload $assuranceDossier. " +
        "Alternative Cross-Government dossiers are in $(Split-Path -Parent $assuranceDossier).") `
        'The Cross-Government Coordinate input does not use its own dossier.'
    Assert-Equal $sessions['patriots-coordinate'].launchUrl 'https://localhost:7081/' `
        'The Patriots launch URL is incorrect.'
    Assert-Equal $sessions['patriots-coordinate'].warmUp.endpoint 'http://localhost:5081/' `
        'The Patriots warm-up endpoint is incorrect.'
    Assert-Equal $sessions['patriots-coordinate'].input "Upload $patriotsDossier" `
        'The Patriots input does not use its own dossier.'
    Assert-Equal $sessions['hosted'].launchUrl 'https://hosted-example.azurewebsites.net/' `
        'The Hosted launch URL is incorrect.'
    Assert-Equal $sessions['hosted'].warmUp.endpoint 'https://hosted-example.azurewebsites.net/health' `
        'The Hosted warm-up endpoint is incorrect.'
    Assert-Equal $sessions['hosted'].launchLabel 'Open application' `
        'The Hosted launch label is incorrect.'

    $hostedLinks = @{}
    foreach ($link in $sessions['hosted'].links) {
        $hostedLinks[[string]$link.kind] = $link
    }
    Assert-Equal $hostedLinks.Count 2 'The Hosted session does not expose exactly two auxiliary links.'
    Assert-Equal $hostedLinks['playground'].label 'Open agent playground' `
        'The playground link label is incorrect.'
    Assert-Equal $hostedLinks['playground'].url `
        'https://ai.azure.com/nextgen/r/workspace-key,rg-demo4,,hosted-account,hosted-project/build/agents/hosted-agent/build?version=4' `
        'The playground link does not carry the exact validated playground URL.'
    Assert-Equal $hostedLinks['vscode'].label 'Open hosted-agent code in VS Code' `
        'The VS Code link label is incorrect.'
    Assert-True ($hostedLinks['vscode'].url.StartsWith('vscode://file/', [StringComparison]::Ordinal)) `
        'The VS Code link does not use the vscode://file scheme.'
    Assert-True ($hostedLinks['vscode'].url.EndsWith(
            'PublicSectorAgentDemos.Demo4.HostedAgents', [StringComparison]::Ordinal)) `
        'The VS Code link does not point at the hosted-agent source directory.'
    Assert-True (-not $hostedLinks['vscode'].url.Contains('\', [StringComparison]::Ordinal)) `
        'The VS Code link still contains a backslash.'
    Assert-True (-not [regex]::IsMatch($hostedLinks['vscode'].url, '[A-Za-z]:\\Users\\')) `
        'The VS Code link leaks a workstation path.'

    $hostedDestinations = @(
        [string]$sessions['hosted'].launchUrl,
        [string]$hostedLinks['playground'].url,
        [string]$hostedLinks['vscode'].url
    )
    Assert-Equal @($hostedDestinations | Select-Object -Unique).Count 3 `
        'The Hosted session does not generate three distinct links.'

    $written = Get-Content -LiteralPath $destination -Raw
    Assert-True ($written.Contains('act-external.azurewebsites.net', [StringComparison]::Ordinal)) `
        'The generated catalog was not written to disk.'
    Assert-True (-not $sessions['foundation'].launchUrl.Contains('rg-demo4', [StringComparison]::Ordinal)) `
        'The Foundation launch target leaks the Demo 4 resource group.'
    Assert-True (-not $sessions['ground'].launchUrl.Contains('rg-demo4', [StringComparison]::Ordinal)) `
        'The Ground launch target leaks the Demo 4 resource group.'
    $context.PatriotsConfigured = $false
    $withoutPatriots = New-DemoReadyCatalog -SourcePath $committedCatalogPath `
        -DestinationPath $destination -Context $context
    $disabled = $withoutPatriots.sessions | Where-Object id -eq 'patriots-coordinate'
    Assert-True $disabled.optional 'Patriots must remain optional.'
    Assert-True (-not $disabled.configured) 'Patriots was enabled implicitly.'
    Assert-Equal $disabled.launchUrl '' 'An absent Patriots integration retained a stale link.'
    Assert-Equal $disabled.warmUp.kind 'disabled' 'An absent Patriots integration retained its probe.'
    foreach ($status in @('not-configured', 'failed', 'ready')) {
        $context.TokensAndCreditsStatus = $status
        $withExtra = New-DemoReadyCatalog -SourcePath $committedCatalogPath `
            -DestinationPath $destination -Context $context
        Assert-Equal @($withExtra.sessions).Count 6 'An extra changed the main session count.'
        Assert-Equal @($withExtra.extras).Count 1 'The extra was merged into the main set.'
        $extra = $withExtra.extras[0]
        Assert-Equal $extra.startupStatus $status 'The optional status was lost.'
        Assert-Equal $extra.configured ($status -ceq 'ready') 'The optional app was claimed ready.'
        Assert-Equal $extra.warmUp.kind ($status -ceq 'ready' ? 'tokens-local' : 'disabled') 'The optional probe is wrong.'
        Assert-Equal $extra.warmUp.endpoint ($status -ceq 'ready' ? 'http://localhost:5041/api/embeddings/manifest' : '') `
            'Tokens warm-up used a cloud or guessed health route.'
    }
}

Test-Case 'The generated catalog disables every unselected session and optional extra' {
    $workspace = New-TestDirectory -Name 'presenter-disabled-selection'
    $context = New-DemoReadyPresenterContext `
        -ActWebUrl 'https://act-selected.example.invalid/' `
        -AssuranceHttpUrl 'http://localhost:5080/' `
        -AssuranceHttpsUrl 'https://localhost:7080/' `
        -PatriotsHttpUrl 'http://localhost:5081/' `
        -PatriotsHttpsUrl 'https://localhost:7081/' `
        -TokensAndCreditsStatus 'not-configured' `
        -Selection ([ordered]@{
            Demo1 = $false
            Demo2 = $true
            Demo3 = $false
            Demo4 = $false
            Patriots = $false
            TokensAndCredits = $false
        }) `
        -RepositoryRoot $root
    $catalog = New-DemoReadyCatalog `
        -SourcePath $committedCatalogPath `
        -DestinationPath (Join-Path $workspace 'sessions.generated.json') `
        -Context $context
    foreach ($session in $catalog.sessions) {
        $selected = [string]$session.id -ceq 'act'
        Assert-Equal $session.configured $selected "The session '$($session.id)' has the wrong selected state."
        if (-not $selected) {
            Assert-Equal $session.launchUrl '' "The unselected session '$($session.id)' retained a launch URL."
            Assert-Equal $session.warmUp.kind 'disabled' "The unselected session '$($session.id)' retained a warm-up."
            $modelProperty = $session.warmUp.PSObject.Properties['modelDeployment']
            Assert-True ($null -eq $modelProperty -or
                [string]::IsNullOrEmpty([string]$modelProperty.Value)) `
                    "The unselected session '$($session.id)' retained a model deployment."
        }
    }
    Assert-Equal $catalog.extras[0].configured $false 'The unselected optional extra was enabled.'
    Assert-Equal $catalog.extras[0].warmUp.kind 'disabled' 'The unselected optional extra retained a warm-up.'
}

Test-Case 'Partial selection generates a Presenter build that accepts disabled sessions' {
    $workspace = New-TestDirectory -Name 'presenter-guided-project'
    $partial = [ordered]@{
        Demo1 = $false
        Demo2 = $true
        Demo3 = $false
        Demo4 = $false
        Patriots = $false
        TokensAndCredits = $false
    }
    $project = New-DemoReadyPresenterProject `
        -RepositoryRoot $root `
        -RuntimeRoot $workspace `
        -Selection $partial
    Assert-True (Test-Path -LiteralPath $project -PathType Leaf) `
        'The guided Presenter project was not generated.'
    Assert-True ($project.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase)) `
        'The guided Presenter project was written outside the runtime directory.'
    Assert-True (Test-Path `
        -LiteralPath (Join-Path (Split-Path -Parent $project) 'wwwroot\presenter.css') `
        -PathType Leaf) 'The guided Presenter did not copy its static assets.'
    $generatedCatalogSource = Get-Content `
        -LiteralPath (Join-Path (Split-Path -Parent $project) 'PresenterSessionCatalog.cs') `
        -Raw
    Assert-True ($generatedCatalogSource.Contains(
        'if (Kind == "disabled" &&',
        [StringComparison]::Ordinal)) 'The guided Presenter does not accept disabled warm-up targets.'
    Assert-True (-not $generatedCatalogSource.Contains(
        'Id is not ("patriots-coordinate" or "tokens-and-credits")',
        [StringComparison]::Ordinal)) 'The guided Presenter still limits disabled state to external sessions.'
    $generatedPageSource = Get-Content `
        -LiteralPath (Join-Path (Split-Path -Parent $project) 'PresenterPage.cs') `
        -Raw
    Assert-True ($generatedPageSource.Contains(
        'Not selected for this run.',
        [StringComparison]::Ordinal)) 'The guided Presenter does not explain an unselected session.'

    $full = [ordered]@{}
    foreach ($name in $partial.Keys) {
        $full[$name] = $partial[$name]
    }
    foreach ($name in @('Demo1', 'Demo2', 'Demo3', 'Demo4')) {
        $full[$name] = $true
    }
    Assert-Equal `
        (New-DemoReadyPresenterProject `
            -RepositoryRoot $root `
            -RuntimeRoot $workspace `
            -Selection $full) `
        (Join-Path $root 'src\PublicSectorAgentDemos.Presenter\PublicSectorAgentDemos.Presenter.csproj') `
        'The full deployment did not keep the committed Presenter project.'
}

Test-Case 'Selected Patriots builds and starts through the safe process host on both endpoints' {
    $workspace = New-TestDirectory -Name 'patriots-startup'
    $repository = [pscustomobject]@{
        Identity = 'patriots'
        DisplayName = 'Patriots council'
        Path = $workspace
    }
    $integration = [pscustomobject]@{
        Status = 'discovered'
        Repository = $repository
        GitBaseline = 'baseline'
        GitStatusPreserved = $null
    }
    $processes = [Collections.Generic.List[object]]::new()
    $script:patriotsCalls = [Collections.Generic.List[string]]::new()
    Invoke-WithMockedFunction `
        -Functions @{
            'Assert-DemoReadyPortsFree' = {
                param($Ports)
                $script:patriotsCalls.Add("ports:$($Ports -join ',')")
            }
            'Invoke-DemoReadyProjectBuild' = {
                param($RepositoryPath, $ProjectPath, $LogRoot, $LogName, $SensitiveValues)
                $script:patriotsCalls.Add("build:$LogName")
                Assert-Equal $RepositoryPath $workspace 'Patriots built outside its repository.'
                Assert-True ($ProjectPath.EndsWith(
                    'src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj',
                    [StringComparison]::Ordinal)) 'Patriots built the wrong project.'
            }
            'Assert-DemoReadyGitStatusPreserved' = {
                param($RepositoryPath, $Baseline, $DisplayName)
                $script:patriotsCalls.Add('git')
                Assert-Equal $Baseline 'baseline' 'Patriots lost its Git baseline.'
            }
            'Start-DemoReadyProcess' = {
                param(
                    $Name, $FilePath, $Arguments, $WorkingDirectory, $Environment,
                    $LogDirectory, $CommandMarker, $Endpoints, $ScriptRoot,
                    $OptionalExternalOwner, $SensitiveValues
                )
                $script:patriotsCalls.Add('start')
                Assert-Equal $Name 'external-patriots' 'Patriots used an unsafe protected process name.'
                Assert-Equal $FilePath 'dotnet' 'Patriots did not use the .NET CLI.'
                Assert-Equal $Environment.ASPNETCORE_URLS `
                    'https://localhost:7081;http://localhost:5081' `
                    'Patriots used the wrong local endpoints.'
                Assert-True ($Arguments -contains '--no-launch-profile') 'Patriots enabled a launch profile.'
                Assert-Equal ($Endpoints -join ',') `
                    'http://localhost:5081/,https://localhost:7081/' `
                    'Patriots did not record both endpoints.'
                return [pscustomobject]@{
                    name = $Name
                    optionalExternalOwner = ''
                    process = [pscustomobject]@{ HasExited = $false }
                }
            }
            'Wait-DemoReadyEndpoint' = {
                param($Uri, $TimeoutSeconds, [switch]$RequireSuccess, [switch]$SkipCertificateCheck)
                $script:patriotsCalls.Add("wait:$Uri")
                if ($Uri -ceq 'https://localhost:7081/') {
                    Assert-True $SkipCertificateCheck 'The local HTTPS development certificate was not handled.'
                }
            }
        } `
        -Action {
            Start-DemoReadyPatriots `
                -Integration $integration `
                -RepositoryRoot $root `
                -LogRoot $workspace `
                -ScriptRoot $scriptRoot `
                -Processes $processes
        }
    Assert-Equal $integration.Status 'ready' 'Patriots did not reach ready state.'
    Assert-True $integration.GitStatusPreserved 'Patriots Git preservation was not recorded.'
    Assert-Equal $processes.Count 1 'Patriots did not record one managed process.'
    Assert-Equal $processes[0].optionalExternalOwner $root 'Patriots did not record managed external ownership.'
    Assert-Equal ($script:patriotsCalls -join '|') `
        'ports:5081,7081|build:patriots|git|start|wait:http://localhost:5081/|wait:https://localhost:7081/|git' `
        'The Patriots startup order is incorrect.'
}

Test-Case 'Startup validates authentication and the plan before it stops local processes' {
    $authentication = $invokeSource.IndexOf('Initialize-DemoReadyAzureContext', [StringComparison]::Ordinal)
    $environmentCheck = $invokeSource.IndexOf('Assert-DemoReadyAzdEnvironmentAccess', [StringComparison]::Ordinal)
    $plan = $invokeSource.IndexOf('Show-DemoReadyDeploymentPlan', [StringComparison]::Ordinal)
    $stop = $invokeSource.LastIndexOf(
        "& (Join-Path `$PSScriptRoot 'Stop-DemoReady.ps1')",
        [StringComparison]::Ordinal)
    Assert-True ($authentication -ge 0 -and $authentication -lt $stop) `
        'Azure authentication does not occur before local process stop.'
    Assert-True ($environmentCheck -gt $authentication -and $environmentCheck -lt $stop) `
        'azd environment validation does not occur before local process stop.'
    Assert-True ($plan -gt $environmentCheck -and $plan -lt $stop) `
        'The final deployment plan does not occur before local process stop.'
    Assert-True ($invokeSource.Contains(
        "Read-DemoReadyYesNo -Prompt 'Continue with this deployment plan?'",
        [StringComparison]::Ordinal)) 'The guided plan does not require final confirmation.'
    Assert-True (($invokeSource.IndexOf(
        "Read-DemoReadyYesNo -Prompt 'Continue with this deployment plan?'",
        [StringComparison]::Ordinal) -lt $stop)) `
        'The plan confirmation does not occur before local process stop.'
}

Test-Case 'The generated catalog rejects a hosted playground URL that is not on ai.azure.com' {
    $workspace = New-TestDirectory -Name 'presenter-catalog-invalid-playground'
    $assuranceDossier = New-TestFile `
        -Path (Join-Path $workspace 'cross-gov\data\policies\dossier-shared-ai-service.md') `
        -Content '# synthetic assurance dossier'
    $patriotsDossier = New-TestFile `
        -Path (Join-Path $workspace 'patriots\data\policies\01-puppet-president.md') `
        -Content '# synthetic patriots dossier'
    $context = New-DemoReadyPresenterContext `
        -Demo1Values @{
            AZURE_AI_SERVICES_ENDPOINT = 'https://demo.services.ai.azure.com/'
            AZURE_AI_PROJECT_ID = '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/rg-demo1/providers/Microsoft.CognitiveServices/accounts/demo-account/projects/demo-project'
            COUNCIL_FAST_MODEL = 'gpt-test'
        } `
        -Demo4Values @{ DEMO4_APPLICATION_NAME = 'hosted-example' } `
        -HostedAgent ([pscustomobject]@{
            playground_url = 'https://not-ai-azure.example/nextgen/r/workspace-key,rg-demo4,,hosted-account,hosted-project/build/agents/hosted-agent/build'
        }) `
        -ActWebUrl 'https://act-external.azurewebsites.net/' `
        -AssuranceDossierPath $assuranceDossier `
        -AssuranceHttpUrl 'http://localhost:5080/' `
        -AssuranceHttpsUrl 'https://localhost:7080/' `
        -PatriotsHttpUrl 'http://localhost:5081/' `
        -PatriotsHttpsUrl 'https://localhost:7081/' `
        -PatriotsDossierPath $patriotsDossier `
        -RepositoryRoot $root

    Assert-Throws `
        -Action {
            New-DemoReadyCatalog `
                -SourcePath $committedCatalogPath `
                -DestinationPath (Join-Path $workspace 'sessions.generated.json') `
                -Context $context
        } `
        -ExpectedFragment 'hosted agent playground URL is invalid' `
        -Message 'A non-ai.azure.com playground URL was accepted.'
}

Test-Case 'Warm-up readiness accepts sign-in redirects' {
    Assert-True (Test-DemoReadyEndpointStatusCode 200) 'HTTP 200 was rejected.'
    Assert-True (Test-DemoReadyEndpointStatusCode 302) 'HTTP 302 was rejected.'
    Assert-True (Test-DemoReadyEndpointStatusCode 307) 'HTTP 307 was rejected.'
    Assert-True (-not (Test-DemoReadyEndpointStatusCode 404)) 'HTTP 404 was accepted.'
    Assert-True (-not (Test-DemoReadyEndpointStatusCode 500)) 'HTTP 500 was accepted.'
    Assert-True (-not (Test-DemoReadyEndpointStatusCode $null)) 'A missing status was accepted.'

    $warmUpSource = Get-Content `
        -LiteralPath (Join-Path $root 'src\PublicSectorAgentDemos.Presenter\PresenterWarmUpService.cs') `
        -Raw
    Assert-True ($warmUpSource.Contains('code is >= 200 and < 400', [StringComparison]::Ordinal)) `
        'The Presenter warm-up rejects the external Act sign-in redirect.'
}

Test-Case 'Presenter reads the generated catalog and keeps its committed fallback' {
    $program = Get-Content `
        -LiteralPath (Join-Path $root 'src\PublicSectorAgentDemos.Presenter\Program.cs') `
        -Raw
    Assert-True ($program.Contains('PRESENTER_SESSION_CATALOG_PATH', [StringComparison]::Ordinal)) `
        'Presenter does not read the generated catalog override.'
    Assert-True ($program.Contains('sessions.v1.json', [StringComparison]::Ordinal)) `
        'Presenter no longer has the committed catalog fallback.'
    Assert-True ($invokeSource.Contains(
            'PRESENTER_SESSION_CATALOG_PATH = $catalogPath',
            [StringComparison]::Ordinal)) `
        'The startup script does not pass the generated catalog to Presenter.'
}

Test-Case 'The committed Act fallback names no internal Azure host' {
    # The Act endpoint always comes from the external DEFRA azd environment at generation time.
    # A deleted internal App Service hostname must never stay in the committed fallback, because
    # an azurewebsites.net name is re-registrable once its app is removed.
    $catalog = Get-Content -LiteralPath $committedCatalogPath -Raw | ConvertFrom-Json
    $act = @($catalog.sessions | Where-Object { [string]$_.id -ceq 'act' })
    Assert-Equal $act.Count 1 'The committed catalog does not contain exactly one Act session.'
    foreach ($value in @([string]$act[0].launchUrl, [string]$act[0].warmUp.endpoint)) {
        Assert-True ($value.EndsWith('.invalid/', [StringComparison]::Ordinal) -or
            $value.EndsWith('.invalid/health', [StringComparison]::Ordinal)) `
            "The committed Act fallback '$value' is not a reserved unresolvable host."
    }
}

Test-Case 'The committed Hosted session uses the four current cross-government cases' {
    $catalogText = Get-Content -LiteralPath $committedCatalogPath -Raw
    Assert-True (-not $catalogText.Contains('CP-4202', [StringComparison]::Ordinal)) `
        'The committed catalog still contains the stale CP-4202 case reference.'
    Assert-True (-not [regex]::IsMatch($catalogText, '(?i)synthetic')) `
        'The committed catalog still contains the word synthetic.'

    $catalog = $catalogText | ConvertFrom-Json
    $hosted = @($catalog.sessions | Where-Object { [string]$_.id -ceq 'hosted' })
    Assert-Equal $hosted.Count 1 'The committed catalog does not contain exactly one Hosted session.'
    foreach ($caseId in @('CG-8101', 'CG-8202', 'CG-8303', 'CG-8404')) {
        Assert-True (([string]$hosted[0].input).Contains($caseId, [StringComparison]::Ordinal)) `
            "The committed Hosted input is missing case '$caseId'."
        Assert-True (([string]$hosted[0].savedResultFallback).Contains($caseId, [StringComparison]::Ordinal)) `
            "The committed Hosted saved-result fallback is missing case '$caseId'."
    }
    Assert-Equal $hosted[0].launchLabel 'Open application' 'The committed Hosted launch label is incorrect.'
    Assert-Equal @($hosted[0].links).Count 2 'The committed Hosted session must define exactly two links.'
}

Test-Case 'Committed configuration and scripts contain no workstation path' {
    $workstationPattern = '[A-Za-z]:\\Users\\'
    $inspected = @(
        $committedCatalogPath,
        $invokePath,
        $validatePath,
        $stopPath,
        $removeAzurePath,
        $externalModulePath,
        $presenterModulePath,
        (Join-Path $scriptRoot 'DemoReady\Common.ps1'),
        (Join-Path $scriptRoot 'DemoReady\Owned.ps1'),
        (Join-Path $scriptRoot 'DemoReady\Entra.ps1'),
        $setupAgentPath
    )
    foreach ($path in $inspected) {
        $content = Get-Content -LiteralPath $path -Raw
        Assert-True (-not [regex]::IsMatch($content, $workstationPattern)) `
            "The file '$path' contains a workstation-specific absolute path."
    }
    $catalog = Get-Content -LiteralPath $committedCatalogPath -Raw | ConvertFrom-Json
    foreach ($session in $catalog.sessions) {
        Assert-True (-not [regex]::IsMatch([string]$session.input, '^[A-Za-z]:\\')) `
            "The committed session '$($session.id)' pins an absolute local path."
    }
}

# ---------------------------------------------------------------------- teardown

Test-Case 'Teardown reverses selected startup work and can retain optional checkouts' {
    Assert-Equal ([regex]::Matches($removeAzureSource, '& azd down')).Count 1 `
        'The teardown must use one shared azd down path.'
    foreach ($required in @(
        '--purge',
        '--force',
        "'cognitiveservices', 'account', 'purge'",
        "'keyvault', 'purge'",
        "'azd-env-name'",
        "'group', 'exists'",
        'soft-deleted resources',
        "Join-Path `$repositoryRoot 'deploy\demo1'",
        "Join-Path `$repositoryRoot 'src\PublicSectorAgentDemos.Demo2.Act'",
        "Join-Path `$repositoryRoot 'src\PublicSectorAgentDemos.Demo3.Coordinate'",
        "Join-Path `$repositoryRoot 'deploy\demo4'",
        'Demo1EnvironmentName',
        'Demo2EnvironmentName',
        'Demo3EnvironmentName',
        'Demo4EnvironmentName',
        'KeepPatriotsAndTokensAndCredits',
        'No matching readiness selection exists',
        'clonedByThisRun',
        'remainingRequested',
        'RequireManagedIdentity',
        'Assert-DemoReadyReportPath',
        'ReparsePoint',
        'external-patriots',
        'tokens-and-credits',
        'az ad app delete',
        'status --porcelain',
        "rev-list --left-right --count '@{upstream}...HEAD'",
        "Join-Path `$runtimeRoot 'sessions.generated.json'",
        "Join-Path `$runtimeRoot 'processes.json'",
        'Retained unrelated or protected process state'
    )) {
        Assert-True ($removeAzureSource.Contains($required, [StringComparison]::Ordinal)) `
            "The teardown script is missing '$required'."
    }
    foreach ($key in @('Demo1', 'Demo2', 'Demo3', 'Demo4')) {
        Assert-True ($removeAzureSource.Contains("Key = '$key'", [StringComparison]::Ordinal)) `
            "The teardown script does not manage $key."
    }
    Assert-True ($invokeSource.Contains('clonedByThisRun', [StringComparison]::Ordinal)) `
        'Startup does not record whether it cloned each optional checkout.'
    Assert-True (-not $invokeSource.Contains('optionalClonePlanned', [StringComparison]::Ordinal)) `
        'Startup infers clone ownership from plan intent instead of completed clone provenance.'
    Assert-True ($externalSource.Contains('CreatedByThisRun', [StringComparison]::Ordinal)) `
        'Repository resolution does not report a completed optional clone.'
    foreach ($forbidden in @(
        'group delete'
    )) {
        Assert-True (-not $removeAzureSource.Contains($forbidden, [StringComparison]::OrdinalIgnoreCase)) `
            "The teardown script contains the forbidden direct deletion '$forbidden'."
    }
}

Test-Case 'Startup and stop scripts contain no destructive Azure call' {
    # Every file the startup script dot-sources must be inspected. Common.ps1, Owned.ps1, and
    # Entra.ps1 hold the Azure and Microsoft Entra mutations, so they must not be skipped here.
    $combined = (
        $invokeSource +
        $validateSource +
        $externalSource +
        $tokensSource +
        $presenterSource +
        $commonModuleSource +
        $ownedModuleSource +
        $entraModuleSource +
        $stopSource).ToLowerInvariant()
    foreach ($forbidden in @(
        'azd down',
        'group delete',
        'resource delete',
        'ad app delete',
        'deployment delete'
    )) {
        Assert-True (-not $combined.Contains($forbidden, [StringComparison]::Ordinal)) `
            "The scripts contain the forbidden call '$forbidden'."
    }
}

# --------------------------------------------------------- demo 4 authentication

Test-Case 'The Entra application requests access-token version 2' {
    $missing = [pscustomobject]@{ id = [Guid]::NewGuid().ToString() }
    Assert-Equal (Get-DemoReadyRequestedAccessTokenVersion -Application $missing) 0 `
        'An application without an api block did not report version 0.'

    $version1 = [pscustomobject]@{
        id = [Guid]::NewGuid().ToString()
        api = [pscustomobject]@{ requestedAccessTokenVersion = 1 }
    }
    Assert-Equal (Get-DemoReadyRequestedAccessTokenVersion -Application $version1) 1 `
        'A version 1 application was not reported.'

    $script:entraPatchBodies = [Collections.Generic.List[string]]::new()
    Invoke-WithMockedFunction `
        -Functions @{
            'Invoke-DemoReadyAzRestJson' = {
                [CmdletBinding()]
                param(
                    [Parameter(Mandatory)][string]$Method,
                    [Parameter(Mandatory)][string]$Uri,
                    [Parameter(Mandatory)][string]$JsonBody,
                    [Parameter(Mandatory)][string]$WorkingDirectory,
                    [Parameter(Mandatory)][string]$RuntimeRoot,
                    [Parameter(Mandatory)][string[]]$SensitiveValues,
                    [switch]$CaptureOutput,
                    [switch]$PreserveCapturedOutput,
                    [switch]$Quiet
                )
                $script:entraPatchBodies.Add("$Method $Uri $JsonBody")
            }
        } `
        -Action {
            Set-DemoReadyRequestedAccessTokenVersion `
                -Application $version1 `
                -RepositoryRoot $root `
                -RuntimeRoot $testRoot `
                -SensitiveValues @('secret')
            Set-DemoReadyRequestedAccessTokenVersion `
                -Application $version1 `
                -RepositoryRoot $root `
                -RuntimeRoot $testRoot `
                -SensitiveValues @('secret')
        }

    Assert-Equal $script:entraPatchBodies.Count 1 `
        'The access-token version was not patched exactly once.'
    Assert-True ($script:entraPatchBodies[0].StartsWith('patch ', [StringComparison]::Ordinal)) `
        'The access-token version was not patched.'
    Assert-True ($script:entraPatchBodies[0].Contains(
            'https://graph.microsoft.com/v1.0/applications/',
            [StringComparison]::Ordinal)) `
        'The access-token version patch did not target Microsoft Graph.'
    Assert-True ($script:entraPatchBodies[0].Contains(
            '"requestedAccessTokenVersion":2',
            [StringComparison]::Ordinal)) `
        'The patch did not request access-token version 2.'
    Assert-Equal (Get-DemoReadyRequestedAccessTokenVersion -Application $version1) 2 `
        'The application does not report access-token version 2 after the patch.'
}

Test-Case 'The Entra reply URL is the exact App Service callback' {
    $expected = 'https://app-demo4-abc123.azurewebsites.net/.auth/login/aad/callback'
    Assert-Equal (Get-DemoReadyAppServiceCallbackUri -ApplicationName 'app-demo4-abc123') `
        $expected 'The generated App Service callback URI is incorrect.'
    Assert-True (Test-DemoReadyAppServiceCallbackUri `
            -RedirectUri $expected `
            -ApplicationName 'app-demo4-abc123') `
        'The exact App Service callback was rejected.'
    Assert-True (-not (Test-DemoReadyAppServiceCallbackUri `
            -RedirectUri $expected `
            -ApplicationName 'app-demo4-other')) `
        'A callback for another App Service was accepted.'

    foreach ($rejected in @(
        'http://app-demo4-abc123.azurewebsites.net/.auth/login/aad/callback',
        'https://app-demo4-abc123.azurewebsites.net/.auth/login/aad/callback/',
        'https://app-demo4-abc123.azurewebsites.net/',
        'https://attacker.example.com/.auth/login/aad/callback',
        'https://app-demo4-abc123.azurewebsites.net.attacker.example.com/.auth/login/aad/callback',
        ''
    )) {
        Assert-True (-not (Test-DemoReadyAppServiceCallbackUri -RedirectUri $rejected)) `
            "The unsafe reply URL '$rejected' was accepted."
    }

    foreach ($invalidName in @('', 'App-Demo4', 'a', 'app_demo4')) {
        Assert-Throws `
            -Action { Get-DemoReadyAppServiceCallbackUri -ApplicationName $invalidName } `
            -ExpectedFragment 'The Demo 4 App Service name is invalid.' `
            -Message "The invalid App Service name '$invalidName' was accepted."
    }
}

Test-Case 'Merging reply URLs keeps unique HTTPS entries and rejects insecure ones' {
    $callback = 'https://app-demo4-abc123.azurewebsites.net/.auth/login/aad/callback'
    $merged = Merge-DemoReadyRedirectUri `
        -ExistingUris @(
            'https://app-demo4-old.azurewebsites.net/.auth/login/aad/callback',
            'http://localhost:5000/signin-oidc',
            '',
            $callback
        ) `
        -RedirectUri $callback
    Assert-Equal @($merged).Count 2 'The merged reply URL set is incorrect.'
    Assert-True ($merged -contains $callback) 'The current callback is missing.'
    Assert-True (-not (@($merged) | Where-Object {
            -not $_.StartsWith('https://', [StringComparison]::Ordinal)
        })) 'The merged reply URL set kept an insecure entry.'

    Assert-Throws `
        -Action {
            Merge-DemoReadyRedirectUri `
                -ExistingUris @() `
                -RedirectUri 'https://app-demo4-abc123.azurewebsites.net/signin-oidc'
        } `
        -ExpectedFragment 'not the exact App Service EasyAuth callback' `
        -Message 'A reply URL outside the EasyAuth callback was accepted.'
}

Test-Case 'Adding the reply URL enables ID-token issuance for the browser flow' {
    $script:mockNativeCalls.Clear()
    $application = [pscustomobject]@{
        id = '33333333-3333-3333-3333-333333333333'
        appId = '44444444-4444-4444-4444-444444444444'
        web = [pscustomobject]@{ redirectUris = @() }
    }
    Invoke-WithMockedFunction `
        -Functions @{ 'Invoke-DemoReadyNative' = $MockInvokeDemoReadyNative } `
        -Action {
            Add-DemoReadyEntraRedirectUri `
                -Application $application `
                -ApplicationName 'app-demo4-abc123' `
                -RepositoryRoot $root `
                -SensitiveValues @('secret')
        }

    Assert-Equal $script:mockNativeCalls.Count 1 'The Entra update did not run exactly once.'
    $call = $script:mockNativeCalls[0]
    Assert-Equal $call.FilePath 'az' 'The Entra update did not use the Azure CLI.'
    $arguments = @($call.Arguments)
    Assert-True ($arguments -contains '--enable-id-token-issuance') `
        'The Entra update did not enable ID-token issuance.'
    Assert-Equal $arguments[[Array]::IndexOf($arguments, '--enable-id-token-issuance') + 1] 'true' `
        'ID-token issuance was not set to true.'
    Assert-True ($arguments -contains '--web-redirect-uris') `
        'The Entra update did not set the reply URLs.'
    Assert-True ($arguments -contains 'https://app-demo4-abc123.azurewebsites.net/.auth/login/aad/callback') `
        'The Entra update did not set the exact App Service callback.'
    Assert-True ($arguments -contains $application.id) `
        'The Entra update did not target the owned application object.'
    Assert-True ([string]::IsNullOrEmpty($call.LogPath)) `
        'The Entra update wrote a log file that could hold identity material.'

    Assert-Throws `
        -Action {
            Add-DemoReadyEntraRedirectUri `
                -Application $application `
                -ApplicationName 'Invalid Name' `
                -RepositoryRoot $root `
                -SensitiveValues @('secret')
        } `
        -ExpectedFragment 'The Demo 4 App Service name is invalid.' `
        -Message 'An invalid App Service name produced a reply URL.'
    $script:mockNativeCalls.Clear()
}

Test-Case 'Entra ownership and collision checks protect an unowned application' {
    $appId = '55555555-5555-5555-5555-555555555555'
    $objectId = '66666666-6666-6666-6666-666666666666'
    $marker = 'public-sector-agent-demos:demo-ready:v1:psad-demo:demo4-api'
    $owned = [pscustomobject]@{
        appId = $appId
        id = $objectId
        displayName = 'psad-psad-demo-demo4-api'
        notes = $marker
        identifierUris = @("api://$appId")
    }
    Assert-True (Test-DemoReadyEntraApplicationOwnership `
            -Application $owned `
            -DisplayName 'psad-psad-demo-demo4-api' `
            -OwnershipMarker $marker) `
        'The owned Entra application failed its ownership check.'

    $wrongMarker = $owned.PSObject.Copy()
    $wrongMarker.notes = 'someone-else'
    Assert-True (-not (Test-DemoReadyEntraApplicationOwnership `
            -Application $wrongMarker `
            -DisplayName 'psad-psad-demo-demo4-api' `
            -OwnershipMarker $marker)) `
        'An application with a foreign ownership marker was accepted.'

    $wrongIdentifier = $owned.PSObject.Copy()
    $wrongIdentifier.identifierUris = @('api://other')
    Assert-True (-not (Test-DemoReadyEntraApplicationOwnership `
            -Application $wrongIdentifier `
            -DisplayName 'psad-psad-demo-demo4-api' `
            -OwnershipMarker $marker)) `
        'An application without its own identifier URI was accepted.'

    Assert-Throws `
        -Action {
            Assert-DemoReadyEntraCollision `
                -Applications @($owned) `
                -ObjectId '' `
                -DisplayName 'psad-psad-demo-demo4-api'
        } `
        -ExpectedFragment 'An unowned Microsoft Entra application already uses' `
        -Message 'A name collision without persisted state was accepted.'
    Assert-Throws `
        -Action {
            Assert-DemoReadyEntraCollision `
                -Applications @($owned) `
                -ObjectId '77777777-7777-7777-7777-777777777777' `
                -DisplayName 'psad-psad-demo-demo4-api'
        } `
        -ExpectedFragment 'An unowned Microsoft Entra application already uses' `
        -Message 'A collision with another object ID was accepted.'
    Assert-DemoReadyEntraCollision `
        -Applications @($owned) `
        -ObjectId $objectId `
        -DisplayName 'psad-psad-demo-demo4-api'
}

Test-Case 'The persisted Entra object ID stays consistent and well formed' {
    $objectId = '88888888-8888-8888-8888-888888888888'
    Assert-Equal (Get-DemoReadyPersistedObjectId $objectId $objectId) $objectId `
        'A matching persisted object ID was rejected.'
    Assert-Equal (Get-DemoReadyPersistedObjectId '' $objectId) $objectId `
        'The azd object ID was not used when state was empty.'
    Assert-Equal (Get-DemoReadyPersistedObjectId '' '') '' `
        'An empty persisted object ID was not returned as empty.'
    Assert-Throws `
        -Action {
            Get-DemoReadyPersistedObjectId $objectId '99999999-9999-9999-9999-999999999999'
        } `
        -ExpectedFragment 'do not match' `
        -Message 'Conflicting persisted object IDs were accepted.'
    Assert-Throws `
        -Action { Get-DemoReadyPersistedObjectId 'not-a-guid' '' } `
        -ExpectedFragment 'is invalid' `
        -Message 'A malformed persisted object ID was accepted.'

    $applicationId = '9778f3b6-21cd-49d2-833f-28f2aee246ce'
    Assert-Equal (Get-DemoReadyPersistedApplicationId '' $applicationId) $applicationId `
        'The azd client ID was not used when local state was empty.'
    Assert-Equal (Get-DemoReadyPersistedApplicationId $applicationId $applicationId) $applicationId `
        'Matching persisted client IDs were rejected.'
    Assert-Throws `
        -Action {
            Get-DemoReadyPersistedApplicationId `
                $applicationId `
                '55555555-5555-5555-5555-555555555555'
        } `
        -ExpectedFragment 'client IDs do not match' `
        -Message 'Conflicting persisted client IDs were accepted.'
}

Test-Case 'A deleted persisted Entra application is recreated safely' {
    $appId = '9778f3b6-21cd-49d2-833f-28f2aee246ce'
    Invoke-WithMockedFunction `
        -Functions @{
            'Invoke-DemoReadyNative' = {
                param(
                    $FilePath, $Arguments, $WorkingDirectory, $LogPath, $Environment,
                    $SensitiveValues, [switch]$CaptureOutput,
                    [switch]$PreserveCapturedOutput, [switch]$Quiet
                )
                Assert-Equal ($Arguments[0..3] -join '|') 'ad|app|list|--filter' `
                    'The stale application check did not use a safe list query.'
                Assert-Equal $Arguments[4] "appId eq '$appId'" `
                    'The stale application check queried the wrong client ID.'
                return '[]'
            }
        } `
        -Action {
            $application = Get-DemoReadyEntraApplicationByApplicationId `
                -ApplicationId $appId `
                -RepositoryRoot $root `
                -SensitiveValues @('masked-value')
            Assert-True ($null -eq $application) `
                'A deleted persisted application was reported as present.'
        }

    foreach ($required in @(
        "`$applications.Remove('demo4Api')",
        "([string]`$existing['DEMO4_API_CLIENT_ID'])",
        "'DEMO_READY_DEMO4_API_APP_OBJECT_ID' ''",
        'Write-DemoReadyJsonAtomic -Path $StatePath -Value $state'
    )) {
        Assert-True ($entraModuleSource.Contains($required, [StringComparison]::Ordinal)) `
            "The stale Entra recovery path is missing '$required'."
    }
    Assert-True ($entraModuleSource.LastIndexOf(
            'Write-DemoReadyJsonAtomic -Path $StatePath -Value $state',
            [StringComparison]::Ordinal) -lt
        $entraModuleSource.LastIndexOf('Ensure-DemoReadyEntraServicePrincipal', [StringComparison]::Ordinal)) `
        'The replacement application is not persisted before service-principal setup.'
}

Test-Case 'The Entra client credential lifetime is bounded and rotates before expiry' {
    $now = [DateTimeOffset]::new(2026, 6, 15, 12, 0, 0, [TimeSpan]::Zero)
    Assert-Equal (Get-DemoReadyCredentialEndDate -Now $now) '2026-07-13T12:00:00Z' `
        'The default credential lifetime is not 28 days.'
    Assert-Equal (Get-DemoReadyCredentialEndDate -Now $now -LifetimeDays 1) '2026-06-16T12:00:00Z' `
        'A one-day credential lifetime was not honoured.'
    Assert-Throws `
        -Action { Get-DemoReadyCredentialEndDate -Now $now -LifetimeDays 365 } `
        -ExpectedFragment 'LifetimeDays' `
        -Message 'An unbounded credential lifetime was accepted.'

    Assert-True (Test-DemoReadyCredentialExpiry `
            -ExpiresOn ([DateTimeOffset]::UtcNow.AddDays(10).ToString('O'))) `
        'A valid credential was treated as expired.'
    foreach ($expired in @(
        '',
        'not-a-date',
        [DateTimeOffset]::UtcNow.AddHours(6).ToString('O'),
        [DateTimeOffset]::UtcNow.AddDays(-1).ToString('O')
    )) {
        Assert-True (-not (Test-DemoReadyCredentialExpiry -ExpiresOn $expired)) `
            "The expiring credential '$expired' was reused."
    }
}

Test-Case 'The Entra module never exposes the client secret' {
    foreach ($forbidden in @('-LogPath', 'Write-Host', 'Write-Output', 'ConvertTo-SecureString')) {
        Assert-True (-not $entraModuleSource.Contains($forbidden, [StringComparison]::Ordinal)) `
            "The Entra module can expose identity material through '$forbidden'."
    }
    foreach ($required in @(
        '$SensitiveValues.Add($persistedSecret)',
        '$SensitiveValues.Add($credential)',
        "Set-DemoReadyAzdValue `$ContextPath `$AzdEnvironmentName ``"
    )) {
        Assert-True ($entraModuleSource.Contains($required, [StringComparison]::Ordinal)) `
            "The Entra module is missing the masking step '$required'."
    }
    Assert-True ($invokeSource.Contains(
            "-ApplicationName ([string]`$demo4Values.DEMO4_APPLICATION_NAME)",
            [StringComparison]::Ordinal)) `
        'The startup script does not derive the reply URL from the deployed App Service.'
    Assert-True (-not $invokeSource.Contains('DEMO4_WEB_CLIENT_SECRET', [StringComparison]::Ordinal)) `
        'The startup script handles the Demo 4 client secret directly.'
    $secretWrite = $entraModuleSource.LastIndexOf("'DEMO4_WEB_CLIENT_SECRET'", [StringComparison]::Ordinal)
    $expiryWrite = $entraModuleSource.LastIndexOf("'DEMO4_WEB_CLIENT_SECRET_EXPIRES_ON'", [StringComparison]::Ordinal)
    $clientWrite = $entraModuleSource.LastIndexOf("'DEMO4_API_CLIENT_ID'", [StringComparison]::Ordinal)
    Assert-True ($secretWrite -lt $expiryWrite -and $expiryWrite -lt $clientWrite) `
        'The Entra client ID is not persisted last for interruption-safe credential recovery.'
}

Test-Case 'Demo 4 EasyAuth protects every browser route except the health probe' {
    $resources = Get-Content -LiteralPath (Join-Path $root 'infra\demo4\resources.bicep') -Raw
    $main = Get-Content -LiteralPath (Join-Path $root 'infra\demo4\main.bicep') -Raw
    foreach ($required in @(
        "openIdIssuer: '`${environment().authentication.loginEndpoint}`${tenant().tenantId}/v2.0'",
        'requireAuthentication: true',
        "unauthenticatedClientAction: 'RedirectToLoginPage'",
        "redirectToProvider: 'azureactivedirectory'",
        'excludedPaths: applicationAnonymousPaths',
        'clientId: applicationApiClientId',
        "var applicationAudience = 'api://`${applicationApiClientId}'",
        'identities: allowedUserPrincipalIds',
        'requireHttps: true',
        'validateNonce: true',
        "name: 'authsettingsV2'",
        "clientSecretSettingName: 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET'"
    )) {
        Assert-True ($resources.Contains($required, [StringComparison]::Ordinal)) `
            "The Demo 4 EasyAuth contract is missing '$required'."
    }
    $anonymousStart = $resources.IndexOf('var applicationAnonymousPaths', [StringComparison]::Ordinal)
    $anonymousEnd = $resources.IndexOf('var applicationAuthSettings', [StringComparison]::Ordinal)
    Assert-True ($anonymousStart -ge 0 -and $anonymousEnd -gt $anonymousStart) `
        'The Demo 4 anonymous path list was not found.'
    $anonymous = $resources.Substring($anonymousStart, $anonymousEnd - $anonymousStart)
    Assert-Equal ([regex]::Matches($anonymous, "'/")).Count 1 `
        'Demo 4 EasyAuth excludes more than the health probe.'
    Assert-True ($anonymous.Contains("'/health'", [StringComparison]::Ordinal)) `
        'Demo 4 EasyAuth does not keep the health probe anonymous.'

    foreach ($guard in @(
        'applicationWebClientSecret is required for Demo 4 EasyAuth.',
        'applicationApiClientId is required for Demo 4 EasyAuth.',
        'deployerPrincipalId is required so Demo 4 sign-in stays restricted to configured users.'
    )) {
        Assert-True ($main.Contains($guard, [StringComparison]::Ordinal)) `
            "The Demo 4 deployment does not fail without '$guard'."
    }
    Assert-True (-not $main.Contains('allowedUserPrincipalIds: empty(', [StringComparison]::Ordinal)) `
        'Demo 4 can deploy with an unrestricted allowed-principal list.'
}

# ------------------------------------------------------------ removed internals

Test-Case 'Removed internal Act and Coordinate assets do not reappear' {
    foreach ($removed in @(
        'azure.yaml',
        'infra\main.bicep',
        'infra\main.parameters.json',
        'infra\resources.bicep',
        'infra\model-deployment.bicep',
        'infra\hooks',
        'infra\demo2-act.bicep',
        'infra\demo2-act.bicepparam',
        'infra\demo2',
        'deploy\demo2',
        'demos\cross-government',
        'demos\demo2',
        'src\PublicSectorAgentDemos.Demo3.Coordinate\PublicSectorAgentDemos.Demo3.Coordinate.csproj',
        'src\PublicSectorAgentDemos.Demo2.Act\PublicSectorAgentDemos.Demo2.Act.csproj',
        'src\Demo2.Web',
        'src\Defra.AgentCore',
        'src\Defra.Tools.Mcp',
        'data\coordinate',
        'data\demo2',
        'config\act',
        'config\agents',
        'config\policies',
        'config\domain',
        'evals\act',
        'docs\COORDINATE-PROVENANCE.md',
        'docs\runbooks\coordinate.md',
        'docs\runbooks\act.md',
        'docs\demo-scripts',
        'docs\presenter-walkthroughs',
        'tests\PublicSectorAgentDemos.CoordinateProtectionTests',
        'tests\PublicSectorAgentDemos.Coordinate.CloudIntegrationTests',
        'tests\PublicSectorAgentDemos.Demo2.Act.Tests',
        '.github\agents\scenario-architect.agent.md'
    )) {
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $root $removed))) `
            "The removed path '$removed' reappeared in the repository."
    }

    [xml]$solution = Get-Content -LiteralPath (Join-Path $root 'PublicSectorAgentDemos.slnx') -Raw
    $projectPaths = @($solution.SelectNodes('//Project') | ForEach-Object { [string]$_.Path }) -join "`n"
    foreach ($forbidden in @(
        'Demo2.Act',
        'Demo3.Coordinate',
        'Demo2.Web',
        'Defra.',
        'GovernanceCouncil',
        'CoordinateProtectionTests',
        'Coordinate.CloudIntegrationTests'
    )) {
        Assert-True (-not $projectPaths.Contains($forbidden, [StringComparison]::Ordinal)) `
            "The root solution includes an isolated or removed build project '$forbidden'."
    }
    foreach ($forbidden in @('runbooks/coordinate.md', 'runbooks/act.md')) {
        Assert-True (-not $solution.OuterXml.Contains($forbidden, [StringComparison]::Ordinal)) `
            "The solution still references the removed document '$forbidden'."
    }
}

Test-Case 'Retained Bicep modules resolve to existing files' {
    $infraRoot = Join-Path $root 'infra'
    $moduleCount = 0
    foreach ($template in Get-ChildItem -LiteralPath $infraRoot -Recurse -File -Filter '*.bicep') {
        foreach ($line in Get-Content -LiteralPath $template.FullName) {
            $match = [regex]::Match($line, "^\s*module\s+\w+\s+'([^']+)'")
            if (-not $match.Success) {
                continue
            }
            $reference = $match.Groups[1].Value
            if ($reference.StartsWith('br', [StringComparison]::Ordinal)) {
                continue
            }
            $moduleCount++
            $resolved = Join-Path $template.DirectoryName $reference
            Assert-True (Test-Path -LiteralPath $resolved -PathType Leaf) `
                "The template '$($template.Name)' references the missing module '$reference'."
        }
    }
    Assert-True ($moduleCount -gt 0) 'No retained Bicep module reference was inspected.'
}

Test-Case 'Startup no longer provisions, seeds, or measures Coordinate' {
    foreach ($forbidden in @(
        'Coordinate.CloudIntegrationTests',
        'Category=CoordinateSeed',
        'Category=CoordinateSeedVerification',
        'COORDINATE_',
        'Get-DemoReadyPatriotsTelemetryConfiguration',
        'Resolve-DemoReadyPatriotsTelemetryMetadata',
        'PublicSectorAgentDemos.Coordinate',
        'PATRIOTS_APPLICATIONINSIGHTS_RESOURCE_ID'
    )) {
        Assert-True (-not $invokeSource.Contains($forbidden, [StringComparison]::Ordinal)) `
            "The startup script still owns Coordinate work for '$forbidden'."
    }
    Assert-Equal ([regex]::Matches($invokeSource, 'deployedByThisRepository = \$false')).Count 2 `
        'The readiness report must distinguish both external applications from the owned demos.'
}

Test-Case 'Unused validation and telemetry helpers are removed' {
    $modules = $commonModuleSource + $ownedModuleSource + $externalSource + $presenterSource
    foreach ($removed in @(
        'function Assert-DemoReadyApplicationInsights',
        'function Assert-DemoReadyWebAppTelemetry',
        'function Wait-DemoReadyTelemetry',
        'function Get-DemoReadyResourceValue',
        'function Get-DemoReadyDemo4HostedAgentEndpoint'
    )) {
        Assert-True (-not ($modules + $invokeSource).Contains($removed, [StringComparison]::Ordinal)) `
            "The dormant helper '$removed' remains in the orchestration scripts."
    }
    Assert-True ($ownedModuleSource.Contains(
            'function Get-DemoReadyApplicationInsightsConnectionString',
            [StringComparison]::Ordinal)) `
        'The Presenter cannot read its Application Insights connection string.'
    Assert-True ($commonModuleSource.Contains(
            'function Invoke-DemoReadyProjectBuild',
            [StringComparison]::Ordinal)) `
        'The shared targeted project build helper is absent.'
    Assert-True (-not $ownedModuleSource.Contains('$Locations', [StringComparison]::Ordinal)) `
        'The Azure context still enumerates model quota by location.'
}

# -------------------------------------------------------------- demo setup agent

Test-Case 'The demo setup agent exists and writes only the ignored setup file' {
    Assert-True (Test-Path -LiteralPath $setupAgentPath -PathType Leaf) `
        'The guided Demo Setup agent is absent.'
    $agent = Get-Content -LiteralPath $setupAgentPath -Raw
    foreach ($required in @(
        'name: demo-setup',
        '.demo-ready\\repositories.local.json',
        'scripts\DemoReady\repositories.v1.schema.json',
        'DEFRA-AI-Demos',
        'cross-gov-assurance-board-demo',
        'azure-ai-mgs-patriots',
        'azd env list --output json',
        'git check-ignore'
    )) {
        Assert-True ($agent.Contains($required, [StringComparison]::Ordinal)) `
            "The Demo Setup agent does not describe '$required'."
    }
    foreach ($restriction in @(
        'Never run `git pull`, `git reset`, `git clean`, `git checkout`',
        'Never copy application source, configuration, or data between repositories.',
        'Never edit any file inside an external repository.',
        'Never run `azd provision`, `azd deploy`, `azd up`, `azd down`',
        'Never write anything other than `.demo-ready\repositories.local.json`'
    )) {
        Assert-True ($agent.Contains($restriction, [StringComparison]::Ordinal)) `
            "The Demo Setup agent is missing the restriction '$restriction'."
    }
    Assert-True (-not [regex]::IsMatch($agent, '(?m)^\s*azd (env new|provision|deploy|up|down)\b')) `
        'The Demo Setup agent instructs a state-changing azd command.'

    $ignoreRules = Get-Content -LiteralPath (Join-Path $root '.gitignore')
    Assert-True ($ignoreRules -contains '.demo-ready/') `
        'The setup file directory is not ignored by Git.'
}

# ------------------------------------------------------- masking and safe state

Test-Case 'Secret masking removes explicit and labelled values' {
    $secret = 'NeverWriteThisCredential-123'
    $connectionString =
        'InstrumentationKey=11111111-1111-1111-1111-111111111111;IngestionEndpoint=https://example.invalid/'
    $protected = Protect-DemoReadyText `
        -Text "client_secret=$secret Authorization=BearerValue access_token=TokenValue $connectionString" `
        -SensitiveValues @($secret, $connectionString)
    Assert-True (-not $protected.Contains($secret, [StringComparison]::Ordinal)) `
        'The explicit credential remains in protected text.'
    Assert-True (-not $protected.Contains('BearerValue', [StringComparison]::Ordinal)) `
        'The authorization value remains in protected text.'
    Assert-True (-not $protected.Contains('TokenValue', [StringComparison]::Ordinal)) `
        'The access token remains in protected text.'
    Assert-True (-not $protected.Contains('InstrumentationKey=1', [StringComparison]::Ordinal)) `
        'The Application Insights connection remains in protected text.'
}

Test-Case 'The process host masks the values the local applications receive' {
    $processHostSource = Get-Content `
        -LiteralPath (Join-Path $scriptRoot 'Start-DemoReadyProcessHost.ps1') -Raw
    foreach ($required in @(
        "'AZURE_TENANT_ID'",
        "'APPLICATIONINSIGHTS_CONNECTION_STRING'",
        "'APPINSIGHTS_CONNECTION_STRING'"
    )) {
        Assert-True ($processHostSource.Contains($required, [StringComparison]::Ordinal)) `
            "The process host does not mask $required."
    }
    Assert-True (-not $processHostSource.Contains('ACT_WEB_ENTRA_CLIENT_SECRET',
            [StringComparison]::Ordinal)) `
        'The process host still names a removed internal Act credential.'
}

Test-Case 'Native command logs mask sensitive output' {    $secret = 'NativeSecret-456'
    $workspace = New-TestDirectory -Name 'native-command'
    $logPath = Join-Path $workspace 'native-command.log'
    $output = Invoke-DemoReadyNative `
        -FilePath 'pwsh' `
        -Arguments @('-NoProfile', '-Command', "Write-Output 'client_secret=$secret'") `
        -WorkingDirectory $root `
        -LogPath $logPath `
        -SensitiveValues @($secret) `
        -CaptureOutput `
        -Quiet
    $log = Get-Content -LiteralPath $logPath -Raw
    Assert-True (-not $output.Contains($secret, [StringComparison]::Ordinal)) `
        'The captured output contains the credential.'
    Assert-True (-not $log.Contains($secret, [StringComparison]::Ordinal)) `
        'The process log contains the credential.'
}

Test-Case 'Native command failures include protected output' {
    $message = Get-ThrownMessage -Action {
        Invoke-DemoReadyNative `
            -FilePath 'pwsh' `
            -Arguments @('-NoProfile', '-Command', "Write-Error 'Synthetic failure.'; exit 7") `
            -WorkingDirectory $root `
            -Quiet
    }
    Assert-True ($message.Contains('Synthetic failure.', [StringComparison]::Ordinal)) `
        'The protected native command output was absent from the error.'
}

Test-Case 'Readiness replaces stale state atomically and validates report paths' {
    $workspace = New-TestDirectory -Name 'readiness'
    $report = Join-Path $workspace 'readiness.json'
    Write-DemoReadyJsonAtomic -Path $report -Value ([ordered]@{ status = 'ready' })
    Write-DemoReadyJsonAtomic -Path $report -Value ([ordered]@{ status = 'in-progress' })
    $state = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    Assert-Equal $state.status 'in-progress' 'A stale ready report survived initialization.'

    Assert-Throws `
        -Action {
            Assert-DemoReadyReportPath `
                -RepositoryRoot $root `
                -RuntimeRoot (Join-Path $root '.demo-ready') `
                -ReportPath (Join-Path $root 'readiness.json')
        } `
        -ExpectedFragment 'must be under the ignored .demo-ready directory' `
        -Message 'An unignored in-repository report path was accepted.'

    Assert-True ($invokeSource.IndexOf("status = 'in-progress'", [StringComparison]::Ordinal) -lt
        $invokeSource.IndexOf("status = 'ready'", [StringComparison]::Ordinal)) `
        'Stale readiness is not replaced before the ready result.'
    Assert-True ($invokeSource.Contains("status = 'failed'", [StringComparison]::Ordinal)) `
        'The startup catch path does not write failed readiness.'
    $catchSource = $invokeSource.Substring($invokeSource.LastIndexOf('catch {', [StringComparison]::Ordinal))
    foreach ($metadata in @('environmentName = $EnvironmentName', 'environments = $environmentNames',
        'selections = $selection', 'clonedByThisRun')) {
        Assert-True ($catchSource.Contains($metadata, [StringComparison]::Ordinal)) `
            "The failed readiness report omits '$metadata'."
    }
    Assert-True ($invokeSource.LastIndexOf('Assert-DemoReadyGitStatusPreserved', [StringComparison]::Ordinal) -lt
        $invokeSource.LastIndexOf('Write-DemoReadyJsonAtomic -Path $ReportPath -Value $report',
            [StringComparison]::Ordinal)) `
        'Ready state is written before the final external repository integrity check.'
}

Test-Case 'Package proxy preflight uses a supported GET request' {
    $commonSource = Get-Content -LiteralPath (Join-Path $scriptRoot 'DemoReady\Common.ps1') -Raw
    Assert-True ($commonSource.Contains('$proxyResponse = Invoke-WebRequest', [StringComparison]::Ordinal)) `
        'The package proxy reachability check is missing.'
    Assert-True ($commonSource.Contains('-Method Get', [StringComparison]::Ordinal)) `
        'The package proxy check does not use GET.'
    Assert-True (-not $commonSource.Contains('-Method Head', [StringComparison]::Ordinal)) `
        'The package proxy check still uses unsupported HEAD.'
    Assert-True ($invokeSource.Contains('Assert-DemoReadyNuGetProxy -RepositoryRoot $repositoryRoot',
            [StringComparison]::Ordinal)) `
        'The startup script does not verify the Microsoft package feed proxy.'
}

Test-Case 'Every azd child process receives the required user agent' {
    $commonSource = Get-Content -LiteralPath (Join-Path $scriptRoot 'DemoReady\Common.ps1') -Raw
    Assert-True ($commonSource.Contains("AZURE_DEV_USER_AGENT = 'microsoft_foundry_skill'",
            [StringComparison]::Ordinal)) `
        'The azd wrapper does not set the required user agent.'
    Assert-True (-not [regex]::IsMatch($invokeSource, '(?m)&\s+azd\b')) `
        'The startup script bypasses the azd wrapper.'
    Assert-True (-not [regex]::IsMatch($validateSource, '(?m)&\s+azd\b')) `
        'The validation script bypasses the azd wrapper.'
}

Test-Case 'Documentation names the ready-to-go command and the validation command' {
    foreach ($document in @(
        (Join-Path $root 'README.md'),
        (Join-Path $root 'docs\deployment\demo-ready.md'),
        (Join-Path $root 'docs\presenter\RUNBOOK.md')
    )) {
        $content = Get-Content -LiteralPath $document -Raw
        Assert-True ($content.Contains('scripts\Invoke-DemoReady.ps1', [StringComparison]::Ordinal)) `
            "The document '$document' does not name the ready-to-go command."
        Assert-True ($content.Contains('scripts\Test-DemoReady.ps1', [StringComparison]::Ordinal)) `
            "The document '$document' does not name the validation command."
        foreach ($removed in @('-RunCloudValidation', '-SkipLiveRehearsal', '-SkipBuild')) {
            Assert-True (-not $content.Contains($removed, [StringComparison]::Ordinal)) `
                "The document '$document' still describes the removed switch '$removed'."
        }
        Assert-True (-not [regex]::IsMatch($content, '[A-Za-z]:\\Users\\')) `
            "The document '$document' contains a workstation-specific absolute path."
    }
}

# ------------------------------------------------------------- local processes

Test-Case 'PID identity rejects a stale process record' {
    $process = Get-Process -Id $PID
    $validRecord = [pscustomobject]@{
        pid = $PID
        processName = $process.ProcessName
        executablePath = $process.MainModule.FileName
        startTimeUtc = $process.StartTime.ToUniversalTime().ToString('O')
        commandMarker = ''
    }
    Assert-True (Test-DemoReadyProcessIdentity -Record $validRecord -Process $process) `
        'A matching live process record was rejected.'
    $staleRecord = $validRecord.PSObject.Copy()
    $staleRecord.startTimeUtc = [DateTimeOffset]::UtcNow.AddDays(-1).ToString('O')
    Assert-True (-not (Test-DemoReadyProcessIdentity -Record $staleRecord -Process $process)) `
        'A stale process record was accepted.'
}

Test-Case 'Stopping a demo process stops its recorded child tree' {
    foreach ($stopMode in @('job', 'snapshot-fallback')) {
    $workspace = New-TestDirectory -Name "process-tree-$stopMode"
    $childPath = Join-Path $workspace 'child.pid'
    $statePath = Join-Path $workspace 'processes.json'
    $childCode = @"
Write-Output 'InstrumentationKey=44444444-4444-4444-4444-444444444444'
Start-Sleep -Seconds 1
`$startInfo = [Diagnostics.ProcessStartInfo]::new()
`$startInfo.FileName = 'pwsh'
`$startInfo.UseShellExecute = `$false
`$null = `$startInfo.ArgumentList.Add('-NoProfile')
`$null = `$startInfo.ArgumentList.Add('-Command')
`$null = `$startInfo.ArgumentList.Add('Start-Sleep -Seconds 60')
`$child = [Diagnostics.Process]::Start(`$startInfo)
Set-Content -LiteralPath '$childPath' -Value `$child.Id
`$child.WaitForExit()
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($childCode))
    $record = Start-DemoReadyProcess `
        -Name 'tree-test' `
        -FilePath 'pwsh' `
        -Arguments @('-NoProfile', '-EncodedCommand', $encoded) `
        -WorkingDirectory $root `
        -Environment @{} `
        -LogDirectory $workspace `
        -CommandMarker '-EncodedCommand' `
        -Endpoints @('http://localhost:1/') `
        -ScriptRoot $scriptRoot
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $childPath) -and $timer.Elapsed.TotalSeconds -lt 20) {
        Start-Sleep -Milliseconds 100
    }
    Assert-True (Test-Path -LiteralPath $childPath) 'The child process did not start.'
    $childPid = [int](Get-Content -LiteralPath $childPath -Raw)
    if ($stopMode -ceq 'snapshot-fallback') {
        $record.jobName = "Local\PublicSectorAgentDemos-$PID-$([Guid]::NewGuid().ToString('N'))"
    }
    Write-DemoReadyJsonAtomic `
        -Path $statePath `
        -Value (ConvertTo-DemoReadyProcessFile -Processes @($record))

    & $stopPath -StatePath $statePath -OwnedOnly:$false -Name 'tree-test'

    Assert-True (Wait-ProcessExit -ProcessId $record.pid) `
        'The recorded root process is still running.'
    Assert-True (Wait-ProcessExit -ProcessId $childPid) `
        'A recorded child process is still running.'
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    Assert-Equal @($state.processes).Count 0 'A verified stopped process was retained.'
    $log = Get-Content -LiteralPath (Join-Path $workspace 'tree-test.stdout.log') -Raw
    Assert-True (-not $log.Contains('44444444-4444-4444-4444-444444444444', [StringComparison]::Ordinal)) `
        'The supervised process log contains routing metadata.'
    Assert-True ($log.Contains('InstrumentationKey=******', [StringComparison]::Ordinal)) `
        'The supervised process log did not record its masked output.'
    }
}

Test-Case 'Exited process records do not block retry' {
    $workspace = New-TestDirectory -Name 'stale-process'
    $statePath = Join-Path $workspace 'processes.json'
    $record = [ordered]@{
        name = 'stale-test'
        pid = 2147483647
        processName = 'pwsh'
        executablePath = 'pwsh.exe'
        startTimeUtc = [DateTimeOffset]::UtcNow.AddDays(-1).ToString('O')
        commandMarker = ''
        jobName = 'Local\PublicSectorAgentDemos-1-11111111111111111111111111111111'
        endpoints = @()
        status = 'running'
    }
    Write-DemoReadyJsonAtomic -Path $statePath -Value ([ordered]@{
        version = 1
        processes = @($record)
    })

    & $stopPath -StatePath $statePath -OwnedOnly:$false -Name 'stale-test'

    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    Assert-Equal @($state.processes).Count 0 'An exited process record blocked the next startup.'
}

Test-Case 'Remote Docker builds exclude generated workspace state' {
    $dockerIgnore = Get-Content -LiteralPath (Join-Path $root '.dockerignore')
    Assert-True ($dockerIgnore -contains '.vs') `
        'The Docker context includes Visual Studio workspace files.'
    Assert-True ($dockerIgnore -contains '.demo-ready') `
        'The Docker context includes demo-ready runtime files.'
}

# --------------------------------------------------------------------- teardown

. (Join-Path $PSScriptRoot 'Bundled-DemoReady.Tests.ps1')
. (Join-Path $PSScriptRoot 'Local-Integrations.Tests.ps1')

if (Test-Path -LiteralPath $testRoot) {
    Remove-Item -LiteralPath $testRoot -Recurse -Force
}

if ($script:failures.Count -gt 0) {
    $script:failures | ForEach-Object { Write-Error $_ }
    throw "$($script:failures.Count) automation test(s) failed."
}
if ($script:executedTests -eq 0) {
    throw "No automation tests matched '$TestFilter'."
}
Write-Host "$($script:executedTests) demo-ready automation tests passed (filter: $TestFilter)."
