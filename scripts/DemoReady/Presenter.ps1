# Presenter catalog generation for the demo-ready orchestrator.
# Dot-source this file after Common.ps1. It defines functions only.
#
# Integration seam: New-DemoReadyPresenterContext is the single data object that
# carries every deployed and external value into the generated catalog. Later
# Presenter metadata work changes New-DemoReadyCatalog only.

function ConvertTo-DemoReadyVsCodeFileUri {
    # Builds a portable vscode://file/<absolute-drive-path> URI from a resolved Windows path.
    # Backslashes become slashes, the drive colon is preserved, and each path segment is
    # percent-encoded independently so the result is a valid absolute URI.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$AbsolutePath
    )

    if ($AbsolutePath -notmatch '^(?<drive>[A-Za-z]):\\') {
        throw "The path must be an absolute Windows drive path: $AbsolutePath"
    }

    $drive = $Matches['drive']
    $rest = $AbsolutePath.Substring(3)
    $segments = $rest -split '\\' |
        Where-Object { $_ -ne '' } |
        ForEach-Object { [Uri]::EscapeDataString($_) }
    return "vscode://file/$drive`:/$($segments -join '/')"
}

function Get-DemoReadyHostedAgentVsCodeUri {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot
    )

    $sourcePath = Join-Path $RepositoryRoot 'src\PublicSectorAgentDemos.Demo4.HostedAgents'
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Container)) {
        throw "The hosted-agent source directory was not found: $sourcePath"
    }
    $resolved = (Resolve-Path -LiteralPath $sourcePath).Path
    return ConvertTo-DemoReadyVsCodeFileUri -AbsolutePath $resolved
}

function New-DemoReadyPresenterContext {
    [CmdletBinding()]
    param(
        [hashtable]$Demo1Values = @{},
        [hashtable]$Demo4Values = @{},
        [AllowNull()][pscustomobject]$HostedAgent,
        [AllowEmptyString()][string]$ActWebUrl = '',
        [AllowEmptyString()][string]$AssuranceDossierPath = '',
        [Parameter(Mandatory)][string]$AssuranceHttpUrl,
        [Parameter(Mandatory)][string]$AssuranceHttpsUrl,
        [Parameter(Mandatory)][string]$PatriotsHttpUrl,
        [Parameter(Mandatory)][string]$PatriotsHttpsUrl,
        [AllowEmptyString()][string]$PatriotsDossierPath = '',
        [bool]$PatriotsConfigured = $false,
        [ValidateSet('not-configured', 'ready', 'failed')][string]$TokensAndCreditsStatus = 'not-configured',
        [Collections.IDictionary]$Selection,
        [Parameter(Mandatory)][string]$RepositoryRoot
    )

    if ($null -eq $Selection) {
        $Selection = [ordered]@{
            Demo1 = $true
            Demo2 = $true
            Demo3 = $true
            Demo4 = $true
            Patriots = $true
            TokensAndCredits = $TokensAndCreditsStatus -ne 'not-configured'
        }
    }
    $actUri = $null
    if ($Selection.Demo2) {
        $actUri = [Uri]$ActWebUrl
        if ($actUri.Scheme -ne 'https') {
            throw 'The Act web URL must use HTTPS.'
        }
    }
    if ($Selection.Demo3 -and
        -not (Test-Path -LiteralPath $AssuranceDossierPath -PathType Leaf)) {
        throw "The Cross-Government dossier was not found: $AssuranceDossierPath"
    }
    if ($Selection.Patriots -and
        -not (Test-Path -LiteralPath $PatriotsDossierPath -PathType Leaf)) {
        throw "The Patriots dossier was not found: $PatriotsDossierPath"
    }
    $hostedAgentVsCodeUri = $Selection.Demo4 `
        ? (Get-DemoReadyHostedAgentVsCodeUri -RepositoryRoot $RepositoryRoot) `
        : ''

    return [pscustomobject]@{
        Selection = $Selection
        Demo1Values = $Demo1Values
        Demo4Values = $Demo4Values
        HostedAgent = $HostedAgent
        ActWebUrl = $null -eq $actUri ? '' : $actUri.AbsoluteUri.TrimEnd('/')
        AssuranceDossierPath = $Selection.Demo3 `
            ? (Resolve-Path -LiteralPath $AssuranceDossierPath).Path `
            : ''
        AssuranceHttpUrl = $AssuranceHttpUrl
        AssuranceHttpsUrl = $AssuranceHttpsUrl
        PatriotsHttpUrl = $PatriotsHttpUrl
        PatriotsHttpsUrl = $PatriotsHttpsUrl
        PatriotsDossierPath = $Selection.Patriots `
            ? (Resolve-Path -LiteralPath $PatriotsDossierPath).Path `
            : ''
        PatriotsConfigured = $Selection.Patriots -and $PatriotsConfigured
        TokensAndCreditsStatus = $TokensAndCreditsStatus
        HostedAgentVsCodeUri = $hostedAgentVsCodeUri
    }
}

