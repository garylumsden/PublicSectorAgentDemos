namespace Defra.Contracts.V1;

public enum ApprovalOutcome
{
    Approved,
    Denied
}

public sealed record ApprovalRequest(
    string RequestId,
    CorrelationId CorrelationId,
    string ToolName,
    IReadOnlyList<string> ArgumentNames,
    string ReasonCode,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt);

public sealed record ApprovalDecision
{
    public ApprovalDecision(
        string requestId,
        CorrelationId correlationId,
        string toolName,
        ApprovalOutcome outcome,
        Guid decidedByObjectId,
        string decisionCode,
        DateTimeOffset decidedAt)
    {
        if (decidedByObjectId == Guid.Empty)
        {
            throw new ArgumentException("Deciding Entra object ID cannot be empty.", nameof(decidedByObjectId));
        }

        RequestId = requestId;
        CorrelationId = correlationId;
        ToolName = toolName;
        Outcome = outcome;
        DecidedByObjectId = decidedByObjectId;
        DecisionCode = decisionCode;
        DecidedAt = decidedAt;
    }

    public string RequestId { get; init; }

    public CorrelationId CorrelationId { get; init; }

    public string ToolName { get; init; }

    public ApprovalOutcome Outcome { get; init; }

    public Guid DecidedByObjectId { get; }

    public string DecisionCode { get; init; }

    public DateTimeOffset DecidedAt { get; init; }
}
