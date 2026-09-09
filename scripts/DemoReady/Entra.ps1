# Microsoft Entra application handling for the repository-owned Demo 4 API.
# Dot-source this file after Common.ps1. It defines functions only.

function Invoke-DemoReadyAzRestJson {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('post', 'patch')][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$JsonBody,
        [Parameter(Mandatory)][string]$WorkingDirectory,
        [Parameter(Mandatory)][string]$RuntimeRoot,
        [Parameter(Mandatory)][string[]]$SensitiveValues,
        [switch]$CaptureOutput,
        [switch]$PreserveCapturedOutput,
        [switch]$Quiet
    )

    $null = New-Item -ItemType Directory -Path $RuntimeRoot -Force
    $requestPath = Join-Path `
        $RuntimeRoot `
        "graph-request-$PID-$([Guid]::NewGuid().ToString('N')).json"
    try {
        Set-Content `
            -LiteralPath $requestPath `
            -Value $JsonBody `
            -Encoding utf8NoBOM
        return Invoke-DemoReadyNative `
            -FilePath 'az' `
            -Arguments @(
                'rest',
                '--method', $Method,
                '--uri', $Uri,
                '--headers', 'Content-Type=application/json',
                '--body', "@$requestPath",
                '--output', ($CaptureOutput ? 'json' : 'none'),
                '--only-show-errors'
            ) `
            -WorkingDirectory $WorkingDirectory `
            -SensitiveValues $SensitiveValues `
            -CaptureOutput:$CaptureOutput `
            -PreserveCapturedOutput:$PreserveCapturedOutput `
            -Quiet:$Quiet
    }
    finally {
        Remove-Item -LiteralPath $requestPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-DemoReadyPersistedObjectId {
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string]$StateObjectId,
        [AllowEmptyString()][string]$AzdObjectId
    )

    if (-not [string]::IsNullOrWhiteSpace($StateObjectId) -and
        -not [string]::IsNullOrWhiteSpace($AzdObjectId) -and
        $StateObjectId -cne $AzdObjectId) {
        throw 'The persisted Entra application object IDs do not match.'
    }
    $objectId = [string]::IsNullOrWhiteSpace($StateObjectId) ? $AzdObjectId : $StateObjectId
    if (-not [string]::IsNullOrWhiteSpace($objectId) -and
        $objectId -notmatch '^[0-9a-fA-F-]{36}$') {
        throw 'A persisted Entra application object ID is invalid.'
    }
    return [string]$objectId
}

function Get-DemoReadyPersistedApplicationId {
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string]$StateApplicationId,
        [AllowEmptyString()][string]$AzdApplicationId
    )

    if (-not [string]::IsNullOrWhiteSpace($StateApplicationId) -and
        -not [string]::IsNullOrWhiteSpace($AzdApplicationId) -and
        $StateApplicationId -cne $AzdApplicationId) {
        throw 'The persisted Entra application client IDs do not match.'
    }
    $applicationId = [string]::IsNullOrWhiteSpace($StateApplicationId) `
        ? $AzdApplicationId `
        : $StateApplicationId
    if (-not [string]::IsNullOrWhiteSpace($applicationId) -and
        $applicationId -notmatch '^[0-9a-fA-F-]{36}$') {
        throw 'A persisted Entra application client ID is invalid.'
    }
    return [string]$applicationId
}

function Test-DemoReadyEntraApplicationOwnership {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Application,
        [Parameter(Mandatory)][string]$DisplayName,
        [Parameter(Mandatory)][string]$OwnershipMarker
    )

    $appId = [string]$Application.appId
    $objectId = [string]$Application.id
    $expectedIdentifierUri = "api://$appId"
    return $appId -match '^[0-9a-fA-F-]{36}$' -and
        $objectId -match '^[0-9a-fA-F-]{36}$' -and
        [string]$Application.displayName -ceq $DisplayName -and
        [string]$Application.notes -ceq $OwnershipMarker -and
        @($Application.identifierUris) -ccontains $expectedIdentifierUri
}

