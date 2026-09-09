$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:secretSettingName = 'MICROSOFT_PROVIDER_AUTHENTICATION_SECRET'
$script:credentialDisplayName = 'demo2-easyauth-29-day'
$script:secretLifetimeDays = 29

function Get-RequiredEnvironmentValue {
    param([Parameter(Mandatory)][string] $Name)

    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        throw "$Name is required."
    }

    return $value.Trim()
}

function Assert-GuidValue {
    param(
        [Parameter(Mandatory)][string] $Value,
        [Parameter(Mandatory)][string] $Name
    )

    $parsed = [Guid]::Empty
    if (-not [Guid]::TryParse($Value, [ref] $parsed) -or $parsed -eq [Guid]::Empty) {
        throw "$Name must be a non-empty GUID."
    }

    return $parsed.ToString()
}

function Assert-ResourceName {
    param(
        [Parameter(Mandatory)][string] $Value,
        [Parameter(Mandatory)][string] $Name
    )

    if ($Value -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,89}$') {
        throw "$Name contains invalid characters."
    }

    return $Value
}

function Get-AzureToken {
    param(
        [Parameter(Mandatory)][string] $TenantId,
        [Parameter(Mandatory)][string] $Resource
    )

    $token = & az account get-access-token `
        --tenant $TenantId `
        --resource $Resource `
        --query accessToken `
        --output tsv `
        --only-show-errors
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
        throw "Unable to acquire an access token for $Resource."
    }

    return $token.Trim()
}

function Invoke-Api {
    param(
        [Parameter(Mandatory)][ValidateSet('Get', 'Post', 'Put')][string] $Method,
        [Parameter(Mandatory)][Uri] $Uri,
        [Parameter(Mandatory)][string] $Token,
        [string] $Body
    )

    $parameters = @{
        Method = $Method
        Uri = $Uri
        Headers = @{
            Authorization = [string]::Concat('Bear', 'er ', $Token)
        }
        ContentType = 'application/json'
        MaximumRedirection = 0
        TimeoutSec = 60
    }
    if (-not [string]::IsNullOrWhiteSpace($Body)) {
        $parameters.Body = $Body
    }

    try {
        return Invoke-RestMethod @parameters
    }
    catch [Microsoft.PowerShell.Commands.HttpResponseException] {
        $status = $_.Exception.Response.StatusCode
        throw [System.Net.Http.HttpRequestException]::new(
            "Request to $($Uri.Host) failed with HTTP $([int]$status).", $null, $status)
    }
}

function Set-AppServiceSecret {
    param(
        [Parameter(Mandatory)][string] $SubscriptionId,
        [Parameter(Mandatory)][string] $ResourceGroupName,
        [Parameter(Mandatory)][string] $SiteName,
        [Parameter(Mandatory)][string] $SecretText,
        [Parameter(Mandatory)][string] $ArmToken
    )

    $baseUri =
        "https://management.azure.com/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.Web/sites/$SiteName/config/appsettings"
    $current = Invoke-Api `
        -Method Post `
        -Uri "$baseUri/list?api-version=2024-11-01" `
        -Token $ArmToken

    $properties = [ordered]@{}
    foreach ($property in $current.properties.PSObject.Properties) {
        $properties[$property.Name] = $property.Value
    }
    $properties[$script:secretSettingName] = $SecretText

    $body = @{ properties = $properties } | ConvertTo-Json -Depth 10 -Compress
    $null = Invoke-Api `
        -Method Put `
        -Uri "${baseUri}?api-version=2024-11-01" `
        -Token $ArmToken `
        -Body $body
}

