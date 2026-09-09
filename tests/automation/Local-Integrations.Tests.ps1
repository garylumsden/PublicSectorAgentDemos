# Loaded by the existing automation harness. No live applications or Azure calls.

Test-Case 'Demo1 comparison startup reuses the selected environment without changing it' {
    $values = @{
        AZURE_AI_FOUNDRY_ENDPOINT = 'https://demo.services.ai.azure.com/api/projects/demo'
        COUNCIL_FAST_MODEL = 'existing-model'
        DEMO1_CITATION_IDENTITY_PATH = 'C:\fixtures\identity.json'
    }
    $before = $values | ConvertTo-Json -Compress
    $environment = Get-DemoReadyDemo1WebEnvironment -Demo1Values $values `
        -ApplicationInsightsConnectionString 'test-connection'
    Assert-Equal ($values | ConvertTo-Json -Compress) $before 'The selected azd values were changed.'
    foreach ($key in $values.Keys) {
        Assert-Equal $environment[$key] $values[$key] 'An existing Demo 1 setting was replaced.'
    }
    Assert-Equal $environment.ASPNETCORE_URLS 'http://localhost:5090' 'The comparison port is incorrect.'
    Assert-Equal $environment.APPLICATIONINSIGHTS_CONNECTION_STRING 'test-connection' 'Shared telemetry was omitted.'
    Assert-Equal $environment.OTEL_SERVICE_NAME 'PublicSectorAgentDemos.Demo1.Web' 'The role name is incorrect.'
    Assert-Equal $environment.AZURE_TOKEN_CREDENTIALS 'AzureCliCredential' 'Local identity selection was weakened.'
    foreach ($endpoint in @('', 'http://example.invalid/', 'https://user:password@example.invalid/')) {
        Assert-Throws -Action {
            Get-DemoReadyDemo1WebEnvironment -Demo1Values @{ AZURE_AI_FOUNDRY_ENDPOINT = $endpoint } `
                -ApplicationInsightsConnectionString 'test'
        } -ExpectedFragment 'valid AZURE_AI_FOUNDRY_ENDPOINT' -Message 'An invalid endpoint was accepted.'
    }
    Assert-True ($invokeSource.Contains('Wait-DemoReadyEndpoint "$($demo1WebUrl)health" -RequireSuccess')) `
        'Startup does not require the comparison health endpoint.'
}

Test-Case 'Relocated council stop ownership requires an exact old or new path pair' {
    foreach ($relative in @('demos\cross-government', 'src\PublicSectorAgentDemos.Demo3.Coordinate')) {
        $context = Join-Path $root $relative
        $record = [pscustomobject]@{
            name = 'cross-government-coordinate'
            workingDirectory = $context
            targetCommandMarker = Join-Path $context 'src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj'
        }
        Assert-True (Test-DemoReadyOwnedProcess $record $root) 'An exact council record was rejected.'
        $record.workingDirectory = Join-Path $root 'other-council'
        Assert-True (-not (Test-DemoReadyOwnedProcess $record $root)) 'A mismatched directory was accepted.'
        $record.PSObject.Properties.Remove('workingDirectory')
        Assert-True (Test-DemoReadyOwnedProcess $record $root) 'An exact legacy record without a directory was rejected.'
        $record.targetCommandMarker += '.different'
        Assert-True (-not (Test-DemoReadyOwnedProcess $record $root)) 'A partial project marker was accepted.'
    }
    $demo1 = [pscustomobject]@{
        name = 'demo1-comparison'; workingDirectory = $root
        targetCommandMarker = Join-Path $root 'src\PublicSectorAgentDemos.Demo1.Web\PublicSectorAgentDemos.Demo1.Web.csproj'
    }
    Assert-True (Test-DemoReadyOwnedProcess $demo1 $root) 'Demo 1 ownership was omitted.'
    $demo1.workingDirectory = Join-Path $root 'another-checkout'
    Assert-True (-not (Test-DemoReadyOwnedProcess $demo1 $root)) 'Another checkout was accepted as Demo 1.'
}

Test-Case 'Optional app stop requires exact ownership and project identity' {
    $workspace = New-TestDirectory -Name 'optional-record'
    foreach ($item in @(
        [pscustomobject]@{
            Name = 'tokens-and-credits'
            Project = 'src\TokensAndCredits.Web\TokensAndCredits.Web.csproj'
        },
        [pscustomobject]@{
            Name = 'external-patriots'
            Project = 'src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj'
        }
    )) {
        $record = [pscustomobject]@{
            name = $item.Name
            optionalExternalOwner = $root
            workingDirectory = $workspace
            targetCommandMarker = Join-Path $workspace $item.Project
            commandMarker = 'Start-DemoReadyProcessHost.ps1'
        }
        Assert-True (Test-DemoReadyManagedProcess $record $root) `
            "The managed optional app '$($item.Name)' was rejected."
        Assert-True (-not (Test-DemoReadyOwnedProcess $record $root)) `
            "The optional app '$($item.Name)' became repository-owned."
        foreach ($field in @('optionalExternalOwner', 'workingDirectory', 'targetCommandMarker', 'commandMarker')) {
            $changed = $record.PSObject.Copy()
            $changed.$field += '-wrong'
            Assert-True (-not (Test-DemoReadyManagedProcess $changed $root)) `
                "A mismatched $field was accepted for '$($item.Name)'."
            $changed.PSObject.Properties.Remove($field)
            Assert-True (-not (Test-DemoReadyManagedProcess $changed $root)) `
                "A missing $field was accepted for '$($item.Name)'."
        }
    }
}

Test-Case 'Patriots cannot enter the external build or process startup helpers' {
    Invoke-WithMockedFunction -Functions @{
        'Invoke-DemoReadyProjectBuild' = { throw 'Unexpected build call.' }
    } -Action {
        Assert-Throws -Action {
            Invoke-DemoReadyExternalBuild -Repository ([pscustomobject]@{ Identity = 'patriots' }) `
                -ProjectPath 'unused' -LogRoot 'unused' -GitBaseline ''
        } -ExpectedFragment 'Patriots is link-only' -Message 'Patriots entered the shared build helper.'
    }
    foreach ($name in @('patriots', 'patriots-coordinate')) {
        Assert-Throws -Action {
            Start-DemoReadyProcess -Name $name -FilePath 'unused' -Arguments @('unused') `
                -WorkingDirectory $root -Environment @{} -LogDirectory $testRoot `
                -CommandMarker 'unused' -Endpoints @('unused') -ScriptRoot $scriptRoot
        } -ExpectedFragment 'Patriots is link-only' -Message 'Patriots entered the shared process helper.'
    }
}

