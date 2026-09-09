# Loaded by Invoke-DemoReady.Tests.ps1. Uses its existing local fixture harness.

Test-Case 'Bundled resolution works without nested Git or existing Azure state' {
    $workspace = New-TestDirectory -Name 'bundled-paths'
    $repositoryRoot = New-GitRepository -Path (Join-Path $workspace 'main')
    foreach ($definition in @($definitions.defra, $definitions.assuranceBoard)) {
        $bundle = Join-Path $repositoryRoot $definition.BundledPath
        $null = New-FakeExternalRepository -Path $bundle -Definition $definition -OmitGit
        $resolved = Resolve-DemoReadyExternalRepository -Definition $definition `
            -RepositoryRoot $repositoryRoot -ParameterPath '' -SetupRepositories @{} `
            -ParameterAzdEnvironmentName ''
        Assert-Equal $resolved.Path $bundle 'The resolver did not choose the fixed bundle.'
        Assert-Equal $resolved.PathSource 'bundled' 'Bundled ownership was lost.'
        Assert-True (-not (Test-Path (Join-Path $bundle '.git'))) 'The resolver created nested Git metadata.'
        Assert-True (-not (Test-Path (Join-Path $bundle '.azure'))) 'The resolver created environment state.'
        Assert-Throws -Action {
            Resolve-DemoReadyExternalRepository -Definition $definition `
                -RepositoryRoot $repositoryRoot -ParameterPath $workspace -SetupRepositories @{} `
                -ParameterAzdEnvironmentName ''
        } -ExpectedFragment 'External deployment is not permitted' -Message 'An external override was accepted.'
    }
}

Test-Case 'Bundled resolution rejects nested Git metadata and linked directories' {
    $workspace = New-TestDirectory -Name 'bundled-boundary'
    $repositoryRoot = New-GitRepository -Path (Join-Path $workspace 'main')
    $bundle = Join-Path $repositoryRoot $definitions.defra.BundledPath
    $null = New-FakeExternalRepository -Path $bundle -Definition $definitions.defra
    Assert-Throws -Action {
        Assert-DemoReadyBundledPath -RepositoryRoot $repositoryRoot -Path $bundle `
            -RequiredPaths $definitions.defra.RequiredPaths
    } -ExpectedFragment 'nested Git metadata' -Message 'Nested Git metadata was accepted.'
    $outside = Join-Path $workspace 'outside'
    $null = New-Item -ItemType Directory -Path $outside -Force
    $link = Join-Path $repositoryRoot 'linked'
    $null = New-Item -ItemType Junction -Path $link -Target $outside
    try {
        Assert-Throws -Action {
            Assert-DemoReadyBundledPath -RepositoryRoot $repositoryRoot -Path $link -RequiredPaths @('missing')
        } -ExpectedFragment 'linked path' -Message 'A junction was accepted.'
    }
    finally {
        [IO.Directory]::Delete($link)
    }
}

Test-Case 'Fresh initialization creates only the requested owned environment' {
    $script:environmentCalls = [Collections.Generic.List[object]]::new()
    Invoke-WithMockedFunction -Functions @{
        'Invoke-DemoReadyAzd' = {
            param($Arguments, $WorkingDirectory, [switch]$CaptureOutput,
                [switch]$PreserveCapturedOutput, [switch]$Quiet, $SensitiveValues)
            $script:environmentCalls.Add(@($Arguments))
            if ($Arguments[0] -ceq 'env' -and $Arguments[1] -ceq 'list') {
                return "[]`nUpdate available: test`nTo update, run test"
            }
        }
    } -Action {
        $created = Initialize-DemoReadyAzdEnvironment -ContextPath $root `
            -EnvironmentName 'fresh-demo2' -Location 'swedencentral' `
            -SubscriptionId '11111111-1111-1111-1111-111111111111' `
            -PrincipalId '22222222-2222-2222-2222-222222222222'
        Assert-True $created 'A missing environment was not identified as new.'
        Assert-Equal ($script:environmentCalls[0] -join ' ') 'env list --output json' 'List must precede writes.'
        Assert-True ($script:environmentCalls[1] -contains 'new') 'A missing environment was not created.'
        Assert-True ($script:environmentCalls[1] -contains '--subscription') 'New environments must receive the subscription.'
        Assert-True ($script:environmentCalls[1] -contains '--location') 'New environments must receive the location.'
        Assert-Equal @($script:environmentCalls | Where-Object { $_[1] -ceq 'new' }).Count 1 'Unexpected environment creation.'
    }
}