function Get-DemoReadyEntraApplicationByApplicationId {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ApplicationId,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string[]]$SensitiveValues
    )

    if ($ApplicationId -notmatch '^[0-9a-fA-F-]{36}$') {
        throw 'The persisted Entra application client ID is invalid.'
    }
    $json = Invoke-DemoReadyNative `
        -FilePath 'az' `
        -Arguments @(
            'ad', 'app', 'list',
            '--filter', "appId eq '$ApplicationId'",
            '--query', '[].{appId:appId,id:id,displayName:displayName,notes:notes,identifierUris:identifierUris,web:web,api:api}',
            '--output', 'json',
            '--only-show-errors'
        ) `
        -WorkingDirectory $RepositoryRoot `
        -SensitiveValues $SensitiveValues `
        -CaptureOutput `
        -PreserveCapturedOutput `
        -Quiet
    $applications = @($json | ConvertFrom-Json -Depth 20)
    if ($applications.Count -gt 1) {
        throw 'Microsoft Entra returned duplicate applications for the persisted client ID.'
    }
    return $applications.Count -eq 0 ? $null : $applications[0]
}

function Get-DemoReadyRequestedAccessTokenVersion {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object]$Application)

    $apiProperty = $Application.PSObject.Properties['api']
    if ($null -eq $apiProperty -or $null -eq $apiProperty.Value) {
        return 0
    }
    $versionProperty = $apiProperty.Value.PSObject.Properties['requestedAccessTokenVersion']
    if ($null -eq $versionProperty -or $null -eq $versionProperty.Value) {
        return 0
    }
    return [int]$versionProperty.Value
}

function Set-DemoReadyRequestedAccessTokenVersion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Application,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$RuntimeRoot,
        [Parameter(Mandatory)][string[]]$SensitiveValues
    )

    if ((Get-DemoReadyRequestedAccessTokenVersion -Application $Application) -eq 2) {
        return
    }
    $body = @{
        api = @{
            requestedAccessTokenVersion = 2
        }
    } | ConvertTo-Json -Compress
    Invoke-DemoReadyAzRestJson `
        -Method patch `
        -Uri "https://graph.microsoft.com/v1.0/applications/$($Application.id)" `
        -JsonBody $body `
        -WorkingDirectory $RepositoryRoot `
        -RuntimeRoot $RuntimeRoot `
        -SensitiveValues $SensitiveValues `
        -Quiet
    $Application | Add-Member `
        -NotePropertyName api `
        -NotePropertyValue ([pscustomobject]@{ requestedAccessTokenVersion = 2 }) `
        -Force
}

function Assert-DemoReadyEntraCollision {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Applications,
        [AllowEmptyString()][string]$ObjectId,
        [Parameter(Mandatory)][string]$DisplayName
    )

    if ([string]::IsNullOrWhiteSpace($ObjectId)) {
        if ($Applications.Count -gt 0) {
            throw "An unowned Microsoft Entra application already uses '$DisplayName'."
        }
        return
    }
    if (@($Applications | Where-Object { [string]$_.id -cne $ObjectId }).Count -gt 0) {
        throw "An unowned Microsoft Entra application already uses '$DisplayName'."
    }
}

function Test-DemoReadyAppServiceCallbackUri {
    # The only reply URL Demo 4 accepts is the App Service EasyAuth callback over HTTPS.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$RedirectUri,
        [AllowEmptyString()][string]$ApplicationName = ''
    )

    $namePattern = '[a-z0-9][a-z0-9-]{1,58}[a-z0-9]'
    if (-not [string]::IsNullOrWhiteSpace($ApplicationName)) {
        $namePattern = [regex]::Escape($ApplicationName)
    }
    $pattern = "^https://$namePattern\.azurewebsites\.net/\.auth/login/aad/callback$"
    return [bool]($RedirectUri -cmatch $pattern)
}

function Merge-DemoReadyRedirectUri {
    # Keeps a unique HTTPS-only reply-URL set. Entra must never hold an insecure reply URL.
    [CmdletBinding()]
    param(
        [AllowEmptyCollection()][string[]]$ExistingUris = @(),
        [Parameter(Mandatory)][string]$RedirectUri
    )

    if (-not (Test-DemoReadyAppServiceCallbackUri -RedirectUri $RedirectUri)) {
        throw 'The Entra reply URL is not the exact App Service EasyAuth callback.'
    }
    return @($ExistingUris + $RedirectUri |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Where-Object { $_.StartsWith('https://', [StringComparison]::Ordinal) } |
        Select-Object -Unique)
}

function Get-DemoReadyCredentialEndDate {
    # Demo credentials must expire. The lifetime is bounded and never open ended.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][DateTimeOffset]$Now,
        [ValidateRange(1, 90)][int]$LifetimeDays = 28
    )

    return $Now.AddDays($LifetimeDays).ToString(
        'yyyy-MM-ddTHH:mm:ssZ',
        [Globalization.CultureInfo]::InvariantCulture)
}

function Get-DemoReadyEntraApplication {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$DisplayName,
        [Parameter(Mandatory)][string]$OwnershipMarker,
        [AllowEmptyString()][string]$ObjectId,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$RuntimeRoot,
        [Parameter(Mandatory)][string[]]$SensitiveValues
    )

    $collisionJson = Invoke-DemoReadyNative `
        -FilePath 'az' `
        -Arguments @(
            'ad', 'app', 'list',
            '--display-name', $DisplayName,
            '--query', '[].{appId:appId,id:id,displayName:displayName,notes:notes,identifierUris:identifierUris,web:web,api:api}',
            '--output', 'json',
            '--only-show-errors'
        ) `
        -WorkingDirectory $RepositoryRoot `
        -SensitiveValues $SensitiveValues `
        -CaptureOutput `
        -PreserveCapturedOutput `
        -Quiet
    $collisions = @($collisionJson | ConvertFrom-Json -Depth 20)
    Assert-DemoReadyEntraCollision `
        -Applications $collisions `
        -ObjectId $ObjectId `
        -DisplayName $DisplayName

    if (-not [string]::IsNullOrWhiteSpace($ObjectId)) {
        if ($ObjectId -notmatch '^[0-9a-fA-F-]{36}$') {
            throw 'The persisted Entra application object ID is invalid.'
        }
        $applicationJson = Invoke-DemoReadyNative `
            -FilePath 'az' `
            -Arguments @(
                'ad', 'app', 'show',
                '--id', $ObjectId,
                '--query', '{appId:appId,id:id,displayName:displayName,notes:notes,identifierUris:identifierUris,web:web,api:api}',
                '--output', 'json',
                '--only-show-errors'
            ) `
            -WorkingDirectory $RepositoryRoot `
            -SensitiveValues $SensitiveValues `
            -CaptureOutput `
            -PreserveCapturedOutput `
            -Quiet
        $application = $applicationJson | ConvertFrom-Json -Depth 20
        if (-not (Test-DemoReadyEntraApplicationOwnership `
                -Application $application `
                -DisplayName $DisplayName `
                -OwnershipMarker $OwnershipMarker)) {
            throw 'The persisted Entra application failed its ownership or configuration check.'
        }
        Set-DemoReadyRequestedAccessTokenVersion `
            -Application $application `
            -RepositoryRoot $RepositoryRoot `
            -RuntimeRoot $RuntimeRoot `
            -SensitiveValues $SensitiveValues
        return $application
    }

    $createBody = [ordered]@{
        displayName = $DisplayName
        signInAudience = 'AzureADMyOrg'
        notes = $OwnershipMarker
    } | ConvertTo-Json -Compress
    $createdJson = Invoke-DemoReadyAzRestJson `
        -Method post `
        -Uri 'https://graph.microsoft.com/v1.0/applications' `
        -JsonBody $createBody `
        -WorkingDirectory $RepositoryRoot `
        -RuntimeRoot $RuntimeRoot `
        -SensitiveValues $SensitiveValues `
        -CaptureOutput `
        -PreserveCapturedOutput `
        -Quiet
    $application = $createdJson | ConvertFrom-Json -Depth 20
    if ([string]$application.appId -notmatch '^[0-9a-fA-F-]{36}$') {
        throw "Microsoft Entra did not return a valid client ID for '$DisplayName'."
    }

    $identifierBody = @{
        identifierUris = @("api://$($application.appId)")
        api = @{
            requestedAccessTokenVersion = 2
        }
    } | ConvertTo-Json -Compress
    Invoke-DemoReadyAzRestJson `
        -Method patch `
        -Uri "https://graph.microsoft.com/v1.0/applications/$($application.id)" `
        -JsonBody $identifierBody `
        -WorkingDirectory $RepositoryRoot `
        -RuntimeRoot $RuntimeRoot `
        -SensitiveValues $SensitiveValues `
        -Quiet
    $application.identifierUris = @("api://$($application.appId)")
    $application | Add-Member `
        -NotePropertyName api `
        -NotePropertyValue ([pscustomobject]@{ requestedAccessTokenVersion = 2 }) `
        -Force
    if (-not (Test-DemoReadyEntraApplicationOwnership `
            -Application $application `
            -DisplayName $DisplayName `
            -OwnershipMarker $OwnershipMarker)) {
        throw 'The new Entra application failed its ownership or configuration check.'
    }
    return $application
}

function Get-DemoReadyAppServiceCallbackUri {
    # Single source of the exact App Service EasyAuth reply URL.
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowEmptyString()][string]$ApplicationName)

    if ($ApplicationName -cnotmatch '^[a-z0-9][a-z0-9-]{1,58}[a-z0-9]$') {
        throw 'The Demo 4 App Service name is invalid.'
    }
    return "https://$ApplicationName.azurewebsites.net/.auth/login/aad/callback"
}

function Add-DemoReadyEntraRedirectUri {
    # Adds the exact App Service callback and enables ID-token issuance for the browser flow.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Application,
        [Parameter(Mandatory)][AllowEmptyString()][string]$ApplicationName,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string[]]$SensitiveValues
    )

    $redirectUri = Get-DemoReadyAppServiceCallbackUri -ApplicationName $ApplicationName
    if (-not (Test-DemoReadyAppServiceCallbackUri `
            -RedirectUri $redirectUri `
            -ApplicationName $ApplicationName)) {
        throw 'The Entra reply URL does not match the deployed App Service callback.'
    }
    $redirects = Merge-DemoReadyRedirectUri `
        -ExistingUris @($Application.web.redirectUris) `
        -RedirectUri $redirectUri
    $arguments = @(
        'ad', 'app', 'update',
        '--id', [string]$Application.id,
        '--enable-id-token-issuance', 'true',
        '--web-redirect-uris'
    ) + $redirects + @('--only-show-errors')
    Invoke-DemoReadyNative `
        -FilePath 'az' `
        -Arguments $arguments `
        -WorkingDirectory $RepositoryRoot `
        -SensitiveValues $SensitiveValues `
        -Quiet
}

function Ensure-DemoReadyEntraServicePrincipal {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ApplicationId,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string[]]$SensitiveValues
    )

    if ($ApplicationId -notmatch '^[0-9a-fA-F-]{36}$') {
        throw 'The Entra application client ID is invalid.'
    }

    $json = Invoke-DemoReadyNative `
        -FilePath 'az' `
        -Arguments @(
            'ad', 'sp', 'list',
            '--filter', "appId eq '$ApplicationId'",
            '--query', '[].{id:id,appId:appId}',
            '--output', 'json',
            '--only-show-errors'
        ) `
        -WorkingDirectory $RepositoryRoot `
        -SensitiveValues $SensitiveValues `
        -CaptureOutput `
        -PreserveCapturedOutput `
        -Quiet
    $servicePrincipals = @($json | ConvertFrom-Json)
    if ($servicePrincipals.Count -eq 0) {
        $json = Invoke-DemoReadyNative `
            -FilePath 'az' `
            -Arguments @(
                'ad', 'sp', 'create',
                '--id', $ApplicationId,
                '--query', '{id:id,appId:appId}',
                '--output', 'json',
                '--only-show-errors'
            ) `
            -WorkingDirectory $RepositoryRoot `
            -SensitiveValues $SensitiveValues `
            -CaptureOutput `
            -PreserveCapturedOutput `
            -Quiet
        $servicePrincipals = @($json | ConvertFrom-Json)
    }

    if ($servicePrincipals.Count -ne 1 -or
        [string]$servicePrincipals[0].id -notmatch '^[0-9a-fA-F-]{36}$' -or
        [string]$servicePrincipals[0].appId -cne $ApplicationId) {
        throw 'The Entra application service principal is invalid.'
    }
}

