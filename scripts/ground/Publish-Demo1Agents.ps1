[CmdletBinding()]
param(
    [string]$ProjectEndpoint = $env:AZURE_AI_FOUNDRY_ENDPOINT,
    [string]$SearchEndpoint = $env:DEMO1_SEARCH_ENDPOINT,
    [string]$StorageAccountName = $env:DEMO1_STORAGE_ACCOUNT_NAME,
    [string]$StorageAccountBlobHost = $env:DEMO1_STORAGE_ACCOUNT_BLOB_HOST,
    [string]$StorageAccountResourceId = $env:DEMO1_STORAGE_ACCOUNT_RESOURCE_ID,
    [string]$KnowledgeContainerName = $env:DEMO1_KNOWLEDGE_CONTAINER_NAME,
    [string]$KnowledgeSourceName = $env:DEMO1_KNOWLEDGE_SOURCE_NAME,
    [string]$AiServicesEndpoint = $env:AZURE_AI_SERVICES_ENDPOINT,
    [string]$ModelDeployment = $env:COUNCIL_FAST_MODEL,
    [string]$EmbeddingDeployment = $env:EMBEDDING_DEPLOYMENT_NAME,
    [ValidateRange(1, 3600)]
    [int]$IngestionTimeoutSeconds = 600,
    [ValidateRange(1, 60)]
    [int]$IngestionPollIntervalSeconds = 5,
    [string]$CitationIdentityPath = $env:DEMO1_CITATION_IDENTITY_PATH,
    [switch]$AgentsOnly,
    [string]$RepositoryRoot = (Join-Path (Join-Path $PSScriptRoot '..') '..')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($env:RUN_DEMO1_CLOUD_INTEGRATION -ne 'true') {
    throw 'Set RUN_DEMO1_CLOUD_INTEGRATION=true to allow Demo 1 cloud provisioning.'
}

$required = @{
    AZURE_AI_FOUNDRY_ENDPOINT = $ProjectEndpoint
    DEMO1_SEARCH_ENDPOINT = $SearchEndpoint
    DEMO1_STORAGE_ACCOUNT_NAME = $StorageAccountName
    DEMO1_STORAGE_ACCOUNT_BLOB_HOST = $StorageAccountBlobHost
    DEMO1_STORAGE_ACCOUNT_RESOURCE_ID = $StorageAccountResourceId
    DEMO1_KNOWLEDGE_CONTAINER_NAME = $KnowledgeContainerName
    DEMO1_KNOWLEDGE_SOURCE_NAME = $KnowledgeSourceName
    AZURE_AI_SERVICES_ENDPOINT = $AiServicesEndpoint
    COUNCIL_FAST_MODEL = $ModelDeployment
    EMBEDDING_DEPLOYMENT_NAME = $EmbeddingDeployment
}
$missing = @($required.GetEnumerator() | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.Value) })
if ($missing.Count -gt 0) {
    throw "Missing required environment values: $($missing.Name -join ', ')"
}

$root = [System.IO.Path]::GetFullPath($RepositoryRoot)
Import-Module (Join-Path $root 'scripts\ground\Demo1AgentPublisher.psm1') -Force
$citationIdentityFullPath = [string]::IsNullOrWhiteSpace($CitationIdentityPath) `
    ? (Join-Path $root '.azure\demo1-citation-identity.json') `
    : [System.IO.Path]::GetFullPath(
    [System.IO.Path]::IsPathRooted($CitationIdentityPath) `
        ? $CitationIdentityPath `
        : (Join-Path $root $CitationIdentityPath)
)
& (Join-Path $root 'scripts\ground\Sync-ManagingPublicMoney.ps1') -Mode Validate -RepositoryRoot $root