Test-Case 'Bicep compilation avoids Windows stdout encoding and removes temporary output' {
    $workspace = New-TestDirectory -Name 'bicep-unicode'
    foreach ($relative in @('infra\main.bicep', 'src\PublicSectorAgentDemos.Demo2.Act\infra\main.bicepparam', 'src\PublicSectorAgentDemos.Demo3.Coordinate\infra\main.bicep')) {
        $null = New-TestFile -Path (Join-Path $workspace $relative) -Content '// compilation fixture'
    }
    $script:bicepOutputPaths = [Collections.Generic.List[string]]::new()
    Invoke-WithMockedFunction -Functions @{
        'Invoke-DemoReadyNative' = {
            param($FilePath, $Arguments, $WorkingDirectory, $Environment, $SensitiveValues, [switch]$Quiet)
            Assert-True ($Arguments -notcontains '--stdout') 'Unicode JSON was routed through Azure CLI stdout.'
            $index = [Array]::IndexOf($Arguments, '--outfile')
            Assert-True ($index -ge 0) 'Compilation has no output file.'
            $outputPath = $Arguments[$index + 1]
            $script:bicepOutputPaths.Add($outputPath)
            $null = New-TestFile -Path $outputPath -Content ([string][char]0x21d2)
        }
    } -Action {
        Assert-DemoReadyBicep -RepositoryRoot $workspace -ParameterEnvironment @{}
    }
    Assert-Equal $script:bicepOutputPaths.Count 3 'Not all template and parameter files were compiled.'
    foreach ($path in $script:bicepOutputPaths) {
        Assert-True (-not (Test-Path -LiteralPath $path)) 'Compiled output was left behind.'
    }
}

Test-Case 'Initialization propagates list and select failures without creating an environment' {
    foreach ($failureMode in @('list', 'select', 'invalid-json')) {
        $script:failureMode = $failureMode
        $script:environmentCalls = [Collections.Generic.List[object]]::new()
        Invoke-WithMockedFunction -Functions @{
            'Invoke-DemoReadyAzd' = {
                param($Arguments, $WorkingDirectory, [switch]$CaptureOutput,
                    [switch]$PreserveCapturedOutput, [switch]$Quiet, $SensitiveValues)
                $script:environmentCalls.Add(@($Arguments))
                if ($Arguments[1] -ceq $script:failureMode) { throw 'Authentication or CLI failure.' }
                if ($script:failureMode -ceq 'invalid-json') { return 'unexpected output' }
                return '[{"Name":"existing-demo2","IsDefault":true}]'
            }
        } -Action {
            $null = Get-ThrownMessage -Action {
                Initialize-DemoReadyAzdEnvironment $root 'existing-demo2' 'swedencentral' `
                    '11111111-1111-1111-1111-111111111111' '22222222-2222-2222-2222-222222222222'
            }
            Assert-Equal @($script:environmentCalls | Where-Object { $_[1] -ceq 'new' }).Count 0 `
                'A CLI failure caused blind environment creation.'
            Assert-Equal @($script:environmentCalls | Where-Object { $_[1] -ceq 'set' }).Count 0 `
                'Initialization continued after a CLI failure.'
        }
    }
}

Test-Case 'Existing initialization is idempotent and refuses subscription retargeting' {
    $script:environmentCalls = [Collections.Generic.List[object]]::new()
    Invoke-WithMockedFunction -Functions @{
        'Invoke-DemoReadyAzd' = {
            param($Arguments, $WorkingDirectory, [switch]$CaptureOutput,
                [switch]$PreserveCapturedOutput, [switch]$Quiet, $SensitiveValues)
            $script:environmentCalls.Add(@($Arguments))
            if ($Arguments[1] -ceq 'list') { return '[{"Name":"existing-demo2","IsDefault":true}]' }
            if ($Arguments[1] -ceq 'get-values') {
                return "AZURE_SUBSCRIPTION_ID=11111111-1111-1111-1111-111111111111`nAZURE_LOCATION=swedencentral"
            }
        }
    } -Action {
        $created = Initialize-DemoReadyAzdEnvironment $root 'existing-demo2' 'swedencentral' `
            '11111111-1111-1111-1111-111111111111' '22222222-2222-2222-2222-222222222222'
        Assert-True (-not $created) 'An existing environment was recreated.'
        Assert-Equal @($script:environmentCalls | Where-Object { $_[1] -ceq 'new' }).Count 0 'Rerun created an environment.'
        $script:environmentCalls.Clear()
        Assert-Throws -Action {
            Initialize-DemoReadyAzdEnvironment $root 'existing-demo2' 'swedencentral' `
                '33333333-3333-3333-3333-333333333333' '22222222-2222-2222-2222-222222222222'
        } -ExpectedFragment 'different AZURE_SUBSCRIPTION_ID' -Message 'Existing subscription was overwritten.'
        Assert-Equal @($script:environmentCalls | Where-Object { $_[1] -ceq 'set' }).Count 0 'Mismatched environment was changed.'
    }
}

