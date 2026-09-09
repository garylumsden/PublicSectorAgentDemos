Set-StrictMode -Version Latest

function Get-Demo1AgentProperty {
    param(
        [object]$InputObject,
        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($null -eq $InputObject) {
        return $null
    }

    if ($InputObject -is [Collections.IDictionary]) {
        return $InputObject.Contains($Name) ? $InputObject[$Name] : $null
    }

    $property = $InputObject.PSObject.Properties[$Name]
    return $null -eq $property ? $null : $property.Value
}

function Invoke-Demo1AgentRest {
    param(
        [Parameter(Mandatory)]
        [hashtable]$Request,
        [scriptblock]$RestInvoker
    )

    if ($null -ne $RestInvoker) {
        return & $RestInvoker $Request
    }

    return Invoke-RestMethod @Request
}

function Test-Demo1TransientVectorizationFailure {
    param([Parameter(Mandatory)]$ErrorRecord)

    $response = Get-Demo1AgentProperty $ErrorRecord.Exception 'Response'
    $statusCode = Get-Demo1AgentProperty $response 'StatusCode'
    if ($null -ne $statusCode -and [int]$statusCode -eq 429) {
        return $true
    }

    $details = @(
        [string](Get-Demo1AgentProperty $ErrorRecord.Exception 'Message')
        [string](Get-Demo1AgentProperty $ErrorRecord.ErrorDetails 'Message')
    ) -join [Environment]::NewLine
    return $details -match '(?is)vectorization.+(?:429|TooManyRequests)'
}

function Invoke-Demo1KnowledgeProbe {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [hashtable]$Request,
        [ValidateRange(1, 10)]
        [int]$MaxAttempts = 6,
        [ValidateRange(0, 300)]
        [int]$InitialDelaySeconds = 5,
        [scriptblock]$RestInvoker,
        [scriptblock]$SleepAction
    )

    $delaySeconds = $InitialDelaySeconds
    for ($attempt = 1; $attempt -le $MaxAttempts; $attempt++) {
        try {
            return Invoke-Demo1AgentRest -Request $Request -RestInvoker $RestInvoker
        }
        catch {
            if ($attempt -eq $MaxAttempts -or
                -not (Test-Demo1TransientVectorizationFailure $_)) {
                throw
            }

            Write-Warning (
                "Knowledge-base vectorization was throttled. Retrying in " +
                "$delaySeconds seconds (attempt $($attempt + 1) of $MaxAttempts).")
            if ($null -ne $SleepAction) {
                & $SleepAction $delaySeconds
            }
            else {
                Start-Sleep -Seconds $delaySeconds
            }
            $delaySeconds = [Math]::Min([Math]::Max(1, $delaySeconds * 2), 60)
        }
    }
}

function Test-Demo1NotFound {
    param([Parameter(Mandatory)]$ErrorRecord)

    $response = Get-Demo1AgentProperty $ErrorRecord.Exception 'Response'
    $statusCode = Get-Demo1AgentProperty $response 'StatusCode'
    return $null -ne $statusCode -and [int]$statusCode -eq 404
}

function Get-Demo1NamedAgent {
    param(
        [Parameter(Mandatory)]
        [string]$ProjectUri,
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [hashtable]$Headers,
        [scriptblock]$RestInvoker
    )

    $escapedName = [Uri]::EscapeDataString($Name)
    $request = @{
        Method = 'Get'
        Uri = "$($ProjectUri.TrimEnd('/'))/agents/${escapedName}?api-version=v1"
        Headers = $Headers
    }

    try {
        return Invoke-Demo1AgentRest -Request $request -RestInvoker $RestInvoker
    }
    catch {
        if (Test-Demo1NotFound $_) {
            return $null
        }

        throw
    }
}

