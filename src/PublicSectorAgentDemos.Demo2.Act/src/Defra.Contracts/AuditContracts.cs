namespace Defra.Contracts.V1;

public enum AuditEventKind
{
    InputScreened,
    ToolRequested,
    ToolDenied,
    ToolCompleted,
    ApprovalRequested,
    ApprovalDecided,
    AnswerProduced
}

public sealed record SanitizedAuditEvent
{
    public SanitizedAuditEvent(
        string eventId,
        DateTimeOffset timestamp,
        CorrelationId correlationId,
        AuditEventKind kind,
        string demo,
        string agent,
        string stage,
        string tool,
        string decision,
        string policyVersion,
        string outcome,
        long durationMs,
        string inputHash,
        string? policyId = null,
        string? approvalRequestId = null,
        string? traceId = null)
    {
        EventId = ValidateIdentifier(eventId, nameof(eventId));
        if (timestamp == default)
        {
            throw new ArgumentException("Audit timestamp is required.", nameof(timestamp));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(durationMs);

        Timestamp = timestamp;
        CorrelationId = correlationId;
        Kind = kind;
        Demo = ValidateIdentifier(demo, nameof(demo));
        Agent = ValidateIdentifier(agent, nameof(agent));
        Stage = ValidateIdentifier(stage, nameof(stage));
        Tool = ValidateIdentifier(tool, nameof(tool));
        Decision = ValidateIdentifier(decision, nameof(decision));
        PolicyVersion = ValidateIdentifier(policyVersion, nameof(policyVersion));
        Outcome = ValidateIdentifier(outcome, nameof(outcome));
        DurationMs = durationMs;
        InputHash = ValidateSha256(inputHash, nameof(inputHash));
        PolicyId = ValidateOptionalIdentifier(policyId, nameof(policyId));
        ApprovalRequestId = ValidateOptionalIdentifier(approvalRequestId, nameof(approvalRequestId));
        TraceId = ValidateOptionalIdentifier(traceId, nameof(traceId));
    }

    public string EventId { get; }

    public DateTimeOffset Timestamp { get; }

    public CorrelationId CorrelationId { get; }

    public AuditEventKind Kind { get; }

    public string Demo { get; }

    public string Agent { get; }

    public string Stage { get; }

    public string Tool { get; }

    public string Decision { get; }

    public string PolicyVersion { get; }

    public string Outcome { get; }

    public long DurationMs { get; }

    public string InputHash { get; }

    public string? PolicyId { get; }

    public string? ApprovalRequestId { get; }

    public string? TraceId { get; }

    private static string ValidateIdentifier(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > 128 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.' and not ':' and not '/' and not '@' and not '+'))
        {
            throw new ArgumentException(
                "Audit identifiers must be 1-128 safe identifier characters.",
                parameterName);
        }

        return value;
    }

    private static string? ValidateOptionalIdentifier(string? value, string parameterName) =>
        value is null ? null : ValidateIdentifier(value, parameterName);

    private static string ValidateSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("InputHash must be a 64-character SHA-256 hexadecimal digest.", parameterName);
        }

        return value.ToLowerInvariant();
    }
}