Test-Case 'Patriots remains protected even with an explicit stop name' {
    $workspace = New-TestDirectory -Name 'patriots-explicit-stop'
    $statePath = Join-Path $workspace 'processes.json'
    $records = @(
        [pscustomobject]@{ name = 'patriots'; pid = $PID; status = 'running' },
        [pscustomobject]@{ name = 'patriots-coordinate'; pid = $PID; status = 'running' }
    )
    Write-DemoReadyJsonAtomic -Path $statePath -Value @{ version = 1; processes = $records }
    & $stopPath -StatePath $statePath -OwnedOnly:$false -Name @('patriots', 'patriots-coordinate')
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    Assert-Equal ($state.processes | ConvertTo-Json -Compress) ($records | ConvertTo-Json -Compress) `
        'An explicit selection changed Patriots records.'
}

Test-Case 'Tokens discovery respects all four path sources without azd' {
    $workspace = New-TestDirectory -Name 'tokens-resolution'
    $repositoryRoot = New-GitRepository -Path (Join-Path $workspace 'main')
    $definition = $definitions.tokensAndCredits
    $paths = @{}
    foreach ($source in @('parameter', 'environment', 'setup', 'tokens-and-credits')) {
        $path = New-GitRepository -Path (Join-Path $workspace $source)
        $paths[$source] = New-FakeExternalRepository -Path $path -Definition $definition -OmitGit
    }
    $setup = @{ tokensAndCredits = [pscustomobject]@{ Path = $paths.setup; AzdEnvironmentName = '' } }
    Invoke-WithMockedFunction -Functions @{
        'Invoke-DemoReadyAzd' = { throw 'No azd call is allowed.' }
    } -Action {
        Use-EnvironmentVariable -Values @{ PSAD_TOKENS_AND_CREDITS_REPO_PATH = $paths.environment } -Action {
            $result = Resolve-DemoReadyTokensAndCredits $repositoryRoot $paths.parameter $setup
            Assert-Equal $result.Repository.PathSource 'parameter' 'Parameter precedence changed.'
            Assert-Equal $result.Status 'discovered' 'Discovery claimed readiness.'
            $result = Resolve-DemoReadyTokensAndCredits $repositoryRoot '' $setup
            Assert-Equal $result.Repository.PathSource 'environment-variable' 'Environment precedence changed.'
        }
        Use-EnvironmentVariable -Values @{ PSAD_TOKENS_AND_CREDITS_REPO_PATH = '' } -Action {
            $result = Resolve-DemoReadyTokensAndCredits $repositoryRoot '' $setup
            Assert-Equal $result.Repository.PathSource 'setup-file' 'Setup precedence changed.'
            $result = Resolve-DemoReadyTokensAndCredits $repositoryRoot '' @{}
            Assert-Equal $result.Repository.PathSource 'sibling-folder' 'Sibling discovery was omitted.'
            Assert-Equal $result.Repository.AzdEnvironmentName '' 'Discovery required an azd environment.'
        }
    }
}

Test-Case 'Tokens absent and invalid checkouts are explicit nonfatal outcomes' {
    $workspace = New-TestDirectory -Name 'tokens-unavailable'
    $rootPath = Join-Path $workspace 'main'
    Use-EnvironmentVariable -Values @{ PSAD_TOKENS_AND_CREDITS_REPO_PATH = '' } -Action {
        $absent = Resolve-DemoReadyTokensAndCredits $rootPath '' @{}
        Assert-Equal $absent.Status 'not-configured' 'An absent optional sibling was reported ready.'
        $requested = Resolve-DemoReadyTokensAndCredits $rootPath (Join-Path $workspace 'absent') @{}
        Assert-Equal $requested.Status 'not-configured' 'An absent explicit checkout blocked main setup.'
        $invalid = Resolve-DemoReadyTokensAndCredits $rootPath $workspace @{}
        Assert-Equal $invalid.Status 'failed' 'An invalid checkout silently became not configured.'
    }
}

Test-Case 'Tokens setup permits a path but forbids azd and cloud configuration' {
    $workspace = New-TestDirectory -Name 'tokens-setup'
    $path = Join-Path $workspace 'repositories.json'
    $null = New-TestFile $path '{"version":1,"repositories":{"tokensAndCredits":{"path":"C:\\fixtures\\tokens"}}}'
    $setup = Read-DemoReadySetupFile $path $schemaPath
    Assert-Equal $setup.tokensAndCredits.AzdEnvironmentName '' 'The optional setup required azd.'
    $null = New-TestFile $path '{"version":1,"repositories":{"tokensAndCredits":{"path":"C:\\fixtures\\tokens","azdEnvironment":"demo"}}}'
    Assert-Throws -Action { Read-DemoReadySetupFile $path $schemaPath } `
        -ExpectedFragment 'does not match' -Message 'Tokens setup accepted an azd environment.'
    $example = Read-DemoReadySetupFile (Join-Path $scriptRoot 'DemoReady\repositories.example.json') $schemaPath
    Assert-True ($example.ContainsKey('tokensAndCredits')) 'The optional path example is missing.'
}