function Disable-DemoReadyPresenterSession {
    [CmdletBinding()]
    param([Parameter(Mandatory)][pscustomobject]$Session)

    $Session | Add-Member -NotePropertyName configured -NotePropertyValue $false -Force
    $Session.launchUrl = ''
    $Session.warmUp.kind = 'disabled'
    $Session.warmUp.endpoint = ''
    if ($null -ne $Session.warmUp.PSObject.Properties['modelDeployment']) {
        $Session.warmUp.modelDeployment = $null
    }
    if ($null -ne $Session.PSObject.Properties['links']) {
        $Session.links = @()
    }
}

function Enable-DemoReadyPresenterSession {
    [CmdletBinding()]
    param([Parameter(Mandatory)][pscustomobject]$Session)

    $Session | Add-Member -NotePropertyName configured -NotePropertyValue $true -Force
}

function New-DemoReadyPresenterProject {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$RuntimeRoot,
        [Parameter(Mandatory)][Collections.IDictionary]$Selection
    )

    $committedProject = Join-Path `
        $RepositoryRoot `
        'src\PublicSectorAgentDemos.Presenter\PublicSectorAgentDemos.Presenter.csproj'
    if ($Selection.Demo1 -and $Selection.Demo2 -and $Selection.Demo3 -and $Selection.Demo4) {
        return $committedProject
    }

    $sourceRoot = Join-Path $RepositoryRoot 'src\PublicSectorAgentDemos.Presenter'
    $generatedRoot = Join-Path $RuntimeRoot 'presenter-guided'
    $null = New-Item -ItemType Directory -Path $generatedRoot -Force
    $generatedWebRoot = Join-Path $generatedRoot 'wwwroot'
    if (Test-Path -LiteralPath $generatedWebRoot) {
        Remove-Item -LiteralPath $generatedWebRoot -Recurse -Force
    }
    Copy-Item `
        -LiteralPath (Join-Path $sourceRoot 'wwwroot') `
        -Destination $generatedWebRoot `
        -Recurse

    $catalogSourcePath = Join-Path $sourceRoot 'PresenterSessionCatalog.cs'
    $catalogSource = Get-Content -LiteralPath $catalogSourcePath -Raw
    $originalConfiguredValidation = @'
            (!Configured && (Id is not ("patriots-coordinate" or "tokens-and-credits") || !Optional ||
                !string.IsNullOrEmpty(LaunchUrl) || WarmUp.Kind != "disabled")) ||
'@
    $guidedConfiguredValidation = @'
            (!Configured && (!string.IsNullOrEmpty(LaunchUrl) || WarmUp.Kind != "disabled")) ||
'@
    $originalWarmUpValidation = @'
        if (Kind == "disabled" && sessionId is "patriots-coordinate" or "tokens-and-credits" &&
            string.IsNullOrEmpty(Endpoint) && string.IsNullOrEmpty(ModelDeployment))
'@
    $guidedWarmUpValidation = @'
        if (Kind == "disabled" &&
            string.IsNullOrEmpty(Endpoint) && string.IsNullOrEmpty(ModelDeployment))
'@
    foreach ($required in @($originalConfiguredValidation, $originalWarmUpValidation)) {
        if (-not $catalogSource.Contains($required, [StringComparison]::Ordinal)) {
            throw 'The Presenter validation source does not match the guided build contract.'
        }
    }
    $catalogSource = $catalogSource.
        Replace($originalConfiguredValidation, $guidedConfiguredValidation).
        Replace($originalWarmUpValidation, $guidedWarmUpValidation)
    $generatedCatalogSource = Join-Path $generatedRoot 'PresenterSessionCatalog.cs'
    $catalogSource | Set-Content -LiteralPath $generatedCatalogSource -Encoding utf8NoBOM

    $pageSourcePath = Join-Path $sourceRoot 'PresenterPage.cs'
    $pageSource = Get-Content -LiteralPath $pageSourcePath -Raw
    $originalMessage = '            return "Not configured. This optional application is managed outside this repository.";'
    $guidedMessage = '            return "Not selected for this run.";'
    if (-not $pageSource.Contains($originalMessage, [StringComparison]::Ordinal)) {
        throw 'The Presenter page source does not match the guided build contract.'
    }
    $generatedPageSource = Join-Path $generatedRoot 'PresenterPage.cs'
    $pageSource.Replace($originalMessage, $guidedMessage) |
        Set-Content -LiteralPath $generatedPageSource -Encoding utf8NoBOM

    $xml = [Security.SecurityElement]
    $projectPath = Join-Path $generatedRoot 'PublicSectorAgentDemos.Presenter.Guided.csproj'
    $projectSource = @"
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <EnableDefaultContentItems>false</EnableDefaultContentItems>
    <AssemblyName>PublicSectorAgentDemos.Presenter</AssemblyName>
    <RootNamespace>PublicSectorAgentDemos.Presenter</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$($xml::Escape((Join-Path $sourceRoot 'Program.cs')))" />
    <Compile Include="$($xml::Escape((Join-Path $sourceRoot 'PresenterWarmUpCoordinator.cs')))" />
    <Compile Include="$($xml::Escape((Join-Path $sourceRoot 'PresenterWarmUpService.cs')))" />
    <Compile Include="$($xml::Escape($generatedCatalogSource))" />
    <Compile Include="$($xml::Escape($generatedPageSource))" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="$($xml::Escape((Join-Path $RepositoryRoot 'src\Shared\PublicSectorAgentDemos.Identity\PublicSectorAgentDemos.Identity.csproj')))" />
    <ProjectReference Include="$($xml::Escape((Join-Path $RepositoryRoot 'src\Shared\PublicSectorAgentDemos.Observability\PublicSectorAgentDemos.Observability.csproj')))" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="$($xml::Escape((Join-Path $generatedWebRoot '**\*')))"
             Link="wwwroot\%(RecursiveDir)%(Filename)%(Extension)"
             CopyToOutputDirectory="PreserveNewest"
             CopyToPublishDirectory="PreserveNewest" />
    <Content Include="$($xml::Escape((Join-Path $RepositoryRoot 'config\presenter\sessions.v1.json')))"
             Link="config\presenter\sessions.v1.json"
             CopyToOutputDirectory="PreserveNewest"
             CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
"@
    $projectSource | Set-Content -LiteralPath $projectPath -Encoding utf8NoBOM
    return $projectPath
}

function Get-DemoReadyDemo1AgentBaseUrl {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Demo1ProjectId,
        [Parameter(Mandatory)][pscustomobject]$HostedAgent
    )

    $projectMatch = [regex]::Match(
        $Demo1ProjectId,
        '^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/(?<resourceGroup>[^/]+)/providers/Microsoft\.CognitiveServices/accounts/(?<account>[^/]+)/projects/(?<project>[^/]+)$',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if (-not $projectMatch.Success) {
        throw 'The Demo 1 Foundry project resource ID is invalid.'
    }
    $playground = [Uri][string]$HostedAgent.playground_url
    $portalMatch = [regex]::Match(
        $playground.AbsolutePath,
        '^/nextgen/r/(?<workspace>[^,/]+),',
        [Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($playground.Scheme -ne 'https' -or
        $playground.Host -cne 'ai.azure.com' -or
        -not $portalMatch.Success) {
        throw 'The Demo 4 hosted agent playground URL is invalid.'
    }
    return 'https://ai.azure.com/nextgen/r/{0},{1},,{2},{3}/build/agents' -f
        $portalMatch.Groups['workspace'].Value,
        $projectMatch.Groups['resourceGroup'].Value,
        $projectMatch.Groups['account'].Value,
        $projectMatch.Groups['project'].Value
}

function New-DemoReadyCatalog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourcePath,
        [Parameter(Mandatory)][string]$DestinationPath,
        [Parameter(Mandatory)][pscustomobject]$Context
    )

    $catalog = Get-Content -LiteralPath $SourcePath -Raw | ConvertFrom-Json
    if (@($catalog.sessions).Count -ne 6) {
        throw 'The committed Presenter catalog must contain exactly six sessions.'
    }

    $foundryResponses = ''
    $demo1AgentBaseUrl = ''
    if ($Context.Selection.Demo1) {
        $demo1Service = [Uri]$Context.Demo1Values.AZURE_AI_SERVICES_ENDPOINT
        if ($demo1Service.Scheme -ne 'https' -or
            $demo1Service.Port -ne 443 -or
            -not $demo1Service.Host.EndsWith(
                '.services.ai.azure.com',
                [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The Demo 1 service endpoint is invalid.'
        }
        $foundryResponses = [Uri]::new($demo1Service, '/openai/v1/responses').AbsoluteUri
        if ($null -ne $Context.HostedAgent) {
            $demo1AgentBaseUrl = Get-DemoReadyDemo1AgentBaseUrl `
                -Demo1ProjectId ([string]$Context.Demo1Values.AZURE_AI_PROJECT_ID) `
                -HostedAgent $Context.HostedAgent
        }
    }

    $hostedEndpoint = ''
    if ($Context.Selection.Demo4) {
        $hostedApplicationName = [string]$Context.Demo4Values.DEMO4_APPLICATION_NAME
        if ($hostedApplicationName -notmatch '^[a-z0-9][a-z0-9-]{0,58}[a-z0-9]$') {
            throw 'The Demo 4 application name is invalid.'
        }
        $hostedEndpoint = "https://$hostedApplicationName.azurewebsites.net"
    }
    $assuranceDossierInput = if ($Context.Selection.Demo3) {
        $dossierDirectory = Split-Path -Parent $Context.AssuranceDossierPath
        "Upload $($Context.AssuranceDossierPath). " +
        "Alternative Cross-Government dossiers are in $dossierDirectory."
    }
    else {
        ''
    }
    $patriotsDossierInput = $Context.Selection.Patriots `
        ? "Upload $($Context.PatriotsDossierPath)" `
        : ''

    foreach ($session in $catalog.sessions) {
        switch ([string]$session.id) {
            'foundation' {
                if (-not $Context.Selection.Demo1) {
                    Disable-DemoReadyPresenterSession -Session $session
                    continue
                }
                Enable-DemoReadyPresenterSession -Session $session
                $session.launchUrl = 'http://localhost:5090/'
                $session.links[0].url = [string]::IsNullOrWhiteSpace($demo1AgentBaseUrl) `
                    ? 'https://ai.azure.com' `
                    : "$demo1AgentBaseUrl/managing-public-money-foundation/build"
                $session.warmUp.endpoint = $foundryResponses
                $session.warmUp.modelDeployment = [string]$Context.Demo1Values.COUNCIL_FAST_MODEL
            }
            'ground' {
                if (-not $Context.Selection.Demo1) {
                    Disable-DemoReadyPresenterSession -Session $session
                    continue
                }
                Enable-DemoReadyPresenterSession -Session $session
                $session.launchUrl = 'http://localhost:5090/'
                $session.links[0].url = [string]::IsNullOrWhiteSpace($demo1AgentBaseUrl) `
                    ? 'https://ai.azure.com' `
                    : "$demo1AgentBaseUrl/managing-public-money-ground/build"
                $session.warmUp.endpoint = $foundryResponses
                $session.warmUp.modelDeployment = [string]$Context.Demo1Values.COUNCIL_FAST_MODEL
            }
            'act' {
                if (-not $Context.Selection.Demo2) {
                    Disable-DemoReadyPresenterSession -Session $session
                    continue
                }
                Enable-DemoReadyPresenterSession -Session $session
                $session.launchUrl = "$($Context.ActWebUrl)/"
                $session.warmUp.endpoint = "$($Context.ActWebUrl)/health"
            }
            'cross-government-coordinate' {
                if (-not $Context.Selection.Demo3) {
                    Disable-DemoReadyPresenterSession -Session $session
                    continue
                }
                Enable-DemoReadyPresenterSession -Session $session
                $session.launchUrl = "$($Context.AssuranceHttpUrl)dossiers"
                $session.warmUp.endpoint = $Context.AssuranceHttpUrl
                $session.input = $assuranceDossierInput
            }
            'patriots-coordinate' {
                $session.optional = $true
                $session.configured = $Context.PatriotsConfigured
                $session.launchUrl = $Context.PatriotsConfigured ? $Context.PatriotsHttpsUrl : ''
                $session.warmUp.kind = $Context.PatriotsConfigured ? 'local-app' : 'disabled'
                $session.warmUp.endpoint = $Context.PatriotsConfigured ? $Context.PatriotsHttpUrl : ''
                if ($Context.Selection.Patriots) {
                    $session.input = $patriotsDossierInput
                }
            }
            'hosted' {
                if (-not $Context.Selection.Demo4) {
                    Disable-DemoReadyPresenterSession -Session $session
                    continue
                }
                Enable-DemoReadyPresenterSession -Session $session
                $session.launchUrl = "$hostedEndpoint/"
                $session.warmUp.endpoint = "$hostedEndpoint/health"
                $playgroundUri = [Uri][string]$Context.HostedAgent.playground_url
                if ($playgroundUri.Scheme -ne 'https' -or $playgroundUri.Host -cne 'ai.azure.com') {
                    throw 'The Demo 4 hosted agent playground URL is invalid.'
                }
                foreach ($link in $session.links) {
                    switch ([string]$link.kind) {
                        'playground' { $link.url = $playgroundUri.AbsoluteUri }
                        'vscode' { $link.url = $Context.HostedAgentVsCodeUri }
                    }
                }
            }
            default {
                throw "The Presenter catalog contains an unknown session '$($session.id)'."
            }
        }
    }

    if (@($catalog.extras).Count -ne 1 -or [string]$catalog.extras[0].id -cne 'tokens-and-credits') {
        throw 'The Presenter extras must contain only the optional Tokens and Credits tile.'
    }
    $tokens = $catalog.extras[0]
    $tokens.startupStatus = $Context.TokensAndCreditsStatus
    $tokens.configured = $Context.TokensAndCreditsStatus -ceq 'ready'
    $tokens.launchUrl = $tokens.configured ? 'http://localhost:5041/' : ''
    $tokens.warmUp.kind = $tokens.configured ? 'tokens-local' : 'disabled'
    $tokens.warmUp.endpoint = $tokens.configured ? 'http://localhost:5041/api/embeddings/manifest' : ''

    $parent = Split-Path -Parent $DestinationPath
    $null = New-Item -ItemType Directory -Path $parent -Force
    $catalog | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $DestinationPath -Encoding utf8NoBOM
    return $catalog
}