function Ensure-ServicePrincipal {
    param(
        [Parameter(Mandatory)][string] $ApplicationClientId,
        [Parameter(Mandatory)][string] $DisplayName,
        [Parameter(Mandatory)][string] $GraphToken
    )

    $servicePrincipalUrl =
        "https://graph.microsoft.com/v1.0/servicePrincipals?`$filter=appId eq '$ApplicationClientId'&`$select=id"
    $lastError = $null
    for ($attempt = 1; $attempt -le 12; $attempt++) {
        $servicePrincipalResult = Invoke-Api `
            -Method Get `
            -Uri $servicePrincipalUrl `
            -Token $GraphToken
        $servicePrincipals = @($servicePrincipalResult.value)
        if ($servicePrincipals.Count -eq 1) {
            Write-Host "Enterprise application ready: $DisplayName"
            return
        }
        if ($servicePrincipals.Count -gt 1) {
            throw "Multiple service principals found for $DisplayName."
        }

        try {
            $body = @{
                appId = $ApplicationClientId
                accountEnabled = $true
                appRoleAssignmentRequired = $false
            } | ConvertTo-Json -Compress
            $created = Invoke-Api `
                -Method Post `
                -Uri 'https://graph.microsoft.com/v1.0/servicePrincipals' `
                -Token $GraphToken `
                -Body $body
            $null = Assert-GuidValue `
                -Value ([string] $created.id) `
                -Name "$DisplayName service principal object ID"
            Write-Host "Enterprise application created: $DisplayName"
            return
        }
        catch [System.Net.Http.HttpRequestException] {
            if ([int]$_.Exception.StatusCode -notin @(400, 404, 409, 429, 500, 502, 503, 504)) {
                throw
            }
            $lastError = $_.Exception.Message
            if ($attempt -lt 12) {
                Start-Sleep -Seconds 5
            }
        }
    }

    throw "Unable to create the enterprise application for $DisplayName after Graph propagation retries. $lastError"
}

function Set-EasyAuthCredential {
    param(
        [Parameter(Mandatory)][string] $ApplicationClientId,
        [Parameter(Mandatory)][string] $SiteName,
        [Parameter(Mandatory)][string] $SubscriptionId,
        [Parameter(Mandatory)][string] $ResourceGroupName,
        [Parameter(Mandatory)][string] $GraphToken,
        [Parameter(Mandatory)][string] $ArmToken
    )

    $applicationUrl =
        "https://graph.microsoft.com/v1.0/applications?`$filter=appId eq '$ApplicationClientId'&`$select=id,passwordCredentials"
    $applications = @()
    for ($attempt = 1; $attempt -le 6 -and $applications.Count -ne 1; $attempt++) {
        $applicationResult = Invoke-Api `
            -Method Get `
            -Uri $applicationUrl `
            -Token $GraphToken
        $applications = @($applicationResult.value)
        if ($applications.Count -ne 1 -and $attempt -lt 6) {
            Start-Sleep -Seconds 5
        }
    }
    if ($applications.Count -ne 1) {
        throw "Expected exactly one application for $SiteName."
    }

    $application = $applications[0]
    $applicationObjectId = Assert-GuidValue `
        -Value ([string] $application.id) `
        -Name 'application object ID'
    $start = [DateTimeOffset]::UtcNow.AddMinutes(-5)
    $end = [DateTimeOffset]::UtcNow.AddDays($script:secretLifetimeDays)
    $addPasswordBody = @{
        passwordCredential = @{
            displayName = $script:credentialDisplayName
            startDateTime = $start.ToString('O')
            endDateTime = $end.ToString('O')
        }
    } | ConvertTo-Json -Depth 5 -Compress

    $credential = Invoke-Api `
        -Method Post `
        -Uri "https://graph.microsoft.com/v1.0/applications/$applicationObjectId/addPassword" `
        -Token $GraphToken `
        -Body $addPasswordBody
    $secretText = [string] $credential.secretText
    $newKeyId = Assert-GuidValue -Value ([string] $credential.keyId) -Name 'credential key ID'
    if ([string]::IsNullOrWhiteSpace($secretText)) {
        throw "Microsoft Graph returned an empty Easy Auth secret for $SiteName."
    }

    try {
        Set-AppServiceSecret `
            -SubscriptionId $SubscriptionId `
            -ResourceGroupName $ResourceGroupName `
            -SiteName $SiteName `
            -SecretText $secretText `
            -ArmToken $ArmToken
    }
    catch [System.Net.Http.HttpRequestException] {
        try {
            $removeBody = @{ keyId = $newKeyId } | ConvertTo-Json -Compress
            $null = Invoke-Api `
                -Method Post `
                -Uri "https://graph.microsoft.com/v1.0/applications/$applicationObjectId/removePassword" `
                -Token $GraphToken `
                -Body $removeBody
        }
        catch [System.Net.Http.HttpRequestException] {
            Write-Warning "Unable to remove the unusable Easy Auth credential for $SiteName."
        }

        throw "Unable to configure the Easy Auth secret for $SiteName."
    }
    finally {
        $secretText = $null
    }

    foreach ($existing in @($application.passwordCredentials)) {
        if ([string] $existing.displayName -cne $script:credentialDisplayName) {
            continue
        }

        $keyId = [string] $existing.keyId
        if ($keyId -eq $newKeyId) {
            continue
        }

        $parsedKeyId = Assert-GuidValue -Value $keyId -Name 'existing credential key ID'
        $removeBody = @{ keyId = $parsedKeyId } | ConvertTo-Json -Compress
        $null = Invoke-Api `
            -Method Post `
            -Uri "https://graph.microsoft.com/v1.0/applications/$applicationObjectId/removePassword" `
            -Token $GraphToken `
            -Body $removeBody
    }

    Write-Host "Easy Auth credential rotated: site=$SiteName expires=$($end.ToString('yyyy-MM-dd'))"
}

$tenantId = Assert-GuidValue `
    -Value (Get-RequiredEnvironmentValue -Name 'AZURE_TENANT_ID') `
    -Name 'AZURE_TENANT_ID'
$subscriptionId = Assert-GuidValue `
    -Value (Get-RequiredEnvironmentValue -Name 'AZURE_SUBSCRIPTION_ID') `
    -Name 'AZURE_SUBSCRIPTION_ID'
$resourceGroupName = Assert-ResourceName `
    -Value (Get-RequiredEnvironmentValue -Name 'AZURE_RESOURCE_GROUP') `
    -Name 'AZURE_RESOURCE_GROUP'

$applications = @(
    @{
        ApplicationClientId = Assert-GuidValue `
            -Value (Get-RequiredEnvironmentValue -Name 'DEFRA_TOOLS_AUTH_CLIENT_ID') `
            -Name 'DEFRA_TOOLS_AUTH_CLIENT_ID'
        DisplayName = 'DEFRA Tools API'
    },
    @{
        ApplicationClientId = Assert-GuidValue `
            -Value (Get-RequiredEnvironmentValue -Name 'DEMO2_AUTH_CLIENT_ID') `
            -Name 'DEMO2_AUTH_CLIENT_ID'
        DisplayName = 'DEFRA Demo 2'
    }
)

$targets = @(
    @{
        ApplicationClientId = $applications[1].ApplicationClientId
        SiteName = Assert-ResourceName `
            -Value (Get-RequiredEnvironmentValue -Name 'DEMO2_WEB_SITE_NAME') `
            -Name 'DEMO2_WEB_SITE_NAME'
    }
)

$graphToken = Get-AzureToken -TenantId $tenantId -Resource 'https://graph.microsoft.com/'
$armToken = Get-AzureToken -TenantId $tenantId -Resource 'https://management.azure.com/'
foreach ($application in $applications) {
    Ensure-ServicePrincipal `
        -ApplicationClientId $application.ApplicationClientId `
        -DisplayName $application.DisplayName `
        -GraphToken $graphToken
}
foreach ($target in $targets) {
    Set-EasyAuthCredential `
        -ApplicationClientId $target.ApplicationClientId `
        -SiteName $target.SiteName `
        -SubscriptionId $subscriptionId `
        -ResourceGroupName $resourceGroupName `
        -GraphToken $graphToken `
        -ArmToken $armToken
}

$graphToken = $null
$armToken = $null
