namespace Defra.Contracts.V1;

public enum ToolCallStatus
{
    Started,
    Succeeded,
    Failed,
    Denied
}

public sealed record StructuredToolError(
    string Code,
    string Title,
    string SafeDetail,
    bool IsRetryable,
    CorrelationId CorrelationId);

public sealed record ToolTraceItem(
    string CallId,
    CorrelationId CorrelationId,
    string ToolName,
    ToolCallStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    StructuredToolError? Error);
