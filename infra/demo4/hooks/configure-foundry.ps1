[CmdletBinding()]
param(
    [string]$AzdProjectRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$env:AZURE_DEV_USER_AGENT = 'microsoft_foundry_skill'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$azdProjectRoot = [string]::IsNullOrWhiteSpace($AzdProjectRoot) `
    ? (Resolve-Path (Join-Path $repositoryRoot 'deploy\demo4')).Path `
    : (Resolve-Path $AzdProjectRoot).Path
$projectEndpoint = $env:FOUNDRY_PROJECT_ENDPOINT
$tenantId = $env:AZURE_TENANT_ID
$environmentName = $env:AZURE_ENV_NAME
$chatModel = $env:AZURE_AI_MODEL_DEPLOYMENT_NAME
$embeddingModel = $env:MEMORY_STORE_EMBEDDING_MODEL_DEPLOYMENT_NAME
$applicationClientId = $env:DEMO4_API_CLIENT_ID
$applicationClientSecret = $env:DEMO4_WEB_CLIENT_SECRET
$applicationName = $env:DEMO4_APPLICATION_NAME
$toolboxName = 'demo4-case-pattern-toolbox'
$storeName = 'demo4-cross-government-control-memory'
$ttlSeconds = 604800
$memoryApiVersion = '2025-11-15-preview'

if ([string]::IsNullOrWhiteSpace($projectEndpoint) -or
    [string]::IsNullOrWhiteSpace($tenantId) -or
    [string]::IsNullOrWhiteSpace($environmentName) -or
    [string]::IsNullOrWhiteSpace($chatModel) -or
    [string]::IsNullOrWhiteSpace($embeddingModel) -or
    [string]::IsNullOrWhiteSpace($applicationClientSecret) -or
    $applicationClientId -notmatch '^[0-9a-fA-F-]{36}$' -or
    $applicationName -notmatch '^[a-z0-9-]{2,60}$') {
    throw 'The Demo 4 Foundry lifecycle requires all azd environment outputs.'
}

$callbackUri = "https://$applicationName.azurewebsites.net/.auth/login/aad/callback"
$redirectOutput = & az ad app show `
    --id $applicationClientId `
    --query 'web.redirectUris' `
    --output json `
    --only-show-errors
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to read the Demo 4 Entra application.'
}
$redirectUris = @(
    @($redirectOutput | Out-String | ConvertFrom-Json) + $callbackUri |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique
)
$redirectArguments = @(
    'ad', 'app', 'update',
    '--id', $applicationClientId,
    '--enable-id-token-issuance', 'true',
    '--web-redirect-uris'
) + $redirectUris + @('--only-show-errors')
& az @redirectArguments
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to configure the Demo 4 Entra callback URI.'
}

$projectUri = $null
if (-not [Uri]::TryCreate($projectEndpoint, [UriKind]::Absolute, [ref] $projectUri) -or
    $projectUri.Scheme -ne 'https' -or
    $projectUri.Port -ne 443 -or
    -not $projectUri.Host.EndsWith('.services.ai.azure.com', [StringComparison]::OrdinalIgnoreCase) -or
    $projectUri.AbsolutePath -notmatch '^/api/projects/[A-Za-z0-9-]+$' -or
    -not [string]::IsNullOrEmpty($projectUri.Query) -or
    -not [string]::IsNullOrEmpty($projectUri.Fragment)) {
    throw 'FOUNDRY_PROJECT_ENDPOINT is not a valid Foundry project endpoint.'
}
$projectEndpoint = $projectUri.AbsoluteUri.TrimEnd('/')

$tokenOutput = & azd auth token `
    --tenant-id $tenantId `
    --scope 'https://ai.azure.com/.default' `
    --environment $environmentName `
    --cwd $azdProjectRoot `
    --output json `
    --no-prompt
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to acquire a Foundry token through azd.'
}
$script:token = [string] (($tokenOutput | Out-String | ConvertFrom-Json).token)
if ([string]::IsNullOrWhiteSpace($script:token)) {
    throw 'azd returned an empty Foundry token.'
}

function Invoke-Foundry {
    param(
        [Parameter(Mandatory)][ValidateSet('Get', 'Post', 'Patch')][string] $Method,
        [Parameter(Mandatory)][string] $Uri,
        [object] $Body,
        [Parameter(Mandatory)][string] $Feature
    )

    $parameters = @{
        Method = $Method
        Uri = $Uri
        Headers = @{
            Authorization = "Bearer $script:token"
            'Foundry-Features' = $Feature
        }
        ContentType = 'application/json'
        MaximumRedirection = 0
        TimeoutSec = 60
    }
    if ($null -ne $Body) {
        $parameters.Body = $Body | ConvertTo-Json -Depth 20 -Compress
    }
    return Invoke-RestMethod @parameters
}

