using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Defra.Contracts.FloodSupport;
using Demo2.Web.Agent;
using Demo2.Web.Domain;

namespace Demo2.Web.Components.UI;

public sealed class ComplaintFormModel
{
    [Required(ErrorMessage = "Enter an assessment reference.")]
    [RegularExpression(
        "^[A-Za-z0-9_-]{1,64}$",
        ErrorMessage = "Use 1–64 letters, numbers, hyphens, or underscores.")]
    public string CaseId { get; set; } = string.Empty;

    [RegularExpression(
        "^[A-Za-z0-9_-]{0,64}$",
        ErrorMessage = "Use up to 64 letters, numbers, hyphens, or underscores.")]
    public string FarmReference { get; set; } = string.Empty;

    [Required(ErrorMessage = "Enter a flood situation report.")]
    [StringLength(6_000, MinimumLength = 20, ErrorMessage = "Use 20 to 6000 characters for the situation report.")]
    public string ComplaintText { get; set; } = string.Empty;

    public void LoadScenario(WelfareScenario scenario, string caseId)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentException.ThrowIfNullOrWhiteSpace(caseId);

        CaseId = caseId;
        FarmReference = scenario.FarmReference;
        ComplaintText = scenario.ComplaintText;
    }
}

public sealed record DispatchApprovalPresentation(
    string AreaReference,
    string IncidentReference,
    SupportPackageDefinition Package,
    string Justification,
    string ActionRequestId,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<KeyValuePair<string, string>> ExactArguments)
{
    public static bool TryCreate(
        AgentMcpApprovalRequest request,
        string expectedAreaReference,
        out DispatchApprovalPresentation? presentation)
    {
        presentation = null;
        if (!string.Equals(request.ToolName, "reserveSupportPackage", StringComparison.Ordinal) ||
            request.ExpiresAt is not { } expiresAt ||
            request.RequestedAt > DateTimeOffset.UtcNow ||
            expiresAt <= request.RequestedAt ||
            expiresAt <= DateTimeOffset.UtcNow ||
            !FloodSupportCatalogue.TryParseApprovalArguments(request.Arguments, out SupportPackageRequest? parsed) ||
            parsed is null ||
            !FloodSupportCatalogue.ValidateRequest(parsed, expectedAreaReference, out SupportPackageDefinition? package) ||
            package is null)
        {
            return false;
        }

        presentation = new(
            parsed.AreaReference,
            parsed.IncidentReference,
            package,
            parsed.Justification,
            parsed.ActionRequestId,
            request.RequestedAt,
            expiresAt,
            request.Arguments
                .Select(argument => new KeyValuePair<string, string>(
                    argument.Key,
                    JsonSerializer.Serialize(argument.Value)))
                .ToArray());
        return true;
    }

    public bool MatchesCurrent(AgentMcpApprovalRequest request, string expectedAreaReference) =>
        TryCreate(request, expectedAreaReference, out DispatchApprovalPresentation? current) &&
        current is not null &&
        AreaReference == current.AreaReference &&
        IncidentReference == current.IncidentReference &&
        Package == current.Package &&
        Justification == current.Justification &&
        ActionRequestId == current.ActionRequestId &&
        RequestedAt == current.RequestedAt &&
        ExpiresAt == current.ExpiresAt &&
        ExactArguments.SequenceEqual(current.ExactArguments);

    public static bool TryGetRecordedPackage(
        WelfareCaseRecord caseRecord,
        WelfareApprovalRecord? approval,
        out SupportPackageRequest? request,
        out SupportPackageDefinition? package)
    {
        request = approval?.SupportRequest ?? caseRecord.SupportRequest;
        package = null;
        SupportPackageDefinition? snapshot = approval?.PackageSnapshot ?? caseRecord.PackageSnapshot;
        if (request is null || snapshot is null ||
            request != caseRecord.SupportRequest ||
            request.ActionRequestId != caseRecord.IssuedActionRequestId ||
            request.IncidentReference != caseRecord.IncidentReference ||
            (approval is not null &&
                (approval.CaseId != caseRecord.CaseId ||
                 approval.CaseVersion != caseRecord.Version ||
                 approval.Request.RequestId != caseRecord.ApprovalRequestId ||
                 approval.Request.ToolName != "reserveSupportPackage" ||
                 approval.SupportRequest is null || approval.PackageSnapshot is null)) ||
            !FloodSupportCatalogue.ValidateRequest(request, caseRecord.FarmReference ?? string.Empty, out var canonical) ||
            canonical is null ||
            !FloodSupportCatalogue.MatchesSnapshot(snapshot, canonical) ||
            caseRecord.PackageSnapshot is null ||
            !FloodSupportCatalogue.MatchesSnapshot(caseRecord.PackageSnapshot, canonical))
        {
            return false;
        }
        package = canonical;
        return true;
    }
}

