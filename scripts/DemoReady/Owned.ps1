# Repository-owned deployment steps.
# Dot-source this file after Common.ps1. It defines functions only.

function Get-DemoReadyDemo1WebEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$Demo1Values,
        [Parameter(Mandatory)][string]$ApplicationInsightsConnectionString
    )

    $endpoint = $null
    if (-not [Uri]::TryCreate([string]$Demo1Values['AZURE_AI_FOUNDRY_ENDPOINT'], [UriKind]::Absolute, [ref]$endpoint) -or
        $endpoint.Scheme -cne 'https' -or -not [string]::IsNullOrEmpty($endpoint.UserInfo)) {
        throw 'The existing Demo 1 environment must provide a valid AZURE_AI_FOUNDRY_ENDPOINT.'
    }
    $values = @{}
    foreach ($entry in $Demo1Values.GetEnumerator()) {
        $values[$entry.Key] = [string]$entry.Value
    }
    $values.ASPNETCORE_URLS = 'http://localhost:5090'
    $values.APPLICATIONINSIGHTS_CONNECTION_STRING = $ApplicationInsightsConnectionString
    $values.OTEL_SERVICE_NAME = 'PublicSectorAgentDemos.Demo1.Web'
    $values.AZURE_TOKEN_CREDENTIALS = 'AzureCliCredential'
    return $values
}

function Get-DemoReadyEnvironmentNames {
    # Foundation and Ground share Demo 1. Optional names apply only to new environments.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [ValidatePattern('^[a-z][a-z0-9-]{1,18}[a-z0-9]$')]
        [string]$BaseName
    )

    [ordered]@{
        Demo1 = "$BaseName-demo1"
        Demo2 = "$BaseName-demo2"
        Demo3 = "$BaseName-demo3"
        Demo4 = "$BaseName-demo4"
        Patriots = "$BaseName-patriots"
        TokensAndCredits = "$BaseName-tokens"
    }
}

function Initialize-DemoReadyAzdEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContextPath,
        [Parameter(Mandatory)][string]$EnvironmentName,
        [Parameter(Mandatory)][string]$Location,
        [Parameter(Mandatory)][string]$SubscriptionId,
        [Parameter(Mandatory)][string]$PrincipalId
    )

    $environments = @(Get-DemoReadyAzdEnvironments -ContextPath $ContextPath)
    $matches = @($environments | Where-Object { [string]$_.Name -ceq $EnvironmentName })
    if ($matches.Count -gt 1) {
        throw "azd returned duplicate environments named '$EnvironmentName'."
    }
    $isNew = $matches.Count -eq 0
    if (-not $isNew) {
        Invoke-DemoReadyAzd `
            -Arguments @('env', 'select', $EnvironmentName, '--no-prompt') `
            -WorkingDirectory $ContextPath `
            -Quiet
        $values = Get-DemoReadyAzdValues $ContextPath $EnvironmentName
        foreach ($setting in @{
            AZURE_SUBSCRIPTION_ID = $SubscriptionId
            AZURE_LOCATION = $Location
        }.GetEnumerator()) {
            $existing = [string]$values[$setting.Key]
            if (-not [string]::IsNullOrWhiteSpace($existing) -and $existing -ine $setting.Value) {
                throw "The existing '$EnvironmentName' environment has a different $($setting.Key). Use its existing setting or a new presentation base."
            }
        }
    }
    else {
        Invoke-DemoReadyAzd `
            -Arguments @(
                'env', 'new', $EnvironmentName, '--subscription', $SubscriptionId,
                '--location', $Location, '--no-prompt'
            ) `
            -WorkingDirectory $ContextPath `
            -Quiet
    }

    Set-DemoReadyAzdValue $ContextPath $EnvironmentName 'AZURE_SUBSCRIPTION_ID' $SubscriptionId
    Set-DemoReadyAzdValue $ContextPath $EnvironmentName 'AZURE_LOCATION' $Location
    Set-DemoReadyAzdValue $ContextPath $EnvironmentName 'AZURE_PRINCIPAL_ID' $PrincipalId
    return $isNew
}