function New-DemoReadyCredential {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ApplicationId,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string[]]$SensitiveValues
    )

    $endDate = Get-DemoReadyCredentialEndDate -Now ([DateTimeOffset]::UtcNow)
    $json = Invoke-DemoReadyNative `
        -FilePath 'az' `
        -Arguments @(
            'ad', 'app', 'credential', 'reset',
            '--id', $ApplicationId,
            '--append',
            '--display-name', 'demo-ready',
            '--end-date', $endDate,
            '--query', '{password:password}',
            '--output', 'json',
            '--only-show-errors'
        ) `
        -WorkingDirectory $RepositoryRoot `
        -SensitiveValues $SensitiveValues `
        -CaptureOutput `
        -PreserveCapturedOutput `
        -Quiet
    $credential = $json | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace([string]$credential.password)) {
        throw 'Microsoft Entra did not return the required application credential.'
    }
    return [string]$credential.password
}

function Test-DemoReadyCredentialExpiry {
    [CmdletBinding()]
    param([AllowEmptyString()][string]$ExpiresOn)

    $parsed = [DateTimeOffset]::MinValue
    return [DateTimeOffset]::TryParse(
        $ExpiresOn,
        [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::AssumeUniversal,
        [ref]$parsed) -and
        $parsed -gt [DateTimeOffset]::UtcNow.AddDays(1)
}

function Initialize-DemoReadyDemo4Application {
    # Creates or reuses the single owned Entra application for the Demo 4 API.
    # It persists the object ID and refreshes the client credential when needed.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ContextPath,
        [Parameter(Mandatory)][string]$AzdEnvironmentName,
        [Parameter(Mandatory)][string]$EnvironmentBaseName,
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$RuntimeRoot,
        [Parameter(Mandatory)][string]$StatePath,
        [Parameter(Mandatory)][Collections.Generic.List[string]]$SensitiveValues
    )

    $existing = Get-DemoReadyAzdValues $ContextPath $AzdEnvironmentName $SensitiveValues
    $persistedSecret = [string]$existing['DEMO4_WEB_CLIENT_SECRET']
    if (-not [string]::IsNullOrWhiteSpace($persistedSecret) -and
        -not $SensitiveValues.Contains($persistedSecret)) {
        # Mask the persisted client secret before any later command can echo it.
        $SensitiveValues.Add($persistedSecret)
    }
    $state = if (Test-Path -LiteralPath $StatePath -PathType Leaf) {
        Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json -AsHashtable
    }
    else {
        [ordered]@{
            version = 1
            environmentName = $EnvironmentBaseName
            applications = [ordered]@{}
        }
    }
    if ([string]$state['environmentName'] -cne $EnvironmentBaseName -or
        $state['applications'] -isnot [Collections.IDictionary]) {
        throw 'The persisted Entra application state is invalid.'
    }

    $applications = $state['applications']
    $stateApplication = $applications.Contains('demo4Api') ? $applications['demo4Api'] : $null
    $stateObjectId = Get-DemoReadyPersistedObjectId `
        ($null -eq $stateApplication ? '' : [string]$stateApplication['objectId']) `
        ''
    $stateApplicationId = Get-DemoReadyPersistedApplicationId `
        ($null -eq $stateApplication ? '' : [string]$stateApplication['appId']) `
        ''
    $azdObjectId = Get-DemoReadyPersistedObjectId `
        '' `
        ([string]$existing['DEMO_READY_DEMO4_API_APP_OBJECT_ID'])
    $azdApplicationId = Get-DemoReadyPersistedApplicationId `
        '' `
        ([string]$existing['DEMO4_API_CLIENT_ID'])
    if ([string]::IsNullOrWhiteSpace($stateObjectId) -ne
        [string]::IsNullOrWhiteSpace($stateApplicationId)) {
        throw 'The persisted Entra application state contains an incomplete identity pair.'
    }

    $objectId = ''
    if (-not [string]::IsNullOrWhiteSpace($stateApplicationId)) {
        $stateRemote = Get-DemoReadyEntraApplicationByApplicationId `
            -ApplicationId $stateApplicationId `
            -RepositoryRoot $RepositoryRoot `
            -SensitiveValues $SensitiveValues
        if ($null -ne $stateRemote) {
            if ([string]$stateRemote.id -cne $stateObjectId) {
                throw 'The state Entra application object ID does not match its client ID.'
            }
            if (-not [string]::IsNullOrWhiteSpace($azdApplicationId) -and
                $azdApplicationId -cne $stateApplicationId) {
                $azdRemote = Get-DemoReadyEntraApplicationByApplicationId `
                    -ApplicationId $azdApplicationId `
                    -RepositoryRoot $RepositoryRoot `
                    -SensitiveValues $SensitiveValues
                if ($null -ne $azdRemote) {
                    throw 'Both state and azd reference different existing Entra applications.'
                }
            }
            $objectId = $stateObjectId
        }
        else {
            $applications.Remove('demo4Api')
            Write-DemoReadyJsonAtomic -Path $StatePath -Value $state
        }
    }

    if ([string]::IsNullOrWhiteSpace($objectId) -and
        -not [string]::IsNullOrWhiteSpace($azdApplicationId)) {
        $azdRemote = Get-DemoReadyEntraApplicationByApplicationId `
            -ApplicationId $azdApplicationId `
            -RepositoryRoot $RepositoryRoot `
            -SensitiveValues $SensitiveValues
        if ($null -ne $azdRemote) {
            if (-not [string]::IsNullOrWhiteSpace($azdObjectId) -and
                [string]$azdRemote.id -cne $azdObjectId) {
                throw 'The azd Entra application object ID does not match its client ID.'
            }
            $objectId = [string]$azdRemote.id
        }
        else {
            $azdObjectId = ''
            Set-DemoReadyAzdValue $ContextPath $AzdEnvironmentName `
                'DEMO_READY_DEMO4_API_APP_OBJECT_ID' '' $SensitiveValues
        }
    }
    elseif ([string]::IsNullOrWhiteSpace($objectId) -and
        -not [string]::IsNullOrWhiteSpace($azdObjectId)) {
        throw 'The azd Entra application state contains an object ID without a client ID.'
    }

    $ownershipMarker = "public-sector-agent-demos:demo-ready:v1:${EnvironmentBaseName}:demo4-api"
    $application = Get-DemoReadyEntraApplication `
        -DisplayName "psad-$EnvironmentBaseName-demo4-api" `
        -OwnershipMarker $ownershipMarker `
        -ObjectId $objectId `
        -RepositoryRoot $RepositoryRoot `
        -RuntimeRoot $RuntimeRoot `
        -SensitiveValues $SensitiveValues
    $applications['demo4Api'] = [ordered]@{
        objectId = [string]$application.id
        appId = [string]$application.appId
        ownershipMarker = $ownershipMarker
    }
    # Persist the validated application before dependent setup. A retry can then
    # adopt it safely if service-principal or credential creation is interrupted.
    Write-DemoReadyJsonAtomic -Path $StatePath -Value $state
    Set-DemoReadyAzdValue $ContextPath $AzdEnvironmentName `
        'DEMO_READY_DEMO4_API_APP_OBJECT_ID' ([string]$application.id) $SensitiveValues
    Ensure-DemoReadyEntraServicePrincipal `
        -ApplicationId ([string]$application.appId) `
        -RepositoryRoot $RepositoryRoot `
        -SensitiveValues $SensitiveValues

    $credential = [string]$existing['DEMO4_WEB_CLIENT_SECRET']
    $expiresOn = [string]$existing['DEMO4_WEB_CLIENT_SECRET_EXPIRES_ON']
    if ([string]$existing['DEMO4_API_CLIENT_ID'] -ne [string]$application.appId -or
        [string]::IsNullOrWhiteSpace($credential) -or
        -not (Test-DemoReadyCredentialExpiry -ExpiresOn $expiresOn)) {
        $credential = New-DemoReadyCredential `
            -ApplicationId ([string]$application.appId) `
            -RepositoryRoot $RepositoryRoot `
            -SensitiveValues $SensitiveValues
        $expiresOn = [DateTimeOffset]::UtcNow.AddDays(27).ToString('O')
    }
    if (-not $SensitiveValues.Contains($credential)) {
        $SensitiveValues.Add($credential)
    }
    # Write the client ID last. If an earlier write is interrupted, the old client ID
    # forces credential rotation on the next run instead of reusing a mismatched secret.
    Set-DemoReadyAzdValue $ContextPath $AzdEnvironmentName `
        'DEMO4_WEB_CLIENT_SECRET' $credential $SensitiveValues
    Set-DemoReadyAzdValue $ContextPath $AzdEnvironmentName `
        'DEMO4_WEB_CLIENT_SECRET_EXPIRES_ON' $expiresOn $SensitiveValues
    Set-DemoReadyAzdValue $ContextPath $AzdEnvironmentName `
        'DEMO4_API_CLIENT_ID' ([string]$application.appId) $SensitiveValues
    return $application
}
