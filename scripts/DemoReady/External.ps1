# Bundled and external repository resolution, validation, and Git preservation.
# Dot-source this file after Common.ps1. It defines functions only.

function Get-DemoReadyExternalRepositoryDefinition {
    # Legacy identity keys remain readable. Owned paths cannot point outside this checkout.
    # The identity keys are also the setup-file repository keys.
    [CmdletBinding()]
    param()

    [ordered]@{
        defra = [pscustomobject]@{
            Identity = 'defra'
            DisplayName = 'Act (bundled Demo 2)'
            BundledPath = 'src\PublicSectorAgentDemos.Demo2.Act'
            FolderName = 'DEFRA-AI-Demos'
            ParameterName = 'DefraRepoPath'
            EnvironmentVariable = 'PSAD_DEFRA_REPO_PATH'
            RequiredPaths = @(
                'azure.yaml',
                'src\Demo2.Web\Demo2.Web.csproj'
            )
        }
        assuranceBoard = [pscustomobject]@{
            Identity = 'assuranceBoard'
            DisplayName = 'Cross-Government Coordinate (bundled council)'
            BundledPath = 'src\PublicSectorAgentDemos.Demo3.Coordinate'
            FolderName = 'cross-gov-assurance-board-demo'
            ParameterName = 'AssuranceBoardRepoPath'
            EnvironmentVariable = 'PSAD_ASSURANCE_BOARD_REPO_PATH'
            RequiredPaths = @(
                'azure.yaml',
                'src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj',
                'data\policies\dossier-shared-ai-service.md'
            )
        }
        patriots = [pscustomobject]@{
            Identity = 'patriots'
            DisplayName = 'Patriots council (azure-ai-mgs-patriots)'
            FolderName = 'azure-ai-mgs-patriots'
            RepositoryUrl = 'https://github.com/garylumsden/azure-ai-mgs-patriots.git'
            ParameterName = 'PatriotsRepoPath'
            EnvironmentVariable = 'PSAD_PATRIOTS_REPO_PATH'
            RequiredPaths = @(
                'azure.yaml',
                'src\GovernanceCouncil.Web\GovernanceCouncil.Web.csproj'
            )
        }
        tokensAndCredits = [pscustomobject]@{
            Identity = 'tokensAndCredits'
            DisplayName = 'Tokens and Credits'
            FolderName = 'tokens-and-credits'
            RepositoryUrl = 'https://github.com/garylumsden/tokens-and-credits.git'
            ParameterName = 'TokensAndCreditsRepoPath'
            EnvironmentVariable = 'PSAD_TOKENS_AND_CREDITS_REPO_PATH'
            RequiredPaths = @(
                'azure.yaml',
                'src\TokensAndCredits.Web\TokensAndCredits.Web.csproj',
                'src\TokensAndCredits.Web\Program.cs',
                'src\TokensAndCredits.Web\Api\TokenEndpoints.cs',
                'src\TokensAndCredits.Web\Resources\embeddings\glove-wiki-gigaword-50.top10000.txt.gz'
            )
        }
    }
}

function Test-DemoReadySetupFileSecret {
    # Rejects setup files that carry credential material.
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Content)

    foreach ($marker in @(
        'InstrumentationKey=',
        'AccountKey=',
        'SharedAccessKey',
        'client_secret',
        'clientSecret',
        'ClientSecret',
        'password',
        'Password',
        'apiKey',
        'ApiKey',
        'api-key',
        'Bearer ',
        'PRIVATE KEY'
    )) {
        if ($Content.Contains($marker, [StringComparison]::Ordinal)) {
            return $true
        }
    }
    return $false
}