function Initialize-DemoReadyAzureContext {
    # Selects the subscription and checks the Azure CLI and azd sign-in state.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [AllowEmptyString()][string]$SubscriptionId
    )

    $accountJson = Invoke-DemoReadyNative `
        -FilePath 'az' `
        -Arguments @('account', 'show', '--query', '{id:id,tenantId:tenantId,state:state}', '--output', 'json') `
        -WorkingDirectory $RepositoryRoot `
        -CaptureOutput `
        -PreserveCapturedOutput `
        -Quiet
    $account = $accountJson | ConvertFrom-Json
    if ([string]$account.state -ne 'Enabled' -or
        [string]$account.id -notmatch '^[0-9a-fA-F-]{36}$' -or
        [string]$account.tenantId -notmatch '^[0-9a-fA-F-]{36}$') {
        throw 'Azure CLI authentication did not return an enabled subscription.'
    }
    if ([string]::IsNullOrWhiteSpace($SubscriptionId)) {
        $SubscriptionId = [string]$account.id
    }
    Invoke-DemoReadyNative `
        -FilePath 'az' `
        -Arguments @('account', 'set', '--subscription', $SubscriptionId) `
        -WorkingDirectory $RepositoryRoot `
        -Quiet
    $selectedJson = Invoke-DemoReadyNative `
        -FilePath 'az' `
        -Arguments @('account', 'show', '--query', '{id:id,tenantId:tenantId,state:state}', '--output', 'json') `
        -WorkingDirectory $RepositoryRoot `
        -CaptureOutput `
        -PreserveCapturedOutput `
        -Quiet
    $selected = $selectedJson | ConvertFrom-Json
    $tenantId = [string]$selected.tenantId
    if ([string]$selected.state -ne 'Enabled' -or
        [string]$selected.id -cne $SubscriptionId -or
        $tenantId -notmatch '^[0-9a-fA-F-]{36}$') {
        throw 'Azure CLI did not select the requested subscription.'
    }

    Invoke-DemoReadyAzd `
        -Arguments @('auth', 'login', '--check-status') `
        -WorkingDirectory $RepositoryRoot `
        -SensitiveValues @($tenantId) `
        -Quiet
    $principalId = Invoke-DemoReadyNative `
        -FilePath 'az' `
        -Arguments @('ad', 'signed-in-user', 'show', '--query', 'id', '--output', 'tsv', '--only-show-errors') `
        -WorkingDirectory $RepositoryRoot `
        -SensitiveValues @($tenantId) `
        -CaptureOutput `
        -PreserveCapturedOutput `
        -Quiet
    $principalId = $principalId.Trim()
    if ($principalId -notmatch '^[0-9a-fA-F-]{36}$') {
        throw 'Azure CLI did not return the signed-in user object ID.'
    }

    return [pscustomobject]@{
        SubscriptionId = $SubscriptionId
        TenantId = $tenantId
        PrincipalId = $principalId
    }
}

function Initialize-DemoReadyCouncilGrounding {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContextPath,
        [Parameter(Mandatory)][string]$EnvironmentName,
        [string[]]$SensitiveValues = @()
    )

    $values = Get-DemoReadyAzdValues $ContextPath $EnvironmentName $SensitiveValues
    $provider = [string]$values['COUNCIL_GROUNDING_PROVIDER']
    if ([string]::IsNullOrWhiteSpace($provider)) {
        $provider = [Environment]::GetEnvironmentVariable('COUNCIL_GROUNDING_PROVIDER')
    }
    $webIqKey = [string]$values['WEBIQ_API_KEY']
    if ([string]::IsNullOrWhiteSpace($webIqKey)) {
        $webIqKey = [Environment]::GetEnvironmentVariable('WEBIQ_API_KEY')
    }
    if ([string]::IsNullOrWhiteSpace($provider)) {
        $provider = [string]::IsNullOrWhiteSpace($webIqKey) ? 'foundryiq' : 'webiq'
    }
    $provider = $provider.Trim().ToLowerInvariant()
    if ($provider -cnotin @('foundryiq', 'webiq')) {
        throw 'COUNCIL_GROUNDING_PROVIDER must be foundryiq or webiq.'
    }
    if ($provider -ceq 'webiq' -and [string]::IsNullOrWhiteSpace($webIqKey)) {
        throw 'Web IQ requires WEBIQ_API_KEY. Set it in the selected council environment or explicitly select foundryiq.'
    }
    Set-DemoReadyAzdValue $ContextPath $EnvironmentName 'COUNCIL_GROUNDING_PROVIDER' $provider $SensitiveValues
    # The retained parameter file references this value even when Foundry IQ does not use it.
    Set-DemoReadyAzdValue $ContextPath $EnvironmentName 'WEBIQ_API_KEY' ([string]$webIqKey) $SensitiveValues
}

function Assert-DemoReadyCouncilEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContextPath,
        [Parameter(Mandatory)][hashtable]$Values
    )

    $envPath = Join-Path $ContextPath 'src\GovernanceCouncil.Web\.env'
    $templatePath = Join-Path $ContextPath 'src\GovernanceCouncil.Web\.env.template'
    if (-not (Test-Path -LiteralPath $envPath -PathType Leaf)) {
        throw 'The council postprovision hook did not generate its local .env file.'
    }
    $generated = Get-DemoReadyDotEnvValues -Paths @($envPath)
    $provider = [string]$Values['COUNCIL_GROUNDING_PROVIDER']
    if ($provider -cnotin @('foundryiq', 'webiq') -or
        [string]$generated['COUNCIL_GROUNDING_PROVIDER'] -cne $provider) {
        throw 'The generated council .env does not match the selected grounding provider. Run the council postprovision hook for the selected environment.'
    }

    # DotEnvLoader overwrites process values. Match the retained hook's precedence and template default.
    $expectedRounds = [string]$Values['COUNCIL_DEBATE_MAX_ROUNDS']
    if ([string]::IsNullOrEmpty($expectedRounds)) {
        $expectedRounds = [Environment]::GetEnvironmentVariable('COUNCIL_DEBATE_MAX_ROUNDS')
    }
    if ([string]::IsNullOrEmpty($expectedRounds)) {
        $template = Get-DemoReadyDotEnvValues -Paths @($templatePath)
        $roundDefault = [regex]::Match(
            [string]$template['COUNCIL_DEBATE_MAX_ROUNDS'],
            '^\$\{COUNCIL_DEBATE_MAX_ROUNDS:-(?<rounds>[1-9][0-9]*)\}$')
        if (-not $roundDefault.Success) {
            throw 'The council template does not declare a valid debate-round default.'
        }
        $expectedRounds = $roundDefault.Groups['rounds'].Value
    }
    $expectedCount = 0
    $generatedCount = 0
    if (-not [int]::TryParse($expectedRounds, [ref]$expectedCount) -or $expectedCount -le 0 -or $expectedCount -gt 20 -or
        -not [int]::TryParse([string]$generated['COUNCIL_DEBATE_MAX_ROUNDS'], [ref]$generatedCount) -or
        $generatedCount -ne $expectedCount) {
        throw 'The generated council .env does not match the selected debate-round setting. Run the council postprovision hook for the selected environment.'
    }
    return [pscustomobject]@{
        GroundingProvider = $provider
        DebateMaxRounds = $generatedCount
    }
}

function Wait-DemoReadyCouncilInitialization {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$LogDirectory,
        [Parameter(Mandatory)][ValidateSet('foundryiq', 'webiq')][string]$GroundingProvider,
        [ValidateRange(1, 600)][int]$TimeoutSeconds = 30
    )

    # The unchanged council catches startup failures and still serves HTTP.
    # Require its success messages from the freshly truncated supervisor logs.
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $text = (@('stdout', 'stderr') | ForEach-Object {
            $path = Join-Path $LogDirectory "cross-government-coordinate.$_.log"
            if (Test-Path -LiteralPath $path -PathType Leaf) {
                Get-Content -LiteralPath $path -Raw
            }
        }) -join "`n"
        foreach ($failure in @(
            'Foundry IQ knowledge base provisioning failed',
            'Agent provisioning failed.',
            'agents provisioned WITHOUT a grounding tool',
            'KB created without a chat model'
        )) {
            if ($text.Contains($failure, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Council initialization failed. Review the current masked council logs; HTTP readiness alone is insufficient.'
            }
        }
        $knowledgeReady = $GroundingProvider -ceq 'webiq' -or
            $text -match 'Knowledge base [^\r\n]+ provisioned \(model: [^\r\n]+\)'
        if ($text.Contains('Council agents ready (', [StringComparison]::Ordinal) -and $knowledgeReady) {
            return
        }
        Start-Sleep -Milliseconds 200
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw 'Council initialization has no current success evidence. Confirm agent and knowledge-base startup in the masked logs.'
}

