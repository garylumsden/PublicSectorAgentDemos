using Defra.Contracts.FloodSupport;
using System.Text.Json.Serialization;
using Defra.Contracts.V1;

namespace Demo2.Web.Domain;

public static class Demo2SyntheticOperator
{
    public static Guid ObjectId { get; } = new("00000000-0000-0000-0000-000000000002");
}

public enum WelfareUrgency
{
    Unclassified = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

public enum WelfareRoute
{
    RequestClarification,
    RecordAndClose,
    [JsonStringEnumMemberName("StandardSupportReview")] StandardInspectorReview,
    [JsonStringEnumMemberName("PrioritySupportReview")] PriorityInspectorReview,
    ImmediateHumanEscalation
}

public enum WelfareCaseStatus
{
    Assessed,
    PendingApproval,
    DispatchApproved,
    DispatchDenied,
    VetDispatched
}

public sealed record WelfareAssessmentCommand(
    string CaseId,
    string? FarmReference,
    string ComplaintText,
    DateTimeOffset ReceivedAt,
    Guid OwnerObjectId)
{
    public string ReservationGeneration { get; init; } = "initial";
}

public sealed record AgentAssessmentRequest(
    string CaseId,
    string? FarmReference,
    string SanitizedComplaint,
    DateTimeOffset ReceivedAt,
    int RepeatComplaintWindowDays,
    WelfareUrgency MinimumUrgency,
    WelfareRoute? RequiredRoute,
    string DispatchActionRequestId,
    string IncidentReference,
    string? ConversationId,
    string? PreviousResponseId)
{
    public string ReservationGeneration { get; init; } = "initial";
}

public sealed record AgentAssessment(
    WelfareUrgency Urgency,
    WelfareRoute Route,
    string Summary,
    IReadOnlyList<string> Evidence,
    int RepeatComplaintCount,
    bool BreedContextUsed,
    bool ApprovalRecommended,
    string ResponseId,
    string ConversationId)
{
    public IReadOnlyList<ToolTraceItem> ToolTrace { get; init; } = [];

    public IReadOnlyList<ApprovalRequest> ApprovalRequests { get; init; } = [];

    public WelfareDispatchRecord? Dispatch { get; init; }
}

public sealed record WelfareDispatchRecord(
    string DispatchReference,
    DateTimeOffset DispatchedAt)
{
    public SupportReservationReceipt? Receipt { get; init; }
}

public sealed record WelfareCaseRecord(
    string CaseId,
    Guid OwnerObjectId,
    string? FarmReference,
    DateTimeOffset ReceivedAt,
    string ComplaintHash,
    WelfareUrgency Urgency,
    WelfareRoute Route,
    WelfareCaseStatus Status,
    string Summary,
    IReadOnlyList<string> PolicyReasons,
    bool AgentBypassed,
    string? AgentResponseId,
    string? ApprovalRequestId,
    int Version,
    DateTimeOffset UpdatedAt)
{
    public string? IssuedActionRequestId { get; init; }
    public bool SupportExecutionInProgress { get; init; }

    public bool SupportExecutionRequiresReconciliation { get; init; }
    public string? IncidentReference { get; init; }
    public SupportPackageRequest? SupportRequest { get; init; }
    public SupportPackageDefinition? PackageSnapshot { get; init; }
}

public sealed record ConversationRecord(
    string CaseId,
    string ConversationId,
    string? PreviousResponseId,
    DateTimeOffset UpdatedAt);

public sealed record WelfareApprovalRecord(
    string CaseId,
    int CaseVersion,
    ApprovalRequest Request,
    ApprovalDecision? Decision,
    string? DispatchReference,
    DateTimeOffset UpdatedAt)
{
    public bool IsPending => Decision is null;

    public SupportPackageRequest? SupportRequest { get; init; }
    public SupportPackageDefinition? PackageSnapshot { get; init; }
}

public sealed record InspectorDecisionRecord(
    string DecisionId,
    string CaseId,
    WelfareUrgency PreviousUrgency,
    WelfareUrgency OverrideUrgency,
    Guid InspectorObjectId,
    string ReasonCode,
    DateTimeOffset DecidedAt);

public sealed record WelfareAssessmentOutcome(
    WelfareCaseRecord Case,
    WelfareApprovalRecord? Approval,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<ToolTraceItem>? ToolTrace = null);

public enum AssessmentExecutionProvider
{
    LocalDeterministic,
    AzureFoundry
}

public enum AssessmentEventKind
{
    Accepted,
    EmergencyFloorApplied,
    AgentStarted,
    AgentToolCallStarted,
    McpApprovalRequested,
    AgentToolResultReceived,
    AgentResponseCompleted,
    AgentCompleted,
    RepeatOffenderEscalated,
    ContinuationFloorApplied,
    McpApprovalDecided,
    DispatchCompleted,
    Persisted,
    PersistenceStarted,
    Completed
}

public sealed record WelfareAssessmentEvent(
    AssessmentEventKind Kind,
    string Message,
    DateTimeOffset Timestamp,
    WelfareAssessmentOutcome? Outcome = null,
    AssessmentExecutionProvider? ExecutionProvider = null,
    bool? ApprovalGranted = null);

public sealed record InputGuardResult(
    bool IsAllowed,
    string Code,
    string SanitizedText,
    string InputHash);

public sealed record EmergencyPolicyResult(
    bool IsEmergency,
    int Score,
    IReadOnlyList<string> MatchedSignals);

public sealed record PostModelPolicyResult(
    WelfareUrgency Urgency,
    WelfareRoute Route,
    IReadOnlyList<string> Reasons,
    bool RepeatEscalated,
    bool ContinuationFloorApplied);