function Read-DemoReadySetupFile {
    # Reads and validates the ignored .demo-ready\repositories.local.json setup file.
    # Returns an empty hashtable when the file is absent.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$SchemaPath
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @{}
    }

    $content = Get-Content -LiteralPath $Path -Raw
    if (Test-DemoReadySetupFileSecret -Content $content) {
        throw "The setup file '$Path' must be secret-free. Remove the credential material."
    }
    if (-not (Test-Json -Json $content -SchemaFile $SchemaPath -ErrorAction SilentlyContinue)) {
        throw "The setup file '$Path' does not match '$SchemaPath'."
    }

    $document = $content | ConvertFrom-Json -AsHashtable
    if ([int]$document['version'] -ne 1) {
        throw "The setup file '$Path' must declare version 1."
    }

    $known = @((Get-DemoReadyExternalRepositoryDefinition).Keys)
    $repositories = @{}
    foreach ($key in @($document['repositories'].Keys)) {
        if ($key -cnotin $known) {
            throw "The setup file '$Path' declares the unknown repository '$key'."
        }
        $entry = $document['repositories'][$key]
        if ([string]::IsNullOrWhiteSpace([string]$entry['path'])) {
            throw "The setup file '$Path' must give a path for '$key'."
        }
        $repositories[$key] = [pscustomobject]@{
            Path = [string]$entry['path']
            AzdEnvironmentName = $entry.ContainsKey('azdEnvironment') `
                ? [string]$entry['azdEnvironment'] `
                : ''
        }
    }
    return $repositories
}

function Assert-DemoReadyExternalRepositoryOrigin {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryPath,
        [Parameter(Mandatory)][string]$ExpectedRepositoryUrl,
        [Parameter(Mandatory)][string]$DisplayName
    )

    $origin = (& git -C $RepositoryPath remote get-url origin 2>$null).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($origin)) {
        throw "The $DisplayName checkout has no readable origin remote."
    }
    $normalize = {
        param([string]$Value)
        $trimmed = $Value.Trim().TrimEnd('/') -replace '\.git$', ''
        $match = [regex]::Match(
            $trimmed,
            '(?i)github\.com[:/](?<owner>[^/]+)/(?<repository>[^/]+)$')
        if (-not $match.Success) {
            return ''
        }
        return "$($match.Groups['owner'].Value)/$($match.Groups['repository'].Value)".ToLowerInvariant()
    }
    $actualIdentity = & $normalize $origin
    $expectedIdentity = & $normalize $ExpectedRepositoryUrl
    if ([string]::IsNullOrWhiteSpace($actualIdentity) -or
        $actualIdentity -cne $expectedIdentity) {
        throw "The $DisplayName checkout origin does not match the approved repository."
    }
}