function Assert-DemoReadyBicep {
    # Compiles every retained Bicep template and parameter file.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][hashtable]$ParameterEnvironment,
        [string[]]$SensitiveValues = @()
    )

    $infraRoots = @(
        (Join-Path $RepositoryRoot 'infra'),
        (Join-Path $RepositoryRoot 'src\PublicSectorAgentDemos.Demo2.Act\infra'),
        (Join-Path $RepositoryRoot 'src\PublicSectorAgentDemos.Demo3.Coordinate\infra')
    )
    $outputDirectory = Join-Path $RepositoryRoot '.demo-ready\validation-bicep'
    $null = New-Item -ItemType Directory -Path $outputDirectory -Force
    foreach ($file in Get-ChildItem -LiteralPath $infraRoots -Recurse -File |
        Where-Object Extension -in @('.bicep', '.bicepparam')) {
        $operation = $file.Extension -ceq '.bicepparam' ? 'build-params' : 'build'
        $outputPath = Join-Path $outputDirectory "$([Guid]::NewGuid().ToString('N')).json"
        try {
            # Azure CLI's Windows stdout encoding cannot represent every source description.
            Invoke-DemoReadyNative `
                -FilePath 'az' `
                -Arguments @('bicep', $operation, '--file', $file.FullName, '--outfile', $outputPath) `
                -WorkingDirectory $RepositoryRoot `
                -Environment $ParameterEnvironment `
                -SensitiveValues $SensitiveValues `
                -Quiet
        }
        finally {
            Remove-Item -LiteralPath $outputPath -Force -ErrorAction SilentlyContinue
        }
    }
}