Test-Case 'Fresh council defaults to Foundry IQ and preserves valid explicit providers' {
    Use-EnvironmentVariable -Values @{ COUNCIL_GROUNDING_PROVIDER = ''; WEBIQ_API_KEY = '' } -Action {
        $script:councilValues = @{}
        $script:councilWrites = @{}
        Invoke-WithMockedFunction -Functions @{
            'Get-DemoReadyAzdValues' = { param($ContextPath, $EnvironmentName, $SensitiveValues) return $script:councilValues }
            'Set-DemoReadyAzdValue' = {
                param($ContextPath, $EnvironmentName, $Name, $Value, $SensitiveValues)
                $script:councilWrites[$Name] = $Value
            }
        } -Action {
            Initialize-DemoReadyCouncilGrounding $root 'fresh-council'
            Assert-Equal $script:councilWrites.COUNCIL_GROUNDING_PROVIDER 'foundryiq' 'Fresh setup required Web IQ.'
            $script:councilValues = @{ AZURE_ENV_NAME = 'interrupted-council' }
            Initialize-DemoReadyCouncilGrounding $root 'interrupted-council'
            Assert-Equal $script:councilWrites.COUNCIL_GROUNDING_PROVIDER 'foundryiq' `
                'Resuming an unconfigured environment changed the provider default.'
            $script:councilValues = @{ COUNCIL_GROUNDING_PROVIDER = 'webiq'; WEBIQ_API_KEY = 'synthetic-web-key' }
            Initialize-DemoReadyCouncilGrounding $root 'fresh-council'
            Assert-Equal $script:councilWrites.COUNCIL_GROUNDING_PROVIDER 'webiq' 'An explicit provider was replaced.'
            $script:councilValues = @{ COUNCIL_GROUNDING_PROVIDER = 'webiq' }
            Assert-Throws -Action { Initialize-DemoReadyCouncilGrounding $root 'fresh-council' } `
                -ExpectedFragment 'requires WEBIQ_API_KEY' -Message 'Web IQ silently lost grounding.'
            $script:councilValues = @{ COUNCIL_GROUNDING_PROVIDER = 'unknown' }
            Assert-Throws -Action { Initialize-DemoReadyCouncilGrounding $root 'fresh-council' } `
                -ExpectedFragment 'must be foundryiq or webiq' -Message 'An invalid provider was accepted.'
            Assert-True (-not $script:councilWrites.ContainsKey('COUNCIL_DEBATE_MAX_ROUNDS')) 'Setup forced council rounds.'
        }
        $masked = Protect-DemoReadyText -Text 'WEBIQ_API_KEY=synthetic-web-key'
        Assert-True (-not $masked.Contains('synthetic-web-key', [StringComparison]::Ordinal)) 'A Web IQ credential was logged.'
    }
}

Test-Case 'azd environment writes preserve an explicitly empty optional value' {
    $script:emptyValueArguments = $null
    Invoke-WithMockedFunction -Functions @{
        'Invoke-DemoReadyNative' = {
            param($FilePath, $Arguments, $WorkingDirectory, $LogPath, $Environment,
                $SensitiveValues, [switch]$CaptureOutput, [switch]$PreserveCapturedOutput, [switch]$Quiet)
            $script:emptyValueArguments = @($Arguments)
        }
    } -Action {
        Set-DemoReadyAzdValue -ContextPath $root -EnvironmentName 'empty-key-council' `
            -Name 'WEBIQ_API_KEY' -Value ''
    }
    Assert-Equal $script:emptyValueArguments.Count 6 'The empty argument was lost.'
    Assert-Equal $script:emptyValueArguments[2] 'WEBIQ_API_KEY' 'The wrong setting was written.'
    Assert-Equal $script:emptyValueArguments[3] '' 'An optional empty setting could not pass through the azd wrapper.'
}

Test-Case 'Council generated dotenv preserves provider and round settings through the original hook' {
    $workspace = New-TestDirectory -Name 'council-dotenv'
    $projectDirectory = Join-Path $workspace 'src\GovernanceCouncil.Web'
    $null = New-Item -ItemType Directory -Path $projectDirectory -Force
    Copy-Item -LiteralPath (Join-Path $root 'src\PublicSectorAgentDemos.Demo3.Coordinate\src\GovernanceCouncil.Web\.env.template') `
        -Destination (Join-Path $projectDirectory '.env.template')
    $hookEnvironment = @{}
    $templateText = Get-Content -LiteralPath (Join-Path $projectDirectory '.env.template') -Raw
    foreach ($match in [regex]::Matches($templateText, '\$\{([A-Z_][A-Z_0-9]*)')) {
        $hookEnvironment[$match.Groups[1].Value] = ''
    }
    $hookEnvironment.AZURE_TENANT_ID = '11111111-1111-1111-1111-111111111111'
    $hook = Join-Path $root 'src\PublicSectorAgentDemos.Demo3.Coordinate\infra\hooks\postprovision.ps1'
    Invoke-WithMockedFunction -Functions @{
        'dotnet' = { }
        'az' = { throw 'The hook must not call Azure in this local fixture.' }
    } -Action {
        foreach ($case in @(
            @{ Provider = 'foundryiq'; Rounds = ''; Expected = 5 },
            @{ Provider = 'webiq'; Rounds = '3'; Expected = 3 }
        )) {
            $hookEnvironment.COUNCIL_GROUNDING_PROVIDER = $case.Provider
            $hookEnvironment.COUNCIL_DEBATE_MAX_ROUNDS = $case.Rounds
            Use-EnvironmentVariable -Values $hookEnvironment -Action {
                Push-Location $workspace
                try { & $hook }
                finally { Pop-Location }
                $values = @{ COUNCIL_GROUNDING_PROVIDER = $case.Provider }
                if (-not [string]::IsNullOrEmpty($case.Rounds)) {
                    $values.COUNCIL_DEBATE_MAX_ROUNDS = $case.Rounds
                }
                $settings = Assert-DemoReadyCouncilEnvironment -ContextPath $workspace -Values $values
                Assert-Equal $settings.GroundingProvider $case.Provider 'The generated provider was replaced.'
                Assert-Equal $settings.DebateMaxRounds $case.Expected 'The generated round setting was replaced.'
            }
        }
    }
    Use-EnvironmentVariable -Values @{ COUNCIL_DEBATE_MAX_ROUNDS = '3' } -Action {
        $settings = Assert-DemoReadyCouncilEnvironment -ContextPath $workspace `
            -Values @{ COUNCIL_GROUNDING_PROVIDER = 'webiq' }
        Assert-Equal $settings.DebateMaxRounds 3 'A process-supplied round setting was not recognized.'
    }
    Assert-Throws -Action {
        Assert-DemoReadyCouncilEnvironment -ContextPath $workspace `
            -Values @{ COUNCIL_GROUNDING_PROVIDER = 'foundryiq'; COUNCIL_DEBATE_MAX_ROUNDS = '3' }
    } -ExpectedFragment 'selected grounding provider' -Message 'A stale dotenv provider was accepted.'
    Assert-Throws -Action {
        Assert-DemoReadyCouncilEnvironment -ContextPath $workspace `
            -Values @{ COUNCIL_GROUNDING_PROVIDER = 'webiq'; COUNCIL_DEBATE_MAX_ROUNDS = '4' }
    } -ExpectedFragment 'selected debate-round setting' -Message 'Stale dotenv rounds were accepted.'
    Assert-True ($invokeSource.Contains('Assert-DemoReadyCouncilEnvironment', [StringComparison]::Ordinal)) `
        'Startup does not check the generated dotenv file.'
    Assert-True ($invokeSource.Contains("liveScenario = 'not-verified'", [StringComparison]::Ordinal)) `
        'Startup incorrectly implies live scenario success.'
}