function Assert-DemoReadyExternalRepositoryCheckout {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][pscustomobject]$Definition
    )

    $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedPath '.git'))) {
        throw "The $($Definition.DisplayName) path is not a Git repository: $resolvedPath"
    }
    if ($null -ne $Definition.PSObject.Properties['RepositoryUrl']) {
        Assert-DemoReadyExternalRepositoryOrigin `
            -RepositoryPath $resolvedPath `
            -ExpectedRepositoryUrl ([string]$Definition.RepositoryUrl) `
            -DisplayName ([string]$Definition.DisplayName)
    }
    foreach ($relativePath in $Definition.RequiredPaths) {
        if (-not (Test-Path -LiteralPath (Join-Path $resolvedPath $relativePath) -PathType Leaf)) {
            throw "The $($Definition.DisplayName) repository is missing '$relativePath'."
        }
    }
    return $resolvedPath
}

function Resolve-DemoReadyExternalRepository {
    # Resolves one external repository path. Precedence: parameter, environment
    # variable, setup file, then sibling folder of this repository's parent.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][pscustomobject]$Definition,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [AllowEmptyString()][string]$ParameterPath,
        [Parameter(Mandatory)][hashtable]$SetupRepositories,
        [AllowEmptyString()][string]$ParameterAzdEnvironmentName,
        [switch]$AllowAbsent,
        [switch]$CloneIfMissing
    )

    $setupEntry = $SetupRepositories.ContainsKey($Definition.Identity) `
        ? $SetupRepositories[$Definition.Identity] `
        : $null
    $environmentPath = [Environment]::GetEnvironmentVariable($Definition.EnvironmentVariable)
    if ($null -ne $Definition.PSObject.Properties['BundledPath']) {
        $bundledPath = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot $Definition.BundledPath))
        foreach ($override in @(
            $ParameterPath, $environmentPath, ($null -eq $setupEntry ? '' : $setupEntry.Path)
        )) {
            if (-not [string]::IsNullOrWhiteSpace($override) -and
                -not [IO.Path]::GetFullPath($override).Equals($bundledPath, [StringComparison]::OrdinalIgnoreCase)) {
                throw (
                    "$($Definition.DisplayName) must use '$bundledPath'. " +
                    "Remove its legacy external path override. External deployment is not permitted.")
            }
        }
        Assert-DemoReadyBundledPath -RepositoryRoot $RepositoryRoot -Path $bundledPath `
            -RequiredPaths $Definition.RequiredPaths
        if ($null -ne $setupEntry -and -not [string]::IsNullOrWhiteSpace($setupEntry.AzdEnvironmentName)) {
            Write-Warning "Ignoring the legacy $($Definition.Identity) setup-file environment. The presentation base determines the owned environment."
        }
        return [pscustomobject]@{
            Identity = $Definition.Identity
            DisplayName = $Definition.DisplayName
            Path = $bundledPath
            PathSource = 'bundled'
            AzdEnvironmentName = $ParameterAzdEnvironmentName
            AzdEnvironmentSource = 'presentation-base'
        }
    }
    $repositoryParent = Split-Path -Parent $RepositoryRoot
    $siblingPath = Join-Path $repositoryParent $Definition.FolderName
    $createdByThisRun = $false

    $candidates = @(
        [pscustomobject]@{ Source = 'parameter'; Path = $ParameterPath },
        [pscustomobject]@{ Source = 'environment-variable'; Path = $environmentPath },
        [pscustomobject]@{ Source = 'setup-file'; Path = ($null -eq $setupEntry ? '' : $setupEntry.Path) },
        [pscustomobject]@{ Source = 'sibling-folder'; Path = $siblingPath }
    )
    $unknownMessage = (
        "The $($Definition.DisplayName) repository path is unknown. " +
        "Set -$($Definition.ParameterName), " +
        "set $($Definition.EnvironmentVariable), " +
        "add it to .demo-ready\repositories.local.json, " +
        "or clone it as the sibling folder '$($Definition.FolderName)'.")
    $selected = $candidates |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.Path) } |
        Select-Object -First 1
    if ($null -eq $selected) {
        throw $unknownMessage
    }
    if (-not (Test-Path -LiteralPath $selected.Path -PathType Container)) {
        if ($CloneIfMissing -and $selected.Source -in @('setup-file', 'sibling-folder')) {
            $siblingPath = Join-Path $repositoryParent $Definition.FolderName
            if (Test-Path -LiteralPath $siblingPath -PathType Container) {
                $selected = [pscustomobject]@{
                    Source = 'sibling-folder'
                    Path = $siblingPath
                }
            }
            else {
                Invoke-DemoReadyExternalClone `
                    -Definition $Definition `
                    -RepositoryRoot $RepositoryRoot
                $createdByThisRun = $true
                $selected = [pscustomobject]@{
                    Source = 'sibling-folder'
                    Path = $siblingPath
                }
            }
        }
        elseif ($AllowAbsent) {
            Write-Warning "The optional $($Definition.DisplayName) checkout is absent. Its tile remains not configured."
            return $null
        }
        elseif ($selected.Source -ceq 'sibling-folder') {
            throw $unknownMessage
        }
        else {
            throw "The $($Definition.DisplayName) repository path does not exist: $($selected.Path)"
        }
    }

    $resolvedPath = Assert-DemoReadyExternalRepositoryCheckout `
        -Path ([string]$selected.Path) `
        -Definition $Definition

    $azdEnvironmentName = ''
    $azdEnvironmentSource = 'azd-default'
    if (-not [string]::IsNullOrWhiteSpace($ParameterAzdEnvironmentName)) {
        $azdEnvironmentName = $ParameterAzdEnvironmentName
        $azdEnvironmentSource = 'parameter'
    }
    elseif ($null -ne $setupEntry -and
        -not [string]::IsNullOrWhiteSpace($setupEntry.AzdEnvironmentName)) {
        $azdEnvironmentName = $setupEntry.AzdEnvironmentName
        $azdEnvironmentSource = 'setup-file'
    }

    return [pscustomobject]@{
        Identity = $Definition.Identity
        DisplayName = $Definition.DisplayName
        Path = $resolvedPath
        PathSource = $selected.Source
        CreatedByThisRun = $createdByThisRun
        AzdEnvironmentName = $azdEnvironmentName
        AzdEnvironmentSource = $azdEnvironmentSource
    }
}