function Publish-DemoReadyDemo1Agents {
    # Runs the existing Demo 1 publisher with the provisioned azd values.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][hashtable]$Demo1Values,
        [Parameter(Mandatory)][string]$CitationIdentityPath
    )

    $savedEnvironment = @{}
    try {
        foreach ($entry in $Demo1Values.GetEnumerator()) {
            $savedEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
            [Environment]::SetEnvironmentVariable($entry.Key, [string]$entry.Value, 'Process')
        }
        $savedEnvironment.RUN_DEMO1_CLOUD_INTEGRATION =
            [Environment]::GetEnvironmentVariable('RUN_DEMO1_CLOUD_INTEGRATION', 'Process')
        $env:RUN_DEMO1_CLOUD_INTEGRATION = 'true'
        & (Join-Path $RepositoryRoot 'scripts\ground\Publish-Demo1Agents.ps1') `
            -RepositoryRoot $RepositoryRoot `
            -CitationIdentityPath $CitationIdentityPath
        if ($LASTEXITCODE -ne 0) {
            throw 'The Demo 1 agent publisher failed.'
        }
    }
    finally {
        foreach ($entry in $savedEnvironment.GetEnumerator()) {
            [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
        }
    }
}

function Test-DemoReadyTransientHostedAgentLookupFailure {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Message
    )

    foreach ($marker in @(
        'agent name could not be resolved from azd environment',
        'The hosted agent did not expose a valid instance identity.'
    )) {
        if ($Message.Contains($marker, [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

function Get-DemoReadyHostedAgent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$EnvironmentName,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [string[]]$SensitiveValues = @(),
        [ValidateRange(1, 12)][int]$MaximumAttempts = 6,
        [ValidateRange(0, 300)][int]$RetryDelaySeconds = 10
    )

    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        try {
            $agentJson = Invoke-DemoReadyAzd `
                -Arguments @(
                    'ai', 'agent', 'show', $Name,
                    '--environment', $EnvironmentName,
                    '--output', 'json'
                ) `
                -WorkingDirectory $WorkingDirectory `
                -SensitiveValues $SensitiveValues `
                -CaptureOutput `
                -PreserveCapturedOutput `
                -Quiet
            $agent = $agentJson | ConvertFrom-Json -Depth 30
            if ([string]$agent.instance_identity.principal_id -notmatch '^[0-9a-fA-F-]{36}$') {
                throw 'The hosted agent did not expose a valid instance identity.'
            }
            return $agent
        }
        catch {
            if ($attempt -eq $MaximumAttempts -or
                -not (Test-DemoReadyTransientHostedAgentLookupFailure -Message $_.Exception.Message)) {
                throw
            }

            Write-Warning (
                "The hosted-agent metadata is not ready. " +
                "Retrying in $RetryDelaySeconds seconds " +
                "(attempt $($attempt + 1) of $MaximumAttempts)."
            )
            Start-Sleep -Seconds $RetryDelaySeconds
        }
    }
}

function Get-DemoReadyApplicationInsightsConnectionString {
    # Reads the connection string that the local Presenter needs to export its own telemetry.
    # It is application configuration, not validation. It runs one Azure CLI command.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ResourceId,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [string[]]$SensitiveValues = @()
    )

    if ($ResourceId -notmatch '^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft\.Insights/components/[^/]+$') {
        throw 'The azd environment does not publish a valid Application Insights resource ID.'
    }
    $connectionString = Invoke-DemoReadyNative `
        -FilePath 'az' `
        -Arguments @(
            'monitor', 'app-insights', 'component', 'show',
            '--ids', $ResourceId,
            '--query', 'connectionString',
            '--output', 'tsv',
            '--only-show-errors'
        ) `
        -WorkingDirectory $RepositoryRoot `
        -SensitiveValues $SensitiveValues `
        -CaptureOutput `
        -PreserveCapturedOutput `
        -Quiet
    $connectionString = $connectionString.Trim()
    if (-not $connectionString.Contains('InstrumentationKey=', [StringComparison]::OrdinalIgnoreCase) -or
        -not $connectionString.Contains('IngestionEndpoint=https://', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Application Insights did not return a valid connection string.'
    }
    return $connectionString
}
