using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Defra.Audit;
using Defra.Contracts.V1;
using Defra.Observability;
using Defra.Policy;
using Defra.Tools.Mcp.Contracts;
using Defra.Tools.Mcp.Providers;
using ModelContextProtocol.Protocol;

namespace Defra.Tools.Mcp;

public sealed class McpToolExecutor(
    PolicyConfiguration policy,
    PolicyScreener policyScreener,
    IAppendOnlyAuditWriter auditWriter,
    TimeProvider timeProvider,
    ILogger<McpToolExecutor> logger)
{
    private const string DecisionBoundary =
        "Tool output records evidence, provenance and approval state. Legal, statutory and enforcement status remains pending until authorised officer confirmation.";

    private readonly PolicyConfiguration _policy = policy;
    private readonly PolicyScreener _policyScreener = policyScreener;
    private readonly IAppendOnlyAuditWriter _auditWriter = auditWriter;
    private readonly TimeProvider _timeProvider = timeProvider;
    private readonly ILogger<McpToolExecutor> _logger = logger;

    public async ValueTask<CallToolResult> ExecuteAsync<TInput, TOutput>(
        string toolName,
        TInput? input,
        Func<TInput?, ToolValidationFailure?> validate,
        Func<TInput, CancellationToken, ValueTask<ProviderResult<TOutput>>> operation,
        CancellationToken cancellationToken)
        where TInput : class
        where TOutput : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(validate);
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();

        CorrelationId correlationId = CorrelationId.Create();
        DateTimeOffset startedAt = _timeProvider.GetUtcNow();
        string serializedInput;
        try
        {
            serializedInput = JsonSerializer.Serialize(input, McpJson.SerializerOptions);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(
                exception,
                "MCP tool {ToolName} input serialization failed. Correlation {CorrelationId}.",
                toolName,
                correlationId.Value);
            return CreateErrorResult<TOutput>(
                correlationId,
                "input.serialization_failed",
                "Tool input rejected",
                "The typed tool input could not be serialized for safety screening.",
                isRetryable: false);
        }

        string inputHash = HashInput(toolName, serializedInput);
        using Activity? activity = DefraActivities.StartToolCall(
            correlationId,
            toolName,
            Guid.NewGuid().ToString("N"));
        DefraActivities.SetPolicy(activity, _policy.PolicyId, _policy.Version);

        if (!await TryAppendAuditAsync(
                CreateAuditEvent(
                    correlationId,
                    AuditEventKind.ToolRequested,
                    toolName,
                    "screen",
                    "pending",
                    startedAt,
                    inputHash,
                    activity),
                cancellationToken).ConfigureAwait(false))
        {
            activity?.SetStatus(ActivityStatusCode.Error, "audit.unavailable");
            return CreateAuditUnavailableResult<TOutput>(correlationId);
        }

        PolicyDecision policyDecision = _policyScreener.ScreenToolArguments(
            toolName,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["request"] = serializedInput
            });
        DefraActivities.SetDecision(activity, policyDecision.Code);

        if (!policyDecision.IsAllowed)
        {
            activity?.SetStatus(ActivityStatusCode.Error, policyDecision.Code);
            bool audited = await TryAppendAuditAsync(
                CreateAuditEvent(
                    correlationId,
                    AuditEventKind.ToolDenied,
                    toolName,
                    policyDecision.Code,
                    "policy-denied",
                    startedAt,
                    inputHash,
                    activity),
                cancellationToken).ConfigureAwait(false);
            return audited
                ? CreateErrorResult<TOutput>(
                    correlationId,
                    policyDecision.Code,
                    "Tool input rejected",
                    "Tool arguments were rejected by configured safety policy.",
                    isRetryable: false)
                : CreateAuditUnavailableResult<TOutput>(correlationId);
        }

        ToolValidationFailure? validationFailure;
        try
        {
            validationFailure = validate(input);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "MCP tool {ToolName} validation failed closed. Correlation {CorrelationId}.",
                toolName,
                correlationId.Value);
            validationFailure = new(
                "validation.failed_closed",
                "Tool input could not be validated and was rejected.");
        }

        if (validationFailure is not null)
        {
            DefraActivities.SetDecision(activity, validationFailure.Code);
            activity?.SetStatus(ActivityStatusCode.Error, validationFailure.Code);
            bool audited = await TryAppendAuditAsync(
                CreateAuditEvent(
                    correlationId,
                    AuditEventKind.ToolDenied,
                    toolName,
                    validationFailure.Code,
                    "validation-failed",
                    startedAt,
                    inputHash,
                    activity),
                cancellationToken).ConfigureAwait(false);
            return audited
                ? CreateErrorResult<TOutput>(
                    correlationId,
                    validationFailure.Code,
                    "Tool input rejected",
                    validationFailure.SafeDetail,
                    isRetryable: false)
                : CreateAuditUnavailableResult<TOutput>(correlationId);
        }

        try
        {
            ProviderResult<TOutput> providerResult =
                await operation(input!, cancellationToken).ConfigureAwait(false);
            bool audited = await TryAppendAuditAsync(
                CreateAuditEvent(
                    correlationId,
                    AuditEventKind.ToolCompleted,
                    toolName,
                    "allowed",
                    "succeeded",
                    startedAt,
                    inputHash,
                    activity),
                cancellationToken).ConfigureAwait(false);
            if (!audited)
            {
                activity?.SetStatus(ActivityStatusCode.Error, "audit.unavailable");
                return CreateAuditUnavailableResult<TOutput>(correlationId);
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
            return CreateSuccessResult(correlationId, providerResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SyntheticProviderException exception)
        {
            _logger.LogWarning(
                exception,
                "MCP tool {ToolName} provider rejected the request with code {ErrorCode}. Correlation {CorrelationId}.",
                toolName,
                exception.Code,
                correlationId.Value);
            DefraActivities.SetDecision(activity, exception.Code);
            activity?.SetStatus(
                exception.IsProtocolError ? ActivityStatusCode.Error : ActivityStatusCode.Ok,
                exception.IsProtocolError ? exception.Code : null);
            bool audited = await TryAppendAuditAsync(
                CreateAuditEvent(
                    correlationId,
                    AuditEventKind.ToolCompleted,
                    toolName,
                    exception.Code,
                    "provider-rejected",
                    startedAt,
                    inputHash,
                    activity),
                cancellationToken).ConfigureAwait(false);
            return audited
                ? exception.IsProtocolError
                    ? CreateErrorResult<TOutput>(
                        correlationId,
                        exception.Code,
                        "Domain data unavailable",
                        exception.SafeDetail,
                        isRetryable: false)
                    : CreateDomainFailureResult<TOutput>(
                        correlationId,
                        exception.Code,
                        "Domain data unavailable",
                        exception.SafeDetail,
                        isRetryable: false)
                : CreateAuditUnavailableResult<TOutput>(correlationId);
        }
        catch (Exception exception)
        {
            const string code = "tool.execution_failed";
            _logger.LogError(
                exception,
                "MCP tool {ToolName} execution failed. Correlation {CorrelationId}.",
                toolName,
                correlationId.Value);
            DefraActivities.SetDecision(activity, code);
            activity?.SetStatus(ActivityStatusCode.Error, code);
            bool audited = await TryAppendAuditAsync(
                CreateAuditEvent(
                    correlationId,
                    AuditEventKind.ToolCompleted,
                    toolName,
                    code,
                    "failed",
                    startedAt,
                    inputHash,
                    activity),
                cancellationToken).ConfigureAwait(false);
            return audited
                ? CreateErrorResult<TOutput>(
                    correlationId,
                    code,
                    "Tool execution failed",
                    "The tool failed closed without exposing internal details.",
                    isRetryable: true)
                : CreateAuditUnavailableResult<TOutput>(correlationId);
        }
    }

    private async ValueTask<bool> TryAppendAuditAsync(
        SanitizedAuditEvent auditEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            await _auditWriter.AppendAsync(auditEvent, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "MCP audit append failed for tool {ToolName}. Correlation {CorrelationId}.",
                auditEvent.Tool,
                auditEvent.CorrelationId.Value);
            return false;
        }
    }

    private SanitizedAuditEvent CreateAuditEvent(
        CorrelationId correlationId,
        AuditEventKind kind,
        string toolName,
        string decision,
        string outcome,
        DateTimeOffset startedAt,
        string inputHash,
        Activity? activity)
    {
        DateTimeOffset timestamp = _timeProvider.GetUtcNow();
        long durationMs = Math.Max(0, (long)(timestamp - startedAt).TotalMilliseconds);
        return new(
            Guid.NewGuid().ToString("N"),
            timestamp,
            correlationId,
            kind,
            "mcp",
            "mcp-service",
            "tool-execution",
            toolName,
            decision,
            _policy.Version,
            outcome,
            durationMs,
            inputHash,
            policyId: _policy.PolicyId,
            traceId: activity?.TraceId.ToString());
    }

    private static CallToolResult CreateSuccessResult<TOutput>(
        CorrelationId correlationId,
        ProviderResult<TOutput> providerResult)
        where TOutput : class =>
        CreateCallToolResult(
            new McpToolResponse<TOutput>(
                McpToolCatalog.Version,
                correlationId,
                IsSuccess: true,
                providerResult.Data,
                providerResult.Evidence,
                Error: null,
                DecisionBoundary),
            isError: false);

    private static CallToolResult CreateAuditUnavailableResult<TOutput>(CorrelationId correlationId)
        where TOutput : class =>
        CreateErrorResult<TOutput>(
            correlationId,
            "audit.unavailable",
            "Tool unavailable",
            "The append-only audit trail was unavailable, so the tool failed closed.",
            isRetryable: true);

    private static CallToolResult CreateErrorResult<TOutput>(
        CorrelationId correlationId,
        string code,
        string title,
        string safeDetail,
        bool isRetryable)
        where TOutput : class =>
        CreateCallToolResult(
            new McpToolResponse<TOutput>(
                McpToolCatalog.Version,
                correlationId,
                IsSuccess: false,
                Data: null,
                Evidence: Array.Empty<EvidenceReference>(),
                new StructuredToolError(code, title, safeDetail, isRetryable, correlationId),
                DecisionBoundary),
            isError: true);

    private static CallToolResult CreateDomainFailureResult<TOutput>(
        CorrelationId correlationId,
        string code,
        string title,
        string safeDetail,
        bool isRetryable)
        where TOutput : class =>
        CreateCallToolResult(
            new McpToolResponse<TOutput>(
                McpToolCatalog.Version,
                correlationId,
                IsSuccess: false,
                Data: null,
                Evidence: Array.Empty<EvidenceReference>(),
                new StructuredToolError(code, title, safeDetail, isRetryable, correlationId),
                DecisionBoundary),
            isError: false);

    private static CallToolResult CreateCallToolResult<TOutput>(
        McpToolResponse<TOutput> response,
        bool isError)
        where TOutput : class
    {
        JsonElement structuredContent =
            JsonSerializer.SerializeToElement(response, McpJson.SerializerOptions);
        return new()
        {
            Content =
            [
                new TextContentBlock
                {
                    Text = JsonSerializer.Serialize(response, McpJson.SerializerOptions)
                }
            ],
            StructuredContent = structuredContent,
            IsError = isError
        };
    }

    private static string HashInput(string toolName, string serializedInput)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"{toolName}\n{serializedInput}");
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