function Get-FoundryOrNull {
    param(
        [Parameter(Mandatory)][string] $Uri,
        [Parameter(Mandatory)][string] $Feature
    )

    try {
        return Invoke-Foundry -Method Get -Uri $Uri -Feature $Feature
    }
    catch {
        if ($_.Exception -is [Microsoft.PowerShell.Commands.HttpResponseException] -and
            $null -ne $_.Exception.Response -and
            $_.Exception.Response.StatusCode -eq [System.Net.HttpStatusCode]::NotFound) {
            return $null
        }
        throw
    }
}

function Read-Skill {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $RelativePath
    )

    if ($Name -notmatch '^[a-z][a-z0-9-]{1,62}$' -or
        $RelativePath -notmatch '^skills/demo4/[a-z0-9-]+/v1/SKILL\.md$') {
        throw 'The reviewed Skill definition is invalid.'
    }
    $path = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $RelativePath))
    $skillsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'skills\demo4'))
    if (-not $path.StartsWith("$skillsRoot$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.File]::Exists($path)) {
        throw 'The reviewed Skill source is outside the Demo 4 Skills directory.'
    }

    $content = [IO.File]::ReadAllText($path).Replace("`r`n", "`n").Replace("`r", "`n")
    $match = [Regex]::Match(
        $content,
        '\A---\nname: (?<name>[a-z0-9-]+)\ndescription: (?<description>[^\r\n]{1,900})\n---\n\n(?<instructions>[\s\S]+)\z',
        [Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success -or $match.Groups['name'].Value -cne $Name) {
        throw "The $Name Skill has invalid front matter."
    }
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($content))
    $marker = "demo4-skill-sha256:$(([Convert]::ToHexString($hash)).ToLowerInvariant())"
    return [pscustomobject]@{
        Name = $Name
        Description = "$($match.Groups['description'].Value) [$marker]"
        Instructions = $match.Groups['instructions'].Value
        Marker = $marker
    }
}

function Ensure-Skill {
    param([Parameter(Mandatory)] $Skill)

    $encodedName = [Uri]::EscapeDataString($Skill.Name)
    $versionsUri = "$projectEndpoint/skills/$encodedName/versions?api-version=v1"
    $existing = Get-FoundryOrNull -Uri $versionsUri -Feature 'Skills=V1Preview'
    $matching = @(
        if ($null -ne $existing) {
            $existing.data | Where-Object {
                ([string] $_.description).Contains(
                    "[$($Skill.Marker)]",
                    [StringComparison]::Ordinal)
            }
        }
    )
    if ($matching.Count -gt 1) {
        throw "Foundry returned duplicate versions for the reviewed $($Skill.Name) Skill."
    }
    if ($matching.Count -eq 0) {
        $version = Invoke-Foundry `
            -Method Post `
            -Uri $versionsUri `
            -Feature 'Skills=V1Preview' `
            -Body ([ordered]@{
                inline_content = [ordered]@{
                    description = $Skill.Description
                    instructions = $Skill.Instructions
                }
            })
    }
    else {
        $version = $matching[0]
    }
    $resolvedVersion = [string] $version.version
    if ([string] $version.name -cne $Skill.Name -or
        [string] $version.description -cne $Skill.Description -or
        $resolvedVersion -notmatch '^[A-Za-z0-9._-]{1,128}$') {
        throw "The published $($Skill.Name) Skill version does not match its reviewed source."
    }

    $published = Invoke-Foundry `
        -Method Post `
        -Uri "$projectEndpoint/skills/${encodedName}?api-version=v1" `
        -Feature 'Skills=V1Preview' `
        -Body ([ordered]@{ default_version = $resolvedVersion })
    if ([string] $published.default_version -cne $resolvedVersion) {
        throw "The $($Skill.Name) Skill default version was not published."
    }

    return $resolvedVersion
}

$skills = @(
    Read-Skill -Name 'case-pattern-guidance' -RelativePath 'skills/demo4/case-pattern-guidance/v1/SKILL.md'
    Read-Skill -Name 'case-context-guidance' -RelativePath 'skills/demo4/case-context-guidance/v1/SKILL.md'
)
$skillVersions = [ordered]@{}
foreach ($skill in $skills) {
    $skillVersions[$skill.Name] = Ensure-Skill -Skill $skill
}