function Invoke-DemoReadyExternalClone {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][pscustomobject]$Definition,
        [Parameter(Mandatory)][string]$RepositoryRoot
    )

    if ($null -eq $Definition.PSObject.Properties['RepositoryUrl'] -or
        [string]::IsNullOrWhiteSpace([string]$Definition.RepositoryUrl)) {
        throw "The $($Definition.DisplayName) repository has no approved clone URL."
    }
    $approvedUrls = @{
        patriots = 'https://github.com/garylumsden/azure-ai-mgs-patriots.git'
        tokensAndCredits = 'https://github.com/garylumsden/tokens-and-credits.git'
    }
    if (-not $approvedUrls.ContainsKey([string]$Definition.Identity) -or
        [string]$Definition.RepositoryUrl -cne $approvedUrls[[string]$Definition.Identity]) {
        throw "The $($Definition.DisplayName) clone URL is not approved."
    }

    $repositoryParent = Split-Path -Parent $RepositoryRoot
    $destination = Join-Path $repositoryParent $Definition.FolderName
    if (Test-Path -LiteralPath $destination) {
        throw "The clone destination already exists: $destination"
    }
    try {
        Invoke-DemoReadyNative `
            -FilePath 'git' `
            -Arguments @('clone', '--', [string]$Definition.RepositoryUrl, $destination) `
            -WorkingDirectory $repositoryParent

        if (-not (Test-Path -LiteralPath $destination -PathType Container) -or
            -not (Test-Path -LiteralPath (Join-Path $destination '.git') -PathType Container)) {
            throw "Git did not create a valid repository at '$destination'."
        }
        foreach ($relativePath in $Definition.RequiredPaths) {
            if (-not (Test-Path -LiteralPath (Join-Path $destination $relativePath) -PathType Leaf)) {
                throw "The cloned $($Definition.DisplayName) repository is missing '$relativePath'."
            }
        }
    }
    catch {
        if (Test-Path -LiteralPath $destination) {
            Remove-Item -LiteralPath $destination -Recurse -Force -ErrorAction SilentlyContinue
        }
        throw
    }
}

function Assert-DemoReadyBundledPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string[]]$RequiredPaths
    )

    $root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\')
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith("$root\", [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A bundled application must be inside the main repository.'
    }
    foreach ($relativePath in @('') + $RequiredPaths) {
        $candidate = Join-Path $fullPath $relativePath
        $pathType = [string]::IsNullOrEmpty($relativePath) ? 'Container' : 'Leaf'
        if (-not (Test-Path -LiteralPath $candidate -PathType $pathType)) {
            throw "The bundled application is missing '$candidate'."
        }
        $item = Get-Item -LiteralPath $candidate
        while ($null -ne $item -and -not $item.FullName.Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "A bundled application cannot use a linked path: $($item.FullName)"
            }
            if ($item -is [IO.DirectoryInfo] -and (Test-Path -LiteralPath (Join-Path $item.FullName '.git'))) {
                throw "A bundled application cannot contain nested Git metadata: $($item.FullName)"
            }
            $item = $item -is [IO.DirectoryInfo] ? $item.Parent : $item.Directory
        }
    }
    $gitRoot = Invoke-DemoReadyNative -FilePath 'git' `
        -Arguments @('rev-parse', '--show-toplevel') -WorkingDirectory $fullPath -CaptureOutput -Quiet
    if (-not [IO.Path]::GetFullPath($gitRoot.Trim()).Equals($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The bundled application does not belong to the main Git working tree.'
    }
}

function Get-DemoReadyPatriotsLink {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [AllowEmptyString()][string]$ParameterPath,
        [Parameter(Mandatory)][hashtable]$SetupRepositories,
        [switch]$IncludePatriots
    )

    if (-not $IncludePatriots -and [string]::IsNullOrWhiteSpace($ParameterPath)) {
        return [pscustomobject]@{ Status = 'not-configured'; Repository = $null }
    }
    $repository = Resolve-DemoReadyExternalRepository `
        -Definition (Get-DemoReadyExternalRepositoryDefinition).patriots `
        -RepositoryRoot $RepositoryRoot -ParameterPath $ParameterPath `
        -SetupRepositories $SetupRepositories -ParameterAzdEnvironmentName '' -AllowAbsent
    if ($null -eq $repository) {
        return [pscustomobject]@{ Status = 'not-configured'; Repository = $null }
    }
    return [pscustomobject]@{ Status = 'configured'; Repository = $repository }
}

function Get-DemoReadyGitStatus {
    # Captures status plus content, so changes to an already dirty file are detected.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepositoryPath)

    $status = @(& git -C $RepositoryPath status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) {
        throw "Git could not read the status of '$RepositoryPath'."
    }
    $worktreeDiff = @(& git -C $RepositoryPath diff --binary --no-ext-diff)
    if ($LASTEXITCODE -ne 0) {
        throw "Git could not read the working-tree diff of '$RepositoryPath'."
    }
    $indexDiff = @(& git -C $RepositoryPath diff --cached --binary --no-ext-diff)
    if ($LASTEXITCODE -ne 0) {
        throw "Git could not read the index diff of '$RepositoryPath'."
    }
    $untracked = [Collections.Generic.List[object]]::new()
    $untrackedPaths = @(& git -C $RepositoryPath ls-files --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) {
        throw "Git could not list untracked files in '$RepositoryPath'."
    }
    foreach ($relativePath in $untrackedPaths) {
        $path = Join-Path $RepositoryPath $relativePath
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $untracked.Add([ordered]@{
                path = [string]$relativePath
                sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            })
        }
    }
    return [ordered]@{
        status = @($status)
        worktreeDiff = @($worktreeDiff)
        indexDiff = @($indexDiff)
        untracked = @($untracked)
    } | ConvertTo-Json -Depth 5 -Compress
}

function Assert-DemoReadyGitStatusPreserved {
    # External repositories may be dirty. Only a change of status is a failure.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$RepositoryPath,
        [Parameter(Mandatory)][AllowEmptyString()][string]$Baseline,
        [Parameter(Mandatory)][string]$DisplayName
    )

    $current = Get-DemoReadyGitStatus -RepositoryPath $RepositoryPath
    if ($current -cne $Baseline) {
        throw "The $DisplayName repository Git status changed. This script must not modify it."
    }
}

function Resolve-DemoReadyExternalAzdEnvironmentName {
    # Uses the requested environment, or the single default from azd env list.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][pscustomobject]$Repository,
        [AllowNull()][object[]]$Environments,
        [string[]]$SensitiveValues = @()
    )

    $environments = @(
        if ($PSBoundParameters.ContainsKey('Environments')) {
            @($Environments)
        }
        else {
            Get-DemoReadyAzdEnvironments `
                -ContextPath $Repository.Path `
                -SensitiveValues $SensitiveValues
        }
    )
    if ($environments.Count -eq 0) {
        throw "The $($Repository.DisplayName) repository has no azd environment."
    }

    if (-not [string]::IsNullOrWhiteSpace($Repository.AzdEnvironmentName)) {
        $requested = $Repository.AzdEnvironmentName
        if (@($environments | Where-Object { [string]$_.Name -ceq $requested }).Count -ne 1) {
            throw "The $($Repository.DisplayName) repository has no azd environment named '$requested'."
        }
        return $requested
    }

    $defaults = @($environments | Where-Object { $_.IsDefault })
    if ($defaults.Count -eq 0) {
        throw (
            "The $($Repository.DisplayName) repository has no default azd environment. " +
            'Name one in the setup file or in a parameter.')
    }
    if ($defaults.Count -gt 1) {
        throw (
            "The $($Repository.DisplayName) repository has more than one default azd environment. " +
            'Name one in the setup file or in a parameter.')
    }
    return [string]$defaults[0].Name
}

function Get-DemoReadyExternalAzdEnvironmentPlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][pscustomobject]$Repository,
        [Parameter(Mandatory)][string]$DefaultEnvironmentName,
        [Parameter(Mandatory)][string]$Location,
        [string[]]$SensitiveValues = @()
    )

    $environments = @(Get-DemoReadyAzdEnvironments `
        -ContextPath $Repository.Path `
        -SensitiveValues $SensitiveValues)
    if ($environments.Count -gt 0) {
        $environmentName = Resolve-DemoReadyExternalAzdEnvironmentName `
            -Repository $Repository `
            -Environments $environments `
            -SensitiveValues $SensitiveValues
        $values = Get-DemoReadyAzdValues `
            -ContextPath $Repository.Path `
            -EnvironmentName $environmentName `
            -SensitiveValues $SensitiveValues
        $existingLocation = [string]$values['AZURE_LOCATION']
        return [pscustomobject]@{
            EnvironmentName = $environmentName
            EnvironmentExists = $true
            Location = [string]::IsNullOrWhiteSpace($existingLocation) ? 'unchanged' : $existingLocation
            Action = 'Reuse the existing azd environment. Run no Azure deployment command.'
        }
    }

    $environmentName = [string]$Repository.AzdEnvironmentName
    if ([string]::IsNullOrWhiteSpace($environmentName)) {
        $environmentName = $DefaultEnvironmentName
    }
    return [pscustomobject]@{
        EnvironmentName = $environmentName
        EnvironmentExists = $false
        Location = $Location
        Action = 'Create the azd environment and deploy it with azd up.'
    }
}

function New-DemoReadyOptionalAzdState {
    [CmdletBinding()]
    param()

    return [ordered]@{
        repositoryPath = $null
        azdEnvironmentName = $null
        azdEnvironmentExisted = $null
        azdEnvironmentManagedByThisRepository = $false
        azdEnvironmentCreatedByThisRun = $false
        azdEnvironmentDeployedByThisRun = $false
    }
}

function Restore-DemoReadyOptionalAzdOwnership {
    [CmdletBinding()]
    param(
        [AllowNull()][object]$PreviousReport,
        [Parameter(Mandatory)][string]$EnvironmentBaseName,
        [Parameter(Mandatory)][string]$SubscriptionId,
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$ExternalPlan,
        [Parameter(Mandatory)][Collections.IDictionary]$State,
        [Parameter(Mandatory)][Collections.IDictionary]$CheckoutPaths,
        [Parameter(Mandatory)][Collections.IDictionary]$CheckoutCreated
    )

    if ($null -eq $PreviousReport) {
        return
    }
    $environmentProperty = $PreviousReport.PSObject.Properties['environmentName']
    $subscriptionProperty = $PreviousReport.PSObject.Properties['subscriptionId']
    $externalProperty = $PreviousReport.PSObject.Properties['external']
    if ($null -eq $environmentProperty -or
        [string]$environmentProperty.Value -cne $EnvironmentBaseName -or
        $null -eq $subscriptionProperty -or
        [string]$subscriptionProperty.Value -cne $SubscriptionId -or
        $null -eq $externalProperty -or $null -eq $externalProperty.Value) {
        return
    }
    $plans = @{}
    foreach ($entry in $ExternalPlan) {
        $plans[[string]$entry.Identity] = $entry
    }
    foreach ($identity in @($State.Keys)) {
        $previousProperty = $externalProperty.Value.PSObject.Properties[[string]$identity]
        if ($null -eq $previousProperty) {
            continue
        }
        $previous = $previousProperty.Value
        $managedProperty = $previous.PSObject.Properties['azdEnvironmentManagedByThisRepository']
        $createdProperty = $previous.PSObject.Properties['azdEnvironmentCreatedByThisRun']
        $managed = ($null -ne $managedProperty -and [bool]$managedProperty.Value) -or
            ($null -ne $createdProperty -and [bool]$createdProperty.Value)
        $repositoryPathProperty = $previous.PSObject.Properties['repositoryPath']
        $environmentNameProperty = $previous.PSObject.Properties['azdEnvironmentName']
        $repositoryPath = $null -eq $repositoryPathProperty ? '' : [string]$repositoryPathProperty.Value
        $environmentName = $null -eq $environmentNameProperty ? '' : [string]$environmentNameProperty.Value
        if (-not $managed -or
            [string]::IsNullOrWhiteSpace($repositoryPath) -or
            [string]::IsNullOrWhiteSpace($environmentName)) {
            continue
        }
        if ($plans.ContainsKey([string]$identity)) {
            $entry = $plans[[string]$identity]
            if (-not [bool]$entry.EnvironmentExists -or
                $environmentName -cne [string]$entry.EnvironmentName -or
                -not [IO.Path]::GetFullPath($repositoryPath).Equals(
                    [IO.Path]::GetFullPath([string]$entry.Path),
                    [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
        }
        $State[$identity]['repositoryPath'] = $repositoryPath
        $State[$identity]['azdEnvironmentName'] = $environmentName
        $State[$identity]['azdEnvironmentExisted'] = $true
        $State[$identity]['azdEnvironmentManagedByThisRepository'] = $managed
        $State[$identity]['azdEnvironmentCreatedByThisRun'] =
            $null -ne $createdProperty -and [bool]$createdProperty.Value
        $deployedProperty = $previous.PSObject.Properties['azdEnvironmentDeployedByThisRun']
        $State[$identity]['azdEnvironmentDeployedByThisRun'] =
            $null -ne $deployedProperty -and [bool]$deployedProperty.Value
        $clonedProperty = $previous.PSObject.Properties['clonedByThisRun']
        if ($null -ne $clonedProperty -and [bool]$clonedProperty.Value) {
            $CheckoutPaths[$identity] = $repositoryPath
            $CheckoutCreated[$identity] = $true
        }
    }
}

function Initialize-DemoReadyExternalAzdEnvironment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][pscustomobject]$Integration,
        [Parameter(Mandatory)][string]$DefaultEnvironmentName,
        [Parameter(Mandatory)][string]$Location,
        [Parameter(Mandatory)][string]$SubscriptionId,
        [Parameter(Mandatory)][string]$PrincipalId,
        [Parameter(Mandatory)][Collections.IDictionary]$State,
        [string[]]$SensitiveValues = @(),
        [switch]$UsePatriotsFoundryIqDefaults,
        [string]$LogPath
    )

    if ($Integration.Status -cne 'discovered' -or $null -eq $Integration.Repository) {
        return
    }

    $repository = $Integration.Repository
    $environmentPlan = Get-DemoReadyExternalAzdEnvironmentPlan `
        -Repository $repository `
        -DefaultEnvironmentName $DefaultEnvironmentName `
        -Location $Location `
        -SensitiveValues $SensitiveValues
    $environmentName = [string]$environmentPlan.EnvironmentName
    $State['azdEnvironmentName'] = $environmentName
    $State['azdEnvironmentExisted'] = [bool]$environmentPlan.EnvironmentExists

    if ($environmentPlan.EnvironmentExists) {
        Invoke-DemoReadyAzd `
            -Arguments @('env', 'select', $environmentName, '--no-prompt') `
            -WorkingDirectory $repository.Path `
            -SensitiveValues $SensitiveValues `
            -Quiet
        return
    }

    Invoke-DemoReadyAzd `
        -Arguments @(
            'env', 'new', $environmentName,
            '--subscription', $SubscriptionId,
            '--location', $Location,
            '--no-prompt'
        ) `
        -WorkingDirectory $repository.Path `
        -SensitiveValues $SensitiveValues `
        -Quiet
    $State['azdEnvironmentManagedByThisRepository'] = $true
    $State['azdEnvironmentCreatedByThisRun'] = $true

    foreach ($setting in ([ordered]@{
        AZURE_SUBSCRIPTION_ID = $SubscriptionId
        AZURE_LOCATION = $Location
        AZURE_PRINCIPAL_ID = $PrincipalId
    }).GetEnumerator()) {
        Set-DemoReadyAzdValue `
            -ContextPath $repository.Path `
            -EnvironmentName $environmentName `
            -Name ([string]$setting.Key) `
            -Value ([string]$setting.Value) `
            -SensitiveValues $SensitiveValues
    }

    if ($UsePatriotsFoundryIqDefaults) {
        foreach ($setting in ([ordered]@{
            COUNCIL_GROUNDING_PROVIDER = 'foundryiq'
            WEBIQ_CONNECTION_NAME = ''
            WEBIQ_API_KEY = ''
        }).GetEnumerator()) {
            Set-DemoReadyAzdValue `
                -ContextPath $repository.Path `
                -EnvironmentName $environmentName `
                -Name ([string]$setting.Key) `
                -Value ([string]$setting.Value) `
                -SensitiveValues $SensitiveValues
        }
    }

    Invoke-DemoReadyAzd `
        -Arguments @('up', '--environment', $environmentName, '--no-prompt') `
        -WorkingDirectory $repository.Path `
        -LogPath $LogPath `
        -SensitiveValues $SensitiveValues
    $State['azdEnvironmentDeployedByThisRun'] = $true
}

function Get-DemoReadyOptionalAzureTeardownContexts {
    [CmdletBinding()]
    param(
        [AllowNull()][object]$Report,
        [Parameter(Mandatory)][bool]$ReportMatchesEnvironment
    )

    if (-not $ReportMatchesEnvironment -or $null -eq $Report -or $null -eq $Report.external) {
        return @()
    }

    $contexts = [Collections.Generic.List[object]]::new()
    foreach ($definition in @(
        [pscustomobject]@{ Key = 'patriots'; Name = 'Patriots' },
        [pscustomobject]@{ Key = 'tokensAndCredits'; Name = 'Tokens and Credits' }
    )) {
        $property = $Report.external.PSObject.Properties[$definition.Key]
        if ($null -eq $property) {
            continue
        }
        $entry = $property.Value
        $managedProperty = $entry.PSObject.Properties['azdEnvironmentManagedByThisRepository']
        $createdProperty = $entry.PSObject.Properties['azdEnvironmentCreatedByThisRun']
        $managed = $null -ne $managedProperty -and [bool]$managedProperty.Value
        $created = $null -ne $createdProperty -and [bool]$createdProperty.Value
        if ($managed -and -not $created) {
            throw "The recorded $($definition.Name) Azure ownership lacks creation proof."
        }
        if (-not $created) {
            continue
        }
        $existedProperty = $entry.PSObject.Properties['azdEnvironmentExisted']
        $deployedProperty = $entry.PSObject.Properties['azdEnvironmentDeployedByThisRun']
        if ($null -eq $existedProperty -or $null -eq $deployedProperty) {
            throw "The recorded $($definition.Name) Azure ownership state is inconsistent."
        }
        $environmentName = [string]$entry.azdEnvironmentName
        $repositoryPath = [string]$entry.repositoryPath
        if ([string]::IsNullOrWhiteSpace($environmentName) -or
            $environmentName -notmatch '^[a-z][a-z0-9-]{0,62}[a-z0-9]$' -or
            [string]::IsNullOrWhiteSpace($repositoryPath)) {
            throw "The recorded $($definition.Name) Azure ownership state is incomplete."
        }
        $contexts.Add([pscustomobject]@{
            Key = $definition.Key
            Name = $definition.Name
            Path = [IO.Path]::GetFullPath($repositoryPath)
            Environment = $environmentName
            Optional = $true
        })
    }
    return @($contexts)
}

function Get-DemoReadyDotEnvValues {
    # Reads plain KEY=VALUE files. Later files override earlier files.
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Paths)

    $values = @{}
    foreach ($path in $Paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }
        foreach ($line in Get-Content -LiteralPath $path) {
            if ($line -match '^\s*(?:#|$)') {
                continue
            }
            $match = [regex]::Match($line, '^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=(?<value>.*)$')
            if (-not $match.Success) {
                continue
            }
            $encodedValue = $match.Groups['value'].Value.Trim()
            $values[$match.Groups['name'].Value] =
                if ($encodedValue.StartsWith('"', [StringComparison]::Ordinal) -and
                    $encodedValue.EndsWith('"', [StringComparison]::Ordinal)) {
                    [string]($encodedValue | ConvertFrom-Json)
                }
                else {
                    $encodedValue.Trim("'")
                }
        }
    }
    return $values
}

function Invoke-DemoReadyExternalBuild {
    # Restores and builds one external project through the Microsoft package feed proxy.
    # The Git status of the external repository must not change.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][pscustomobject]$Repository,
        [Parameter(Mandatory)][string]$ProjectPath,
        [Parameter(Mandatory)][string]$LogRoot,
        [Parameter(Mandatory)][AllowEmptyString()][string]$GitBaseline,
        [string[]]$SensitiveValues = @()
    )

    if ($Repository.Identity -eq 'patriots') {
        throw 'Patriots uses its dedicated build path, not the shared external build helper.'
    }
    Invoke-DemoReadyProjectBuild `
        -RepositoryPath $Repository.Path `
        -ProjectPath $ProjectPath `
        -LogRoot $LogRoot `
        -LogName $Repository.Identity `
        -SensitiveValues $SensitiveValues
    Assert-DemoReadyGitStatusPreserved `
        -RepositoryPath $Repository.Path `
        -Baseline $GitBaseline `
        -DisplayName $Repository.DisplayName
}