public sealed record AssessmentTimelineItem(
    string Id,
    string Title,
    string Message,
    string Icon,
    string Tone,
    DateTimeOffset Timestamp)
{
    public static AssessmentTimelineItem From(
        WelfareAssessmentEvent assessmentEvent,
        long sequence)
    {
        bool azure = assessmentEvent.ExecutionProvider == AssessmentExecutionProvider.AzureFoundry;
        (string title, string icon, string tone) = assessmentEvent.Kind switch
        {
            AssessmentEventKind.Accepted => ("Input guard passed", "✓", "success"),
            AssessmentEventKind.EmergencyFloorApplied => ("Emergency guardrail intervened", "!", "warning"),
            AssessmentEventKind.AgentStarted => (azure ? "Azure Foundry agent started" : "Local deterministic assessment started", "→", "info"),
            AssessmentEventKind.AgentToolCallStarted => (azure ? "MCP tool started" : "Local action started", "→", "info"),
            AssessmentEventKind.McpApprovalRequested => (azure ? "MCP approval required" : "Local approval required", "◆", "warning"),
            AssessmentEventKind.AgentToolResultReceived => (azure ? "MCP tool result" : "Local action result", "✓", "success"),
            AssessmentEventKind.AgentResponseCompleted => (azure ? "Azure Foundry response complete" : "Local deterministic result complete", "→", "info"),
            AssessmentEventKind.AgentCompleted => (azure ? "Azure Foundry response validated" : "Local deterministic result validated", "✓", "success"),
            AssessmentEventKind.RepeatOffenderEscalated => ("Recurring unmet need escalated", "↑", "warning"),
            AssessmentEventKind.ContinuationFloorApplied => ("Urgency floor retained", "■", "warning"),
            AssessmentEventKind.McpApprovalDecided => (azure ? "MCP approval decided" : "Local approval decided", "◆", "warning"),
            AssessmentEventKind.DispatchCompleted => ("Reservation recorded", "✓", "success"),
            AssessmentEventKind.Persisted => ("State persisted", "✓", "success"),
            AssessmentEventKind.PersistenceStarted => ("State persistence started", "→", "info"),
            AssessmentEventKind.Completed => ("Assessment complete", "✓", "success"),
            _ => ("Workflow update", "•", "info")
        };

        return new(
            $"{sequence:D6}-{assessmentEvent.Kind}-{assessmentEvent.Timestamp.UtcTicks}",
            title,
            assessmentEvent.Message,
            icon,
            tone,
            assessmentEvent.Timestamp);
    }
}

public sealed record WorkflowTraceItem(string Name, string Status, string Detail, string Tone);

public sealed record InspectorConfirmation(string ReasonCode, DateTimeOffset ConfirmedAt);

public static class FloodSupportCaptions
{
    public static string Format<T>(T value) where T : struct, Enum => value switch
    {
        WelfareRoute.RequestClarification => "Request situation clarification",
        WelfareRoute.RecordAndClose => "Record and close",
        WelfareRoute.StandardInspectorReview => "Standard coordination review",
        WelfareRoute.PriorityInspectorReview => "Priority coordination review",
        WelfareRoute.ImmediateHumanEscalation => "Immediate incident commander review",
        WelfareCaseStatus.Assessed => "Assessed",
        WelfareCaseStatus.PendingApproval => "Awaiting reservation approval",
        WelfareCaseStatus.DispatchApproved => "Reservation approved",
        WelfareCaseStatus.DispatchDenied => "Reservation rejected",
        WelfareCaseStatus.VetDispatched => "Package reserved",
        _ => System.Text.RegularExpressions.Regex.Replace(value.ToString(), "([a-z])([A-Z])", "$1 $2")
    };

    public static string Reason(string reason) => reason switch
    {
        "repeat-complaint-escalation" or "repeat-complaint-threshold" or "repeat-offender-escalation"
            => "Recurring unmet need requires priority coordination",
        "pre-model-emergency-high" => "Emergency support need requires High urgency",
        "continuation-urgency-floor" => "Existing urgency cannot decrease during continuation",
        "ImmediateWelfareRisk" => "Immediate flood support need",
        _ => reason.Replace("-", " ", StringComparison.Ordinal).Replace("_", " ", StringComparison.Ordinal)
    };

    public static string ApprovalMode(bool azure) =>
        azure ? "Azure Foundry" : "Local simulation";

    public static string ApprovalDescription(bool azure) => azure
        ? "Foundry paused reserveSupportPackage before execution."
        : "The local workflow is waiting for your decision.";
}