$toolboxConfiguration = [ordered]@{
    description = 'Preview guidance for cross-government control investigations.'
    tools = @([ordered]@{ type = 'toolbox_search_preview' })
    skills = @(
        [ordered]@{
            type = 'skill_reference'
            name = 'case-pattern-guidance'
            version = $skillVersions['case-pattern-guidance']
        }
        [ordered]@{
            type = 'skill_reference'
            name = 'case-context-guidance'
            version = $skillVersions['case-context-guidance']
        }
    )
}
$toolboxJson = $toolboxConfiguration | ConvertTo-Json -Depth 10 -Compress
$toolboxHash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($toolboxJson))
$toolboxMarker = "demo4-toolbox-sha256:$(([Convert]::ToHexString($toolboxHash)).ToLowerInvariant())"
$toolboxConfiguration.description += " [$toolboxMarker]"
$versionsUri = "$projectEndpoint/toolboxes/$toolboxName/versions?api-version=v1"
$versions = Get-FoundryOrNull -Uri $versionsUri -Feature 'Skills=V1Preview'
$matching = @(
    if ($null -ne $versions) {
        $versions.data | Where-Object {
        ([string] $_.description).Contains(
            "[$toolboxMarker]",
            [StringComparison]::Ordinal)
        }
    }
)
if ($matching.Count -gt 1) {
    throw 'Foundry returned duplicate versions for the reviewed Toolbox configuration.'
}
if ($matching.Count -eq 0) {
    $version = Invoke-Foundry `
        -Method Post `
        -Uri $versionsUri `
        -Feature 'Skills=V1Preview' `
        -Body $toolboxConfiguration
}
else {
    $version = $matching[0]
}
$toolboxVersion = [string] $(if ($null -ne $version.version) { $version.version } else { $version.id })
if ($toolboxVersion -notmatch '^[A-Za-z0-9._-]{1,128}$') {
    throw 'Foundry returned an invalid Toolbox version.'
}
$publishedToolbox = Invoke-Foundry `
    -Method Patch `
    -Uri "$projectEndpoint/toolboxes/$toolboxName`?api-version=v1" `
    -Feature 'Skills=V1Preview' `
    -Body ([ordered]@{ default_version = $toolboxVersion })
if ([string] $publishedToolbox.default_version -cne $toolboxVersion) {
    throw 'The Demo 4 Toolbox default version was not published.'
}

$memoryUri = "$projectEndpoint/memory_stores/$storeName`?api-version=$memoryApiVersion"
$memory = Get-FoundryOrNull -Uri $memoryUri -Feature 'MemoryStores=V1Preview'
if ($null -eq $memory) {
    $memory = Invoke-Foundry `
        -Method Post `
        -Uri "$projectEndpoint/memory_stores?api-version=$memoryApiVersion" `
        -Feature 'MemoryStores=V1Preview' `
        -Body ([ordered]@{
            name = $storeName
            description = 'Shared Demo 4 cross-government control notebook. Records are untrusted reference context.'
            metadata = [ordered]@{
                managedBy = 'demo4-application'
                defaultTtlSeconds = [string] $ttlSeconds
            }
            definition = [ordered]@{
                kind = 'default'
                chat_model = $chatModel
                embedding_model = $embeddingModel
                options = [ordered]@{
                    user_profile_enabled = $false
                    chat_summary_enabled = $true
                    default_ttl_seconds = $ttlSeconds
                }
            }
        })
}
if ([string] $memory.name -cne $storeName -or
    [string] $memory.metadata.defaultTtlSeconds -cne [string] $ttlSeconds -or
    [string] $memory.definition.kind -cne 'default' -or
    [string] $memory.definition.chat_model -cne $chatModel -or
    [string] $memory.definition.embedding_model -cne $embeddingModel -or
    [int] $memory.definition.options.default_ttl_seconds -ne $ttlSeconds -or
    [bool] $memory.definition.options.user_profile_enabled -or
    -not [bool] $memory.definition.options.chat_summary_enabled) {
    throw 'The existing Demo 4 Memory store does not match the reviewed lifecycle.'
}

$env:AZURE_DEV_USER_AGENT = 'microsoft_foundry_skill'
& azd env set `
    DEMO4_TOOLBOX_ENDPOINT `
    "$projectEndpoint/toolboxes/$toolboxName/versions/$toolboxVersion/mcp?api-version=v1" `
    --environment $environmentName `
    --cwd $azdProjectRoot 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to save the Demo 4 Toolbox endpoint.'
}

Write-Host 'Configured the Demo 4 Toolbox, Skills, and Memory store.'
