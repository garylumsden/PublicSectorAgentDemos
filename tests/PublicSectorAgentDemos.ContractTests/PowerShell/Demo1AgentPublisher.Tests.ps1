[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('first-create', 'rerun-update-version', 'partial-recovery', 'transient-probe-retry')]
    [string]$Case,
    [Parameter(Mandatory)]
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $RepositoryRoot 'scripts\ground\Demo1AgentPublisher.psm1') -Force

function Assert-Equal {
    param(
        [object]$Expected,
        [object]$Actual,
        [Parameter(Mandatory)]
        [string]$Message
    )

    if ($Expected -is [string]) {
        if ([string]$Expected -cne [string]$Actual) {
            throw "$Message Expected '$Expected', received '$Actual'."
        }
    }
    elseif ($Expected -ne $Actual) {
        throw "$Message Expected '$Expected', received '$Actual'."
    }
}

function New-AgentPayload {
    param(
        [Parameter(Mandatory)]
        [string]$Name,
        [string]$Instructions = 'Initial instructions.'
    )

    return [ordered]@{
        name = $Name
        description = "$Name description"
        definition = [ordered]@{
            kind = 'prompt'
            model = 'gpt-5-mini'
            instructions = $Instructions
            reasoning = @{ effort = 'low' }
        }
        metadata = @{
            stage = $Name
            managedBy = 'mock-test'
        }
    }
}

function New-MockAgentService {
    param([string]$FailFirstCreateFor)

    $state = @{
        Agents = @{}
        Calls = [Collections.Generic.List[object]]::new()
        FailFirstCreateFor = $FailFirstCreateFor
        FailedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    }

    $invoker = {
        param([hashtable]$Request)

        $method = [string]$Request.Method
        $uri = [Uri][string]$Request.Uri
        $route = $uri.AbsolutePath.Substring($uri.AbsolutePath.IndexOf('/agents'))
        $state.Calls.Add([pscustomobject]@{ Method = $method; Route = $route })
        $segments = @($route.Trim('/').Split('/'))

        if ($method -eq 'Get' -and $segments.Count -eq 2) {
            $name = [Uri]::UnescapeDataString($segments[1])
            return $state.Agents.ContainsKey($name) ? $state.Agents[$name].Agent : $null
        }

        if ($method -eq 'Get' -and
            $segments.Count -eq 4 -and
            $segments[2] -eq 'versions') {
            $name = [Uri]::UnescapeDataString($segments[1])
            $version = [Uri]::UnescapeDataString($segments[3])
            return $state.Agents[$name].Versions[$version]
        }

        if ($method -ne 'Post') {
            throw "Unexpected mock request: $method $route"
        }

        $body = $Request.Body | ConvertFrom-Json -Depth 100
        $isCreate = $segments.Count -eq 1
        $name = $isCreate ? [string]$body.name : [Uri]::UnescapeDataString($segments[1])
        if ($isCreate -and
            $state.FailFirstCreateFor -ceq $name -and
            $state.FailedNames.Add($name)) {
            throw "Synthetic create failure for $name."
        }

        if ($isCreate) {
            if ($state.Agents.ContainsKey($name)) {
                throw "The mock agent '$name' already exists."
            }

            $versionNumber = 1
        }
        else {
            if (-not $state.Agents.ContainsKey($name)) {
                throw "The mock agent '$name' does not exist."
            }

            $current = $state.Agents[$name].Agent.versions.latest
            $sameDefinition =
                ($current.definition | ConvertTo-Json -Depth 100 -Compress) -ceq
                ($body.definition | ConvertTo-Json -Depth 100 -Compress)
            if ($sameDefinition) {
                return $state.Agents[$name].Agent
            }

            $versionNumber = [int]$current.version + 1
        }

        $version = [pscustomobject]@{
            object = 'agent.version'
            id = "agent-$name-$versionNumber"
            name = $name
            version = [string]$versionNumber
            description = [string]$body.description
            definition = $body.definition
        }
        if (-not $state.Agents.ContainsKey($name)) {
            $state.Agents[$name] = @{
                Versions = @{}
                Agent = $null
            }
        }

        $state.Agents[$name].Versions[[string]$versionNumber] = $version
        $state.Agents[$name].Agent = [pscustomobject]@{
            object = 'agent'
            id = "agent-$name"
            name = $name
            state = 'enabled'
            versions = @{ latest = $version }
        }
        return $state.Agents[$name].Agent
    }.GetNewClosure()

    return [pscustomobject]@{
        State = $state
        Invoker = $invoker
    }
}

$projectUri = 'https://example.services.ai.azure.com/api/projects/demo'
$headers = @{ Authorization = 'Bearer mock-token'; 'Content-Type' = 'application/json' }