Test-Case 'Council readiness requires current agent and grounding initialization' {
    $workspace = New-TestDirectory -Name 'council-initialization'
    $logPath = Join-Path $workspace 'cross-government-coordinate.stdout.log'
    $null = New-TestFile -Path $logPath -Content "Knowledge base council-kb provisioned (model: fast)`nCouncil agents ready (Responses)"
    Wait-DemoReadyCouncilInitialization -LogDirectory $workspace -GroundingProvider foundryiq -TimeoutSeconds 1
    $null = New-TestFile -Path $logPath -Content 'Council agents ready (Responses)'
    Wait-DemoReadyCouncilInitialization -LogDirectory $workspace -GroundingProvider webiq -TimeoutSeconds 1
    Assert-Throws -Action {
        Wait-DemoReadyCouncilInitialization -LogDirectory $workspace -GroundingProvider foundryiq -TimeoutSeconds 1
    } -ExpectedFragment 'no current success evidence' -Message 'Agents without knowledge were claimed ready.'
    $null = New-TestFile -Path $logPath -Content "Council agents ready (Responses)`nFoundry IQ knowledge base provisioning failed"
    Assert-Throws -Action {
        Wait-DemoReadyCouncilInitialization -LogDirectory $workspace -GroundingProvider foundryiq -TimeoutSeconds 1
    } -ExpectedFragment 'initialization failed' -Message 'A caught startup failure was ignored.'
}