function Assert-Demo1AgentValue {
    param(
        [object]$Expected,
        [object]$Actual,
        [Parameter(Mandatory)]
        [string]$Path
    )

    if ($null -eq $Expected) {
        if ($null -ne $Actual) {
            throw "The published agent definition differs at '$Path'."
        }

        return
    }

    if ($Expected -is [Collections.IDictionary]) {
        $expectedProperties = @(
            foreach ($key in $Expected.Keys) {
                [pscustomobject]@{ Name = [string]$key; Value = $Expected[$key] }
            }
        )
        foreach ($property in $expectedProperties) {
            $actualProperty = Get-Demo1AgentProperty $Actual $property.Name
            if ($null -eq $actualProperty -and $null -ne $property.Value) {
                throw "The published agent definition is missing '$Path.$($property.Name)'."
            }

            Assert-Demo1AgentValue `
                -Expected $property.Value `
                -Actual $actualProperty `
                -Path "$Path.$($property.Name)"
        }

        return
    }

    if ($Expected -is [Collections.IEnumerable] -and $Expected -isnot [string]) {
        $expectedItems = @($Expected)
        $actualItems = @($Actual)
        if ($expectedItems.Count -ne $actualItems.Count) {
            throw "The published agent definition has a different item count at '$Path'."
        }

        for ($index = 0; $index -lt $expectedItems.Count; $index++) {
            Assert-Demo1AgentValue `
                -Expected $expectedItems[$index] `
                -Actual $actualItems[$index] `
                -Path "$Path[$index]"
        }

        return
    }

    if ($Expected -isnot [string] -and
        $Expected -isnot [ValueType] -and
        $Expected.PSObject.Properties.Count -gt 0) {
        foreach ($property in $Expected.PSObject.Properties) {
            $actualProperty = Get-Demo1AgentProperty $Actual $property.Name
            if ($null -eq $actualProperty -and $null -ne $property.Value) {
                throw "The published agent definition is missing '$Path.$($property.Name)'."
            }

            Assert-Demo1AgentValue `
                -Expected $property.Value `
                -Actual $actualProperty `
                -Path "$Path.$($property.Name)"
        }

        return
    }

    if ($Expected -is [string]) {
        if ([string]$Actual -cne [string]$Expected) {
            throw "The published agent definition differs at '$Path'."
        }
    }
    elseif ($Actual -ne $Expected) {
        throw "The published agent definition differs at '$Path'."
    }
}

function Publish-Demo1NamedAgent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ProjectUri,
        [Parameter(Mandatory)]
        [hashtable]$Headers,
        [Parameter(Mandatory)]
        [Collections.IDictionary]$Payload,
        [scriptblock]$RestInvoker
    )

    $name = [string](Get-Demo1AgentProperty $Payload 'name')
    if ([string]::IsNullOrWhiteSpace($name)) {
        throw 'The agent payload must contain a name.'
    }

    $definition = Get-Demo1AgentProperty $Payload 'definition'
    if ($null -eq $definition) {
        throw "The '$name' agent payload must contain a definition."
    }

    $baseUri = $ProjectUri.TrimEnd('/')
    $escapedName = [Uri]::EscapeDataString($name)
    $existing = Get-Demo1NamedAgent `
        -ProjectUri $baseUri `
        -Name $name `
        -Headers $Headers `
        -RestInvoker $RestInvoker

    if ($null -eq $existing) {
        $requestBody = $Payload
        $publishRequest = @{
            Method = 'Post'
            Uri = "$baseUri/agents?api-version=v1"
            Headers = $Headers
            Body = ($requestBody | ConvertTo-Json -Depth 100)
        }
        $operation = 'created'
    }
    else {
        $requestBody = [ordered]@{
            description = Get-Demo1AgentProperty $Payload 'description'
            definition = $definition
            metadata = Get-Demo1AgentProperty $Payload 'metadata'
        }
        $publishRequest = @{
            Method = 'Post'
            Uri = "$baseUri/agents/${escapedName}?api-version=v1"
            Headers = $Headers
            Body = ($requestBody | ConvertTo-Json -Depth 100)
        }
        $operation = 'updated'
    }

    $published = Invoke-Demo1AgentRest `
        -Request $publishRequest `
        -RestInvoker $RestInvoker
    $latest = Get-Demo1AgentProperty (Get-Demo1AgentProperty $published 'versions') 'latest'
    if ($null -eq $latest) {
        $latest = $published
    }

    $version = [string](Get-Demo1AgentProperty $latest 'version')
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "The Foundry service did not return a version for '$name'."
    }

    $escapedVersion = [Uri]::EscapeDataString($version)
    $verifyRequest = @{
        Method = 'Get'
        Uri = "$baseUri/agents/${escapedName}/versions/${escapedVersion}?api-version=v1"
        Headers = $Headers
    }
    $verified = Invoke-Demo1AgentRest `
        -Request $verifyRequest `
        -RestInvoker $RestInvoker
    if ([string](Get-Demo1AgentProperty $verified 'name') -cne $name) {
        throw "The Foundry service returned an unexpected name for '$name' version '$version'."
    }

    Assert-Demo1AgentValue `
        -Expected $definition `
        -Actual (Get-Demo1AgentProperty $verified 'definition') `
        -Path 'definition'

    return [pscustomobject]@{
        Name = $name
        Version = $version
        Operation = $operation
    }
}

Export-ModuleMember -Function Publish-Demo1NamedAgent, Invoke-Demo1KnowledgeProbe