Test-Case 'Tokens local health accepts only its manifest and never makes a model request' {
    $script:healthResponse = [pscustomobject]@{
        StatusCode = 200
        Content = '{"origin":"local-static-embedding","dimensions":50,"vocabularyCount":10000}'
    }
    $script:healthRequests = 0
    Invoke-WithMockedFunction -Functions @{
        'Invoke-WebRequest' = {
            param($Uri, $Method, $TimeoutSec, $MaximumRedirection)
            Assert-Equal $Uri 'http://localhost:5041/api/embeddings/manifest' 'Health used a different route.'
            Assert-Equal $Method 'Get' 'Health made a non-GET request.'
            Assert-Equal $MaximumRedirection 0 'Health followed a redirect.'
            $script:healthRequests++
            return $script:healthResponse
        }
    } -Action {
        Wait-DemoReadyTokensHealth -TimeoutSeconds 1
        Assert-Equal $script:healthRequests 1 'The local health request was duplicated.'
        foreach ($response in @(
            @{ StatusCode = 302; Content = '{}' },
            @{ StatusCode = 200; Content = '<html>another application</html>' },
            @{ StatusCode = 200; Content = '{"origin":"cloud","dimensions":50,"vocabularyCount":10000}' },
            @{ StatusCode = 200; Content = '{"origin":"local-static-embedding","dimensions":50,"vocabularyCount":"10000"}' },
            @{ StatusCode = 200; Content = '{"origin":"local-static-embedding","dimensions":0,"vocabularyCount":10000}' }
        )) {
            $script:healthResponse = [pscustomobject]$response
            Assert-Throws -Action { Wait-DemoReadyTokensHealth -TimeoutSeconds 1 } `
                -ExpectedFragment 'did not return its local embedding manifest' -Message 'Invalid health became ready.'
        }
    }
}

Test-Case 'Tokens process serialization retains explicit ownership and exact identity fields' {
    $startDefinition = (Get-Command Start-DemoReadyProcess).ScriptBlock.ToString()
    $returnBlock = [regex]::Match($startDefinition, '(?s)return \[pscustomobject\]@\{(?<record>.*?)\r?\n    \}')
    Assert-True $returnBlock.Success 'The recorded startup result was not found.'
    Assert-True ($returnBlock.Groups['record'].Value.Contains('optionalExternalOwner = $OptionalExternalOwner')) `
        'Startup did not return its explicit external ownership marker.'
    $record = [pscustomobject]@{
        name = 'tokens-and-credits'; pid = 123; processName = 'pwsh'; executablePath = 'C:\fixtures\pwsh.exe'
        startTimeUtc = '2026-09-06T10:00:00.0000000Z'; commandMarker = 'Start-DemoReadyProcessHost.ps1'
        targetCommandMarker = 'C:\fixtures\tokens\src\TokensAndCredits.Web\TokensAndCredits.Web.csproj'
        targetPid = 124; targetStartTimeUtc = '2026-09-06T10:00:01.0000000Z'
        workingDirectory = 'C:\fixtures\tokens'; optionalExternalOwner = $root
        jobName = 'Local\PublicSectorAgentDemos-123-11111111111111111111111111111111'
        endpoints = @('http://localhost:5041/'); status = 'running'
    }
    $serialized = ConvertTo-DemoReadyProcessFile -Processes @($record)
    $reloaded = ($serialized | ConvertTo-Json -Depth 10 | ConvertFrom-Json).processes[0]
    Assert-True (Test-DemoReadyManagedProcess $reloaded $root) 'Saved external ownership could not be loaded.'
    foreach ($key in @('pid', 'processName', 'executablePath', 'commandMarker', 'targetCommandMarker', 'targetPid', 'jobName')) {
        Assert-Equal $reloaded.$key $record.$key "Serialization changed $key."
    }
    Assert-Equal (ConvertTo-DemoReadyUtcTimestamp $reloaded.startTimeUtc).UtcTicks `
        (ConvertTo-DemoReadyUtcTimestamp $record.startTimeUtc).UtcTicks 'Serialization changed the exact start time.'
}

Test-Case 'Tokens startup records only its local runtime and keeps every optional failure nonfatal' {
    $workspace = New-TestDirectory -Name 'tokens-startup'
    foreach ($failure in @('', 'port', 'build', 'start', 'health', 'git')) {
        $script:tokensFailure = $failure
        $script:tokensCalls = [Collections.Generic.List[string]]::new()
        $integration = [pscustomobject]@{
            Status = 'discovered'
            Repository = [pscustomobject]@{ Path = $workspace; Identity = 'tokensAndCredits'; DisplayName = 'Tokens' }
            GitBaseline = ''
            GitStatusPreserved = $null
        }
        $processes = [Collections.Generic.List[object]]::new()
        Invoke-WithMockedFunction -Functions @{
            'Assert-DemoReadyPortsFree' = {
                param($Ports)
                $script:tokensCalls.Add('port')
                Assert-Equal ($Ports -join ',') '5041' 'Optional startup inspected unrelated ports.'
                if ($script:tokensFailure -ceq 'port') { throw 'busy' }
            }
            'Invoke-DemoReadyExternalBuild' = {
                param($Repository, $ProjectPath, $LogRoot, $GitBaseline, $SensitiveValues)
                $script:tokensCalls.Add('build')
                Assert-True ($ProjectPath.EndsWith('src\TokensAndCredits.Web\TokensAndCredits.Web.csproj')) 'The wrong project was built.'
                if ($script:tokensFailure -ceq 'build') { throw 'build failed' }
            }
            'Start-DemoReadyProcess' = {
                param($Name, $FilePath, $Arguments, $WorkingDirectory, $Environment,
                    $LogDirectory, $CommandMarker, $Endpoints, $ScriptRoot, $OptionalExternalOwner, $SensitiveValues)
                $script:tokensCalls.Add('start')
                Assert-Equal $Name 'tokens-and-credits' 'A different external app was started.'
                Assert-Equal $OptionalExternalOwner $root 'Explicit management was not recorded.'
                Assert-Equal $Environment.Count 2 'Unexpected external environment settings were added.'
                Assert-Equal $Environment.ASPNETCORE_URLS 'http://localhost:5041' 'The optional port is wrong.'
                Assert-Equal $Environment.DOTNET_ENVIRONMENT 'Development' 'The Foundry development configuration was not enabled.'
                Assert-True ($Arguments -contains '--no-launch-profile') 'External launch profiles were enabled.'
                if ($script:tokensFailure -ceq 'start') { throw 'start failed' }
                return [pscustomobject]@{ name = $Name; process = [pscustomobject]@{ HasExited = $false } }
            }
            'Wait-DemoReadyTokensHealth' = {
                $script:tokensCalls.Add('health')
                if ($script:tokensFailure -ceq 'health') { throw 'health failed' }
            }
            'Assert-DemoReadyGitStatusPreserved' = {
                param($RepositoryPath, $Baseline, $DisplayName)
                $script:tokensCalls.Add('git')
                if ($script:tokensFailure -ceq 'git') { throw 'Git changed' }
            }
            'Invoke-DemoReadyAzd' = { throw 'No azd call is permitted.' }
        } -Action {
            Start-DemoReadyTokensAndCredits -Integration $integration -RepositoryRoot $root `
                -LogRoot $workspace -ScriptRoot $scriptRoot -Processes $processes
        }
        Assert-Equal $integration.Status ($failure -ceq '' ? 'ready' : 'failed') 'Optional failure changed main control flow.'
        Assert-Equal $integration.GitStatusPreserved ($failure -cne 'git') 'External Git preservation was misreported.'
        $expectedRecords = $failure -in @('', 'health', 'git') ? 1 : 0
        Assert-Equal $processes.Count $expectedRecords 'A started optional process was not retained for safe stop.'
        if ($failure -ceq '') {
            Assert-Equal ($script:tokensCalls -join ',') 'port,build,start,health,git' 'The optional lifecycle order is incorrect.'
        }

    }
    foreach ($status in @('not-configured', 'failed')) {
        Invoke-WithMockedFunction -Functions @{
            'Assert-DemoReadyPortsFree' = { throw 'An absent optional app inspected ports.' }
        } -Action {
            Start-DemoReadyTokensAndCredits -Integration ([pscustomobject]@{ Status = $status }) `
                -RepositoryRoot $root -LogRoot $workspace -ScriptRoot $scriptRoot `
                -Processes ([Collections.Generic.List[object]]::new())
        }
    }
}