switch ($Case) {
    'first-create' {
        $service = New-MockAgentService
        $result = Publish-Demo1NamedAgent `
            -ProjectUri $projectUri `
            -Headers $headers `
            -Payload (New-AgentPayload -Name 'foundation') `
            -RestInvoker $service.Invoker

        Assert-Equal 'created' $result.Operation 'The first publish operation differs.'
        Assert-Equal '1' $result.Version 'The first published version differs.'
        Assert-Equal 3 $service.State.Calls.Count 'The first publish call count differs.'
        Assert-Equal 'Get' $service.State.Calls[0].Method 'The first request method differs.'
        Assert-Equal '/agents/foundation' $service.State.Calls[0].Route 'The named query route differs.'
        Assert-Equal '/agents' $service.State.Calls[1].Route 'The create route differs.'
        Assert-Equal '/agents/foundation/versions/1' $service.State.Calls[2].Route 'The verification route differs.'
    }
    'rerun-update-version' {
        $service = New-MockAgentService
        $payload = New-AgentPayload -Name 'foundation'
        $null = Publish-Demo1NamedAgent `
            -ProjectUri $projectUri `
            -Headers $headers `
            -Payload $payload `
            -RestInvoker $service.Invoker
        $same = Publish-Demo1NamedAgent `
            -ProjectUri $projectUri `
            -Headers $headers `
            -Payload $payload `
            -RestInvoker $service.Invoker
        $changed = Publish-Demo1NamedAgent `
            -ProjectUri $projectUri `
            -Headers $headers `
            -Payload (New-AgentPayload -Name 'foundation' -Instructions 'Changed instructions.') `
            -RestInvoker $service.Invoker

        Assert-Equal 'updated' $same.Operation 'The unchanged rerun operation differs.'
        Assert-Equal '1' $same.Version 'The unchanged rerun created another version.'
        Assert-Equal 'updated' $changed.Operation 'The changed rerun operation differs.'
        Assert-Equal '2' $changed.Version 'The changed rerun did not create version 2.'
        Assert-Equal 2 $service.State.Agents.foundation.Versions.Count 'The version count differs.'
        Assert-Equal '/agents/foundation' $service.State.Calls[4].Route 'The named update route differs.'
        Assert-Equal '/agents/foundation/versions/1' $service.State.Calls[5].Route 'The rerun verification route differs.'
        Assert-Equal '/agents/foundation' $service.State.Calls[7].Route 'The changed update route differs.'
        Assert-Equal '/agents/foundation/versions/2' $service.State.Calls[8].Route 'The changed verification route differs.'
    }
    'partial-recovery' {
        $service = New-MockAgentService -FailFirstCreateFor 'ground'
        $foundation = New-AgentPayload -Name 'foundation'
        $ground = New-AgentPayload -Name 'ground'
        $failed = $false
        try {
            foreach ($payload in @($foundation, $ground)) {
                $null = Publish-Demo1NamedAgent `
                    -ProjectUri $projectUri `
                    -Headers $headers `
                    -Payload $payload `
                    -RestInvoker $service.Invoker
            }
        }
        catch {
            $failed = $true
        }

        Assert-Equal $true $failed 'The synthetic partial failure did not occur.'
        Assert-Equal $true $service.State.Agents.ContainsKey('foundation') 'Foundation was not preserved.'
        Assert-Equal $false $service.State.Agents.ContainsKey('ground') 'Ground was created during the failed run.'

        foreach ($payload in @($foundation, $ground)) {
            $null = Publish-Demo1NamedAgent `
                -ProjectUri $projectUri `
                -Headers $headers `
                -Payload $payload `
                -RestInvoker $service.Invoker
        }

        Assert-Equal $true $service.State.Agents.ContainsKey('foundation') 'Foundation was lost during recovery.'
        Assert-Equal $true $service.State.Agents.ContainsKey('ground') 'Ground was not created during recovery.'
        Assert-Equal 1 $service.State.Agents.foundation.Versions.Count 'Foundation gained a duplicate version.'
        Assert-Equal 1 $service.State.Agents.ground.Versions.Count 'Ground has an unexpected version count.'
    }
    'transient-probe-retry' {
        $attempts = 0
        $delays = [Collections.Generic.List[int]]::new()
        $invoker = {
            param([hashtable]$Request)
            $script:attempts++
            if ($script:attempts -lt 3) {
                throw 'BadGateway: vectorization endpoint returned 429 TooManyRequests.'
            }
            return [pscustomobject]@{ output = @() }
        }
        $sleep = {
            param([int]$DelaySeconds)
            $delays.Add($DelaySeconds)
        }.GetNewClosure()

        $result = Invoke-Demo1KnowledgeProbe `
            -Request @{ Method = 'Post'; Uri = "$projectUri/probe"; Headers = $headers } `
            -InitialDelaySeconds 5 `
            -RestInvoker $invoker `
            -SleepAction $sleep

        Assert-Equal 3 $attempts 'The probe retry count differs.'
        Assert-Equal 2 $delays.Count 'The probe delay count differs.'
        Assert-Equal 5 $delays[0] 'The first probe retry delay differs.'
        Assert-Equal 10 $delays[1] 'The second probe retry delay differs.'
        Assert-Equal 0 @($result.output).Count 'The successful probe response differs.'
    }
}

Write-Output "Passed $Case."