Test-Case 'Council rejects a round count outside the retained engine range' {
    $workspace = New-TestDirectory -Name 'invalid-council-rounds'
    $null = New-TestFile -Path (Join-Path $workspace 'src\GovernanceCouncil.Web\.env') `
        -Content "COUNCIL_GROUNDING_PROVIDER=foundryiq`nCOUNCIL_DEBATE_MAX_ROUNDS=21"
    Assert-Throws -Action {
        Assert-DemoReadyCouncilEnvironment -ContextPath $workspace `
            -Values @{ COUNCIL_GROUNDING_PROVIDER = 'foundryiq'; COUNCIL_DEBATE_MAX_ROUNDS = '21' }
    } -ExpectedFragment 'debate-round setting' -Message 'A round limit that the engine silently replaces was accepted.'
}

Test-Case 'Process snapshots reject changed creation times and commands before capture' {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Command pwsh).Source
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($argument in @('-NoProfile', '-Command', 'Start-Sleep -Seconds 60')) {
        $null = $startInfo.ArgumentList.Add($argument)
    }
    $childProcess = [Diagnostics.Process]::Start($startInfo)
    try {
        $rootProcess = Get-Process -Id $PID
        $script:realSnapshotRows = @(
            Get-CimInstance Win32_Process -Filter "ProcessId = $PID OR ProcessId = $($childProcess.Id)" |
                Select-Object ProcessId, ParentProcessId, CreationDate, Name, ExecutablePath, CommandLine
        )
        Assert-Equal $script:realSnapshotRows.Count 2 'The isolated process fixture was not captured.'
        foreach ($change in @('creation', 'command')) {
            $script:initialSnapshotRows = @($script:realSnapshotRows |
                Select-Object ProcessId, ParentProcessId, CreationDate, Name, ExecutablePath, CommandLine)
            $staleChild = $script:initialSnapshotRows | Where-Object ProcessId -eq $childProcess.Id
            if ($change -ceq 'creation') {
                $staleChild.CreationDate = $staleChild.CreationDate.AddTicks(-10)
            }
            else {
                $staleChild.CommandLine = 'a different original command'
            }
            Invoke-WithMockedFunction -Functions @{
                'Get-CimInstance' = {
                    param($ClassName, $Filter)
                    if ([string]::IsNullOrWhiteSpace($Filter)) { return $script:initialSnapshotRows }
                    $idMatch = [regex]::Match($Filter, 'ProcessId\s*=\s*(?<id>[0-9]+)')
                    return @($script:realSnapshotRows | Where-Object ProcessId -eq ([int]$idMatch.Groups['id'].Value))
                }
            } -Action {
                Assert-Throws -Action {
                    Get-DemoReadyProcessTreeSnapshot -RootPid $PID `
                        -RootStartTimeUtc ([DateTimeOffset]$rootProcess.StartTime.ToUniversalTime())
                } -ExpectedFragment 'changed before its identity' -Message 'A replacement process identity entered the stop snapshot.'
            }
            Assert-True (-not $childProcess.HasExited) 'The snapshot check stopped a process.'
        }
    }
    finally {
        if (-not $childProcess.HasExited) {
            Stop-Process -Id $childProcess.Id -ErrorAction Stop
        }
        $childProcess.Dispose()
    }
}