function Get-ValidatedServiceUri {
    param(
        [Parameter(Mandatory)]
        [string]$Value,
        [Parameter(Mandatory)]
        [string]$Name,
        [Parameter(Mandatory)]
        [string]$HostSuffix
    )

    $uri = [Uri]$Value
    if ($uri.Scheme -ne 'https' -or
        -not $uri.Host.EndsWith($HostSuffix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name must use HTTPS on a $HostSuffix host."
    }

    return $uri.AbsoluteUri.TrimEnd('/')
}

function Get-OptionalProperty {
    param(
        [object]$InputObject,
        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($null -eq $InputObject) {
        return $null
    }

    $property = $InputObject.PSObject.Properties[$Name]
    return $null -eq $property ? $null : $property.Value
}

function Get-PinnedFileCitationIds {
    param(
        [Parameter(Mandatory)]
        [object]$Response,
        [Parameter(Mandatory)]
        [string]$Filename
    )

    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($output in @((Get-OptionalProperty $Response 'output'))) {
        if (([string](Get-OptionalProperty $output 'type')) -ne 'message') {
            continue
        }

        foreach ($content in @((Get-OptionalProperty $output 'content'))) {
            if (([string](Get-OptionalProperty $content 'type')) -ne 'output_text') {
                continue
            }

            foreach ($annotation in @((Get-OptionalProperty $content 'annotations'))) {
                if (([string](Get-OptionalProperty $annotation 'type')) -ne 'file_citation' -or
                    ([string](Get-OptionalProperty $annotation 'filename')) -cne $Filename) {
                    continue
                }

                $fileId = [string](Get-OptionalProperty $annotation 'file_id')
                if (-not [string]::IsNullOrWhiteSpace($fileId)) {
                    $null = $ids.Add($fileId)
                }
            }
        }
    }

    return @($ids | Sort-Object)
}

function Get-PinnedUrlCitationUris {
    param(
        [Parameter(Mandatory)]
        [object]$Response,
        [Parameter(Mandatory)]
        [string]$ExpectedUri
    )

    $uris = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($output in @((Get-OptionalProperty $Response 'output'))) {
        if (([string](Get-OptionalProperty $output 'type')) -ne 'message') {
            continue
        }

        foreach ($content in @((Get-OptionalProperty $output 'content'))) {
            if (([string](Get-OptionalProperty $content 'type')) -ne 'output_text') {
                continue
            }

            foreach ($annotation in @((Get-OptionalProperty $content 'annotations'))) {
                if (([string](Get-OptionalProperty $annotation 'type')) -ne 'url_citation') {
                    continue
                }

                $uri = [string](Get-OptionalProperty $annotation 'url')
                if ($uri -ceq $ExpectedUri) {
                    $null = $uris.Add($uri)
                }
            }
        }
    }

    return @($uris | Sort-Object)
}

function Get-KnowledgeSourceStatus {
    param(
        [Parameter(Mandatory)]
        [string]$SearchUri,
        [Parameter(Mandatory)]
        [string]$KnowledgeSourceName,
        [Parameter(Mandatory)]
        [string]$ApiVersion,
        [Parameter(Mandatory)]
        [hashtable]$Headers,
        [ValidateRange(1, 60)]
        [int]$RequestTimeoutSeconds = 30,
        [switch]$AllowNotFound
    )

    $escapedName = [Uri]::EscapeDataString($KnowledgeSourceName)
    $statusUri = "$SearchUri/knowledgesources('$escapedName')/status?api-version=$ApiVersion"
    $request = @{
        Method = 'Get'
        Uri = $statusUri
        Headers = $Headers
    }
    if ((Get-Command Invoke-RestMethod).Parameters.ContainsKey('OperationTimeoutSeconds')) {
        $request.ConnectionTimeoutSeconds = $RequestTimeoutSeconds
        $request.OperationTimeoutSeconds = $RequestTimeoutSeconds
    }
    else {
        $request.TimeoutSec = $RequestTimeoutSeconds
    }

    try {
        return Invoke-RestMethod @request
    }
    catch {
        $response = $_.Exception.Response
        if ($AllowNotFound -and $null -ne $response -and [int]$response.StatusCode -eq 404) {
            return $null
        }

        throw
    }
}

function Get-SynchronizationKey {
    param([object]$Status)

    $state = Get-OptionalProperty $Status 'lastSynchronizationState'
    if ($null -eq $state) {
        return $null
    }

    $startTime = [string](Get-OptionalProperty $state 'startTime')
    $endTime = [string](Get-OptionalProperty $state 'endTime')
    return "$startTime|$endTime"
}

function Wait-KnowledgeSourceIngestion {
    param(
        [Parameter(Mandatory)]
        [string]$SearchUri,
        [Parameter(Mandatory)]
        [string]$KnowledgeSourceName,
        [Parameter(Mandatory)]
        [string]$ApiVersion,
        [Parameter(Mandatory)]
        [hashtable]$Headers,
        [string]$BaselineSynchronizationKey,
        [Parameter(Mandatory)]
        [int]$TimeoutSeconds,
        [Parameter(Mandatory)]
        [int]$PollIntervalSeconds
    )

    $timer = [Diagnostics.Stopwatch]::StartNew()
    $lastStatus = $null
    do {
        $requestTimeoutSeconds = [Math]::Max(
            1,
            [Math]::Min(
                30,
                [Math]::Ceiling($TimeoutSeconds - $timer.Elapsed.TotalSeconds)
            )
        )
        $lastStatus = Get-KnowledgeSourceStatus `
            -SearchUri $SearchUri `
            -KnowledgeSourceName $KnowledgeSourceName `
            -ApiVersion $ApiVersion `
            -Headers $Headers `
            -RequestTimeoutSeconds ([int]$requestTimeoutSeconds) `
            -AllowNotFound

        $serviceError = Get-OptionalProperty $lastStatus 'error'
        if ($null -ne $serviceError) {
            throw "Knowledge source ingestion returned an error: $($serviceError | ConvertTo-Json -Depth 10 -Compress)"
        }

        $synchronizationStatus = [string](Get-OptionalProperty $lastStatus 'synchronizationStatus')
        if ($synchronizationStatus -match 'failed|error|deleting') {
            throw "Knowledge source ingestion entered the '$synchronizationStatus' state."
        }

        foreach ($stateName in @('currentSynchronizationState', 'lastSynchronizationState')) {
            $state = Get-OptionalProperty $lastStatus $stateName
            if ($null -eq $state) {
                continue
            }

            if ($stateName -eq 'lastSynchronizationState' -and
                (Get-SynchronizationKey $lastStatus) -eq $BaselineSynchronizationKey) {
                continue
            }

            $failedItems = Get-OptionalProperty $state 'itemsUpdatesFailed'
            if ($null -ne $failedItems -and [int]$failedItems -gt 0) {
                throw "Knowledge source ingestion reported $failedItems failed item(s) in $stateName."
            }

            $errors = @((Get-OptionalProperty $state 'errors') | Where-Object { $null -ne $_ })
            if ($errors.Count -gt 0) {
                $details = $errors | ConvertTo-Json -Depth 10 -Compress
                throw "Knowledge source ingestion reported errors in ${stateName}: $details"
            }

            $stateStatus = [string](Get-OptionalProperty $state 'status')
            if ($stateStatus -match 'failed|error|partialSuccess') {
                throw "Knowledge source ingestion entered the '$stateStatus' state in $stateName."
            }
        }

        $completedState = Get-OptionalProperty $lastStatus 'lastSynchronizationState'
        $endTime = [string](Get-OptionalProperty $completedState 'endTime')
        $processedItems = Get-OptionalProperty $completedState 'itemsUpdatesProcessed'
        $completedStatus = [string](Get-OptionalProperty $completedState 'status')
        $completedKey = Get-SynchronizationKey $lastStatus
        $isNewSynchronization = [string]::IsNullOrWhiteSpace($BaselineSynchronizationKey) -or
        $completedKey -ne $BaselineSynchronizationKey
        $isSuccessfulStatus = [string]::IsNullOrWhiteSpace($completedStatus) -or
        $completedStatus -match '^(success|succeeded|successful)$'
        if (-not [string]::IsNullOrWhiteSpace($endTime) -and
            $isNewSynchronization -and
            $isSuccessfulStatus) {
            if ($null -eq $processedItems -or [int]$processedItems -lt 1) {
                throw 'Knowledge source ingestion completed without processing the pinned corpus item.'
            }

            Write-Host "Knowledge source ingestion succeeded at $endTime."
            return
        }

        if ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            $remainingMilliseconds = [Math]::Max(
                1,
                [Math]::Min(
                    $PollIntervalSeconds * 1000,
                    ($TimeoutSeconds - $timer.Elapsed.TotalSeconds) * 1000
                )
            )
            Start-Sleep -Milliseconds ([int]$remainingMilliseconds)
        }
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)

    $statusSummary = $null -eq $lastStatus `
        ? 'No status was returned.' `
        : ($lastStatus | ConvertTo-Json -Depth 10 -Compress)
    throw "Knowledge source ingestion timed out after $TimeoutSeconds second(s). Last status: $statusSummary"
}

$catalog = Get-Content -LiteralPath (Join-Path $root 'config\ground\agent-definitions.v1.json') -Raw | ConvertFrom-Json
$sourceManifest = Get-Content -LiteralPath (Join-Path $root 'data\ground\v1\source-manifest.json') -Raw | ConvertFrom-Json
$base = Get-Content -LiteralPath (Join-Path $root $catalog.canonicalBaseInstructionsPath) -Raw
$foundryToken = (& az account get-access-token --scope https://ai.azure.com/.default --query accessToken -o tsv).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($foundryToken)) {
    throw 'Azure CLI could not acquire a Microsoft Foundry access token.'
}

$projectUri = Get-ValidatedServiceUri $ProjectEndpoint 'ProjectEndpoint' '.services.ai.azure.com'
$searchUri = Get-ValidatedServiceUri $SearchEndpoint 'SearchEndpoint' '.search.windows.net'
$aiServicesUri = Get-ValidatedServiceUri $AiServicesEndpoint 'AiServicesEndpoint' '.services.ai.azure.com'
$iq = ($catalog.definitions | Where-Object stage -eq 'ground').foundryIq
$source = $sourceManifest.sources[0]
$corpusPath = Join-Path $root "data\ground\v1\knowledge\$($source.expectedLocalFilename)"
if ($StorageAccountBlobHost -ne "$StorageAccountName.blob.core.windows.net") {
    throw 'DEMO1_STORAGE_ACCOUNT_BLOB_HOST does not match the deployed storage account.'
}
if ($KnowledgeSourceName -ne $iq.knowledgeSourceName -or
    $KnowledgeSourceName -ne $source.knowledgeSourceName) {
    throw 'DEMO1_KNOWLEDGE_SOURCE_NAME does not match the pinned knowledge source.'
}

if (-not $AgentsOnly) {
    $searchToken = (& az account get-access-token --scope https://search.azure.com/.default --query accessToken -o tsv).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($searchToken)) {
        throw 'Azure CLI could not acquire an Azure AI Search access token.'
    }

    & az storage blob upload `
        --account-name $StorageAccountName `
        --container-name $KnowledgeContainerName `
        --name $source.expectedLocalFilename `
        --file $corpusPath `
        --auth-mode login `
        --overwrite true `
        --only-show-errors | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw 'The pinned Managing Public Money upload failed.'
    }

    $searchHeaders = @{
        Authorization = "Bearer $searchToken"
        'Content-Type' = 'application/json'
    }
    $knowledgeSourcePayload = @{
        name = $iq.knowledgeSourceName
        description = 'Pinned April 2026 Managing Public Money GOV.UK publication.'
        kind = 'azureBlob'
        azureBlobParameters = @{
            connectionString = "ResourceId=$StorageAccountResourceId;"
            containerName = $KnowledgeContainerName
            isADLSGen2 = $false
            ingestionParameters = @{
                disableImageVerbalization = $true
                contentExtractionMode = 'minimal'
                embeddingModel = @{
                    kind = 'azureOpenAI'
                    azureOpenAIParameters = @{
                        resourceUri = $aiServicesUri
                        deploymentId = $EmbeddingDeployment
                        modelName = $EmbeddingDeployment
                    }
                }
            }
        }
    } | ConvertTo-Json -Depth 10
    $baselineStatus = Get-KnowledgeSourceStatus `
        -SearchUri $searchUri `
        -KnowledgeSourceName $iq.knowledgeSourceName `
        -ApiVersion $iq.apiVersion `
        -Headers $searchHeaders `
        -AllowNotFound
    $baselineSynchronizationKey = Get-SynchronizationKey $baselineStatus
    $deployedKnowledgeSource = Invoke-RestMethod `
        -Method Put `
        -Uri "$searchUri/knowledgesources/$($iq.knowledgeSourceName)?api-version=$($iq.apiVersion)" `
        -Headers $searchHeaders `
        -Body $knowledgeSourcePayload
    $knowledgeSourceWriteReturnedDefinition = $null -ne $deployedKnowledgeSource -and
    -not [string]::IsNullOrWhiteSpace([string](Get-OptionalProperty $deployedKnowledgeSource 'name'))
    $deployedKnowledgeSourceName = [string](Get-OptionalProperty $deployedKnowledgeSource 'name')
    if ([string]::IsNullOrWhiteSpace($deployedKnowledgeSourceName)) {
        $deployedKnowledgeSource = Invoke-RestMethod `
            -Method Get `
            -Uri "$searchUri/knowledgesources/$($iq.knowledgeSourceName)?api-version=$($iq.apiVersion)" `
            -Headers $searchHeaders
        $deployedKnowledgeSourceName = [string](Get-OptionalProperty $deployedKnowledgeSource 'name')
    }
    if ($deployedKnowledgeSourceName -ne $KnowledgeSourceName) {
        throw 'The Search service returned an unexpected knowledge-source identifier.'
    }

    $baselineLastSynchronization = Get-OptionalProperty $baselineStatus 'lastSynchronizationState'
    $baselineProcessed = Get-OptionalProperty $baselineLastSynchronization 'itemsUpdatesProcessed'
    $baselineFailed = Get-OptionalProperty $baselineLastSynchronization 'itemsUpdatesFailed'
    $baselineErrors = @(Get-OptionalProperty $baselineLastSynchronization 'errors')
    $canReuseSuccessfulIngestion = -not $knowledgeSourceWriteReturnedDefinition -and
    $null -ne $baselineLastSynchronization -and
    [int64]$baselineProcessed -gt 0 -and
    [int64]$baselineFailed -eq 0 -and
    $baselineErrors.Count -eq 0
    if ($canReuseSuccessfulIngestion) {
        Write-Host 'Knowledge source configuration is unchanged; reusing the completed pinned-source ingestion.'
    }
    else {
        Wait-KnowledgeSourceIngestion `
            -SearchUri $searchUri `
            -KnowledgeSourceName $iq.knowledgeSourceName `
            -ApiVersion $iq.apiVersion `
            -Headers $searchHeaders `
            -BaselineSynchronizationKey $baselineSynchronizationKey `
            -TimeoutSeconds $IngestionTimeoutSeconds `
            -PollIntervalSeconds $IngestionPollIntervalSeconds
    }

    $knowledgeBasePayload = @{
        name = $iq.knowledgeBaseName
        description = 'Pinned Managing Public Money evidence for the Demo 1 Ground agent.'
        knowledgeSources = @(
            @{ name = $iq.knowledgeSourceName }
        )
        models = @(
            @{
                kind = 'azureOpenAI'
                azureOpenAIParameters = @{
                    resourceUri = $aiServicesUri
                    deploymentId = $ModelDeployment
                    modelName = $ModelDeployment
                }
            }
        )
        retrievalReasoningEffort = @{ kind = [string]$iq.retrievalReasoningEffort }
        retrievalInstructions = "Use only knowledge source ID $KnowledgeSourceName and manifest record $($source.sourceId). Return no claim when the pinned source does not support it."
        outputMode = 'extractiveData'
    } | ConvertTo-Json -Depth 10
    Invoke-RestMethod `
        -Method Put `
        -Uri "$searchUri/knowledgebases/$($iq.knowledgeBaseName)?api-version=$($iq.apiVersion)" `
        -Headers $searchHeaders `
        -Body $knowledgeBasePayload | Out-Null
}
else {
    if (-not (Test-Path -LiteralPath $citationIdentityFullPath -PathType Leaf)) {
        throw 'AgentsOnly requires an existing pinned corpus citation identity.'
    }
    $existingIdentity = Get-Content -LiteralPath $citationIdentityFullPath -Raw | ConvertFrom-Json
    if ($existingIdentity.manifestSourceId -cne $source.sourceId -or
        $existingIdentity.filename -cne $source.expectedLocalFilename -or
        $existingIdentity.knowledgeSourceName -cne $KnowledgeSourceName) {
        throw 'AgentsOnly requires a citation identity matching the pinned corpus.'
    }
    $deployedKnowledgeSourceName = $KnowledgeSourceName
    Write-Host 'AgentsOnly: preserving existing storage, ingestion, knowledge source, and knowledge base.'
}

$headers = @{
    Authorization = "Bearer $foundryToken"
    'Content-Type' = 'application/json'
}
foreach ($definition in $catalog.definitions) {
    $instructions = $base.TrimEnd()
    $bodyDefinition = [ordered]@{
        kind = 'prompt'
        model = $ModelDeployment
        instructions = $instructions
        reasoning = @{
            effort = [string]$definition.reasoningEffort
        }
    }

    if ($definition.stage -eq 'ground') {
        $extension = Get-Content -LiteralPath (Join-Path $root $definition.instructionExtensionPath) -Raw
        $instructions = "$instructions`n`n$($extension.Trim())"
        $bodyDefinition.instructions = $instructions
        $definitionIq = $definition.foundryIq
        $bodyDefinition.tools = @(
            [ordered]@{
                type = 'mcp'
                server_label = 'managingPublicMoney'
                server_url = "$searchUri/knowledgebases/$($definitionIq.knowledgeBaseName)/mcp?api-version=$($definitionIq.apiVersion)"
                allowed_tools = @{
                    tool_names = @($definitionIq.toolName)
                }
                require_approval = 'never'
                project_connection_id = $definitionIq.connectionName
            }
        )
    }

    $payload = [ordered]@{
        name = $definition.name
        definition = $bodyDefinition
        description = $definition.displayName
        metadata = @{
            stage = $definition.stage
            managedBy = 'public-sector-agent-demos'
        }
    }
    $publishResult = Publish-Demo1NamedAgent `
        -ProjectUri $projectUri `
        -Headers $headers `
        -Payload $payload
    Write-Host "$($publishResult.Operation) $($publishResult.Name) version $($publishResult.Version)."
}

$groundAgentName = [string](($catalog.definitions | Where-Object stage -eq 'ground').name)
$probePayload = @{
    tool_choice = 'required'
    input = @(
        @{
            role = 'user'
            content = 'Using the pinned publication, state one of the four Managing Public Money standards in one line. Include its service citation.'
        }
    )
} | ConvertTo-Json -Depth 10
$probeResponse = Invoke-Demo1KnowledgeProbe -Request @{
    Method = 'Post'
    Uri = "$projectUri/agents/$([Uri]::EscapeDataString($groundAgentName))/endpoint/protocols/openai/responses?api-version=v1"
    Headers = $headers
    Body = $probePayload
}
$usedKnowledgeBase = @((Get-OptionalProperty $probeResponse 'output') | Where-Object {
        ([string](Get-OptionalProperty $_ 'type')) -ceq 'mcp_call' -and
        ([string](Get-OptionalProperty $_ 'name')) -ceq 'knowledge_base_retrieve'
    }).Count -gt 0
if (-not $usedKnowledgeBase) {
    throw 'The Ground agent corpus probe did not call knowledge_base_retrieve.'
}

$fileIds = @(Get-PinnedFileCitationIds `
        -Response $probeResponse `
        -Filename ([string]$source.expectedLocalFilename))
$pinnedBlobUri = "https://$StorageAccountBlobHost/$KnowledgeContainerName/$([Uri]::EscapeDataString(
    [string]$source.expectedLocalFilename))"
$approvedUris = @(Get-PinnedUrlCitationUris `
        -Response $probeResponse `
        -ExpectedUri $pinnedBlobUri)
if ($fileIds.Count -eq 0 -and $approvedUris.Count -eq 0) {
    throw 'The Ground agent corpus probe returned no citation for the pinned corpus.'
}

$identity = [ordered]@{
    schemaVersion = 1
    generatedAt = [DateTimeOffset]::UtcNow.ToString('O')
    agentName = $groundAgentName
    knowledgeSourceName = $deployedKnowledgeSourceName
    manifestSourceId = [string]$source.sourceId
    filename = [string]$source.expectedLocalFilename
    fileIds = $fileIds
    approvedUris = $approvedUris
} | ConvertTo-Json -Depth 5
$identityDirectory = Split-Path -Parent $citationIdentityFullPath
$null = New-Item -ItemType Directory -Path $identityDirectory -Force
Set-Content -LiteralPath $citationIdentityFullPath -Value $identity -Encoding utf8
Write-Host "Persisted the exact corpus citation identity to $citationIdentityFullPath."
Write-Output "DEMO1_CITATION_IDENTITY_PATH=$citationIdentityFullPath"