Test-Case 'Reloaded stop snapshots preserve the exact identity of a live process' {
    $process = Get-Process -Id $PID
    $snapshot = @([pscustomobject]@{
        pid = $PID
        startTimeUtc = $process.StartTime.ToUniversalTime().ToString('O')
    })
    Assert-True (-not (Test-DemoReadyTreeStopped -Snapshot $snapshot)) 'A live process was reported stopped.'
    $reloaded = @{ stopSnapshot = $snapshot } | ConvertTo-Json -Depth 5 | ConvertFrom-Json
    Assert-True (-not (Test-DemoReadyTreeStopped -Snapshot @($reloaded.stopSnapshot))) `
        'JSON timestamp conversion hid a live process.'
}

Test-Case 'Patriots is not discovered or probed without explicit opt-in' {
    Invoke-WithMockedFunction -Functions @{
        'Resolve-DemoReadyExternalRepository' = { throw 'Unexpected external discovery.' }
    } -Action {
        $link = Get-DemoReadyPatriotsLink -RepositoryRoot $root -ParameterPath '' `
            -SetupRepositories @{ patriots = [pscustomobject]@{ Path = 'C:\absent'; AzdEnvironmentName = '' } }
        Assert-Equal $link.Status 'not-configured' 'The external link was enabled implicitly.'
        Assert-True ($null -eq $link.Repository) 'A repository was invented for an absent integration.'
    }
    Assert-True (-not $invokeSource.Contains('Wait-DemoReadyEndpoint $patriots', [StringComparison]::Ordinal)) `
        'External readiness must not block owned readiness.'
}

Test-Case 'An explicitly requested but absent Patriots checkout does not block owned setup' {
    $workspace = New-TestDirectory -Name 'absent-patriots'
    $link = Get-DemoReadyPatriotsLink -RepositoryRoot $workspace `
        -ParameterPath (Join-Path $workspace 'absent') -SetupRepositories @{}
    Assert-Equal $link.Status 'not-configured' 'An absent external app was claimed configured.'
    Assert-True ($null -eq $link.Repository) 'An absent external app retained a repository link.'
}

Test-Case 'Owned stop preserves Patriots and external council records without changing them' {
    $workspace = New-TestDirectory -Name 'protected-processes'
    $statePath = Join-Path $workspace 'processes.json'
    $patriots = [pscustomobject]@{
        name = 'patriots'; pid = $PID; status = 'running'; privateExtension = 'preserve-me'
    }
    $externalCouncil = [pscustomobject]@{
        name = 'cross-government-coordinate'; pid = $PID; status = 'running'
        targetCommandMarker = 'C:\external\src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj'
    }
    $original = @($patriots, $externalCouncil)
    Write-DemoReadyJsonAtomic -Path $statePath -Value @{ version = 1; processes = $original }
    & $stopPath -StatePath $statePath
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    Assert-Equal ($state.processes | ConvertTo-Json -Depth 10 -Compress) `
        ($original | ConvertTo-Json -Depth 10 -Compress) 'Protected process records were changed.'
    $merged = ConvertTo-DemoReadyProcessFile -Processes @() -PreservedProcesses $original
    Assert-Equal @($merged.processes).Count 2 'Saving owned state removed protected records.'
    $presenter = [pscustomobject]@{
        name = 'presenter'; pid = $PID; targetCommandMarker = 'PublicSectorAgentDemos.Presenter'
        workingDirectory = $root
    }
    Assert-True (Test-DemoReadyOwnedProcess -Record $presenter -RepositoryRoot $root) 'An owned Presenter record was rejected.'
    $presenter.workingDirectory = Join-Path $workspace 'another-checkout'
    Assert-True (-not (Test-DemoReadyOwnedProcess -Record $presenter -RepositoryRoot $root)) `
        'A Presenter from another checkout was treated as owned.'
}

Test-Case 'Startup orders independent provisioning and uses only current outputs' {
    foreach ($call in @(
        "@('up', '--environment', `$environmentNames.Demo2, '--no-prompt')",
        "@('provision', '--environment', `$environmentNames.Demo3, '--no-prompt')",
        '-WorkingDirectory $contexts.Demo2',
        '-WorkingDirectory $contexts.Council',
        '-PreservedProcesses $preservedProcesses',
        'requiredSessionCount = 5',
        'ownedDeploymentCount = 4'
    )) {
        Assert-True ($invokeSource.Contains($call, [StringComparison]::Ordinal)) "Missing integration contract: $call"
    }
    Assert-True ($invokeSource.IndexOf('Initialize-DemoReadyCouncilGrounding') -lt
        $invokeSource.IndexOf("@('provision', '--environment', `$environmentNames.Demo3")) 'Council defaults must precede provisioning.'
    Assert-True ($invokeSource.IndexOf("@('up', '--environment', `$environmentNames.Demo2") -lt
        $invokeSource.IndexOf('$demo2Values = Get-DemoReadyAzdValues')) 'Act read stale outputs before deployment.'
    Assert-True ($invokeSource.IndexOf('$assuranceValues = Get-DemoReadyAzdValues') -lt
        $invokeSource.IndexOf("-Name 'cross-government-coordinate'")) 'Council startup did not use current outputs.'
    Assert-True (-not $invokeSource.Contains('-SkipCertificateCheck', [StringComparison]::Ordinal)) 'Startup bypassed TLS validation.'
}

Test-Case 'Nested Azure state is ignored and environment templates remain trackable' {
    foreach ($relativePath in @(
        'src\PublicSectorAgentDemos.Demo2.Act\.azure\fresh\.env',
        'src\PublicSectorAgentDemos.Demo3.Coordinate\.azure\fresh\config.json',
        'deploy\demo1\.azure\fresh\.env',
        'src\PublicSectorAgentDemos.Demo3.Coordinate\src\GovernanceCouncil.Web\.env'
    )) {
        & git -C $root check-ignore --quiet -- $relativePath
        Assert-Equal $LASTEXITCODE 0 "State is not ignored: $relativePath"
    }
    & git -C $root check-ignore --quiet --no-index -- 'src\PublicSectorAgentDemos.Demo3.Coordinate\src\GovernanceCouncil.Web\.env.template'
    Assert-Equal $LASTEXITCODE 1 'The council environment template is ignored.'
}
