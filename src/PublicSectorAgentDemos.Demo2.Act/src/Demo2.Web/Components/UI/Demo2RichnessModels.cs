using Demo2.Web.Domain;

namespace Demo2.Web.Components.UI;

public enum WelfareScenarioPath
{
    Emergency,
    RepeatComplaint,
    StandardReview,
    LowPriority,
    ApprovalDispatch
}

public sealed record WelfareScenario(
    string Id,
    WelfareScenarioPath Path,
    string Title,
    string FarmReference,
    string ComplaintText,
    string ExpectedBehavior);

public static class WelfareScenarioCatalogue
{
    public static IReadOnlyList<WelfareScenario> All { get; } =
    [
        new(
            "emergency",
            WelfareScenarioPath.Emergency,
            "Emergency safeguard",
            string.Empty,
            "Flash flooding creates immediate danger. Temporary accommodation is full and transport support is urgently needed. The area reference needs confirmation.",
            "Expected: an emergency guardrail sets High urgency. Confirm the incident area before requesting a reservation."),
        new(
            "repeat",
            WelfareScenarioPath.RepeatComplaint,
            "Recurring unmet need",
            "AREA-1001",
            "Riverton has recurring unmet accommodation and transport needs after earlier flood situation reports. Review the latest capacity evidence and outstanding household support.",
            "Expected: repeated situation reports can require priority coordination. Earlier reports do not prove that current capacity is available."),
        new(
            "standard",
            WelfareScenarioPath.StandardReview,
            "Standard review",
            "AREA-1002",
            "Flood water is stable. Temporary accommodation has spare capacity and scheduled transport remains available. Review support coordination during normal operating hours.",
            "Starting point for standard coordination review. Evidence and guardrails can raise urgency and request reservation approval."),
        new(
            "low",
            WelfareScenarioPath.LowPriority,
            "Low priority",
            "AREA-1002",
            "Information only: a routine flood preparedness exercise checks accommodation and transport capacity. Confirm whether any additional support is required before requesting a package.",
            "Expected locally: Low urgency and Record and Close. Azure displays only the validated result."),
        new(
            "approval-dispatch",
            WelfareScenarioPath.ApprovalDispatch,
            "Riverton package approval",
            "AREA-1001",
            "Riverton flooding affects 180 households. Temporary accommodation becomes full at 18:00. An earlier report lists 90 rooms, but the latest report lists only 60 available. Check accommodation and transport capacity, then request incident commander approval for a support package.",
            "Expected: review WHAT and WHY before reserveSupportPackage. Local approval and Azure MCP approval both bind the exact package.")
    ];

    public static WelfareScenario GetRequired(string id) =>
        All.Single(scenario => string.Equals(scenario.Id, id, StringComparison.Ordinal));
}

public enum AssessmentStageKind
{
    InputScreening,
    Guardrails,
    FoundryAgent,
    McpApprovalAction,
    Persistence
}

public enum AssessmentStageStatus
{
    Waiting,
    Running,
    Complete,
    Intervention,
    Failed,
    Cancelled
}

public sealed class AssessmentStageState(
    AssessmentStageKind kind,
    string title,
    string waitingMessage)
{
    public AssessmentStageKind Kind { get; } = kind;

    public string Title { get; internal set; } = title;

    public string WaitingMessage { get; internal set; } = waitingMessage;

    public AssessmentStageStatus Status { get; internal set; } = AssessmentStageStatus.Waiting;

    public string Result { get; internal set; } = waitingMessage;

    public DateTimeOffset? StartedAt { get; internal set; }

    public DateTimeOffset? CompletedAt { get; internal set; }

    public TimeSpan? Elapsed => StartedAt is { } started && CompletedAt is { } completed
        ? completed - started
        : null;

    public string? ElapsedText => Elapsed is { } elapsed
        ? elapsed.TotalSeconds < 1
            ? $"{elapsed.TotalMilliseconds:0} ms"
            : $"{elapsed.TotalSeconds:0.0} s"
        : null;

    public void Reset()
    {
        Status = AssessmentStageStatus.Waiting;
        Result = WaitingMessage;
        StartedAt = null;
        CompletedAt = null;
    }
}

public sealed class WelfareAssessmentBoardState
{
    private readonly Dictionary<AssessmentStageKind, AssessmentStageState> _stages;
    private readonly HashSet<AssessmentEventKind> _guardrailEvents = [];
    private bool _providerConfigured;

    public WelfareAssessmentBoardState()
    {
        AssessmentStageState[] stages =
        [
            new(AssessmentStageKind.InputScreening, "Input screening", "Waiting for a flood situation report."),
            new(AssessmentStageKind.Guardrails, "Guardrails", "Waiting for screened input."),
            new(AssessmentStageKind.FoundryAgent, "Assessment provider", "Waiting for policy checks."),
            new(AssessmentStageKind.McpApprovalAction, "Approval/action", "No operational activity yet."),
            new(AssessmentStageKind.Persistence, "Persistence", "Waiting for a validated result.")
        ];
        Stages = stages;
        _stages = stages.ToDictionary(stage => stage.Kind);
    }

    public AssessmentExecutionProvider ExecutionProvider { get; private set; }

    public IReadOnlyList<AssessmentStageState> Stages { get; }

    public string CurrentActivity { get; private set; } = "Ready to assess.";

    public string CurrentDetail { get; private set; } =
        "Choose a scenario or enter a flood situation report, then run the assessment.";

    public int EvidenceCount { get; private set; }

    public int GuardrailCount => _guardrailEvents.Count;

    public bool IsComplete { get; private set; }

    public void Configure(AssessmentExecutionProvider executionProvider)
    {
        if (_providerConfigured && ExecutionProvider == executionProvider)
        {
            return;
        }

        ExecutionProvider = executionProvider;
        _providerConfigured = true;
        AssessmentStageState provider = _stages[AssessmentStageKind.FoundryAgent];
        AssessmentStageState action = _stages[AssessmentStageKind.McpApprovalAction];
        if (executionProvider == AssessmentExecutionProvider.AzureFoundry)
        {
            provider.Title = "Azure Foundry agent";
            provider.WaitingMessage = "Waiting for Azure Foundry invocation.";
            action.Title = "MCP approval/action";
            action.WaitingMessage = "No MCP tool activity yet.";
        }
        else
        {
            provider.Title = "Local deterministic assessment";
            provider.WaitingMessage = "Waiting for the local deterministic provider.";
            action.Title = "Local approval/action";
            action.WaitingMessage = "Waiting for the package approval decision.";
        }

        if (provider.Status == AssessmentStageStatus.Waiting)
        {
            provider.Result = provider.WaitingMessage;
        }

        if (action.Status == AssessmentStageStatus.Waiting)
        {
            action.Result = action.WaitingMessage;
        }
    }

    public void Start(
        DateTimeOffset timestamp,
        AssessmentExecutionProvider executionProvider)
    {
        Configure(executionProvider);
        foreach (AssessmentStageState stage in Stages)
        {
            stage.Reset();
        }

        _guardrailEvents.Clear();
        EvidenceCount = 0;
        IsComplete = false;
        SetRunning(AssessmentStageKind.InputScreening, timestamp, "Screening protected input.");
        SetActivity("Screening input", "Checking length, unsafe instructions, and personal-data controls.");
    }

    public void Apply(WelfareAssessmentEvent assessmentEvent)
    {
        if (assessmentEvent.ExecutionProvider is { } executionProvider)
        {
            Configure(executionProvider);
        }

        switch (assessmentEvent.Kind)
        {
            case AssessmentEventKind.Accepted:
                Complete(AssessmentStageKind.InputScreening, assessmentEvent.Timestamp, assessmentEvent.Message);
                SetRunning(AssessmentStageKind.Guardrails, assessmentEvent.Timestamp, "Evaluating deterministic policy.");
                SetActivity("Applying guardrails", assessmentEvent.Message);
                break;
            case AssessmentEventKind.EmergencyFloorApplied:
            case AssessmentEventKind.RepeatOffenderEscalated:
            case AssessmentEventKind.ContinuationFloorApplied:
                _guardrailEvents.Add(assessmentEvent.Kind);
                Intervene(AssessmentStageKind.Guardrails, assessmentEvent.Timestamp, assessmentEvent.Message);
                SetActivity("Guardrail intervention", assessmentEvent.Message);
                break;
            case AssessmentEventKind.AgentStarted:
                CompleteGuardrailsIfRunning(assessmentEvent.Timestamp);
                SetRunning(AssessmentStageKind.FoundryAgent, assessmentEvent.Timestamp, assessmentEvent.Message);
                SetActivity(
                    ExecutionProvider == AssessmentExecutionProvider.AzureFoundry ? "Azure Foundry agent running" : "Local deterministic assessment running",
                    assessmentEvent.Message);
                break;
            case AssessmentEventKind.AgentToolCallStarted:
                SetRunning(AssessmentStageKind.McpApprovalAction, assessmentEvent.Timestamp, assessmentEvent.Message);
                SetActivity(
                    ExecutionProvider == AssessmentExecutionProvider.AzureFoundry ? "Connected MCP tool running" : "Local action running",
                    assessmentEvent.Message);
                break;
            case AssessmentEventKind.McpApprovalRequested:
                Intervene(AssessmentStageKind.McpApprovalAction, assessmentEvent.Timestamp, assessmentEvent.Message);
                SetActivity("Human decision required", assessmentEvent.Message);
                break;
            case AssessmentEventKind.McpApprovalDecided:
                _guardrailEvents.Add(assessmentEvent.Kind);
                Intervene(AssessmentStageKind.McpApprovalAction, assessmentEvent.Timestamp, assessmentEvent.Message);
                SetActivity("Approval decided", assessmentEvent.Message);
                break;
            case AssessmentEventKind.AgentToolResultReceived:
            case AssessmentEventKind.DispatchCompleted:
                Complete(AssessmentStageKind.McpApprovalAction, assessmentEvent.Timestamp, assessmentEvent.Message);
                SetActivity("Tool result received", assessmentEvent.Message);
                break;
            case AssessmentEventKind.AgentResponseCompleted:
                SetRunning(AssessmentStageKind.FoundryAgent, assessmentEvent.Timestamp, assessmentEvent.Message);
                SetActivity("Validating response", assessmentEvent.Message);
                break;
            case AssessmentEventKind.AgentCompleted:
                Complete(AssessmentStageKind.FoundryAgent, assessmentEvent.Timestamp, assessmentEvent.Message);
                SetActivity("Response validated", assessmentEvent.Message);
                break;
            case AssessmentEventKind.PersistenceStarted:
                SetRunning(AssessmentStageKind.Persistence, assessmentEvent.Timestamp, assessmentEvent.Message);
                SetActivity("Persisting state", assessmentEvent.Message);
                break;
            case AssessmentEventKind.Persisted:
                Complete(AssessmentStageKind.Persistence, assessmentEvent.Timestamp, assessmentEvent.Message);
                SetActivity("State persisted", assessmentEvent.Message);
                break;
            case AssessmentEventKind.Completed:
                CompleteWaitingMcp(assessmentEvent.Timestamp);
                EvidenceCount = assessmentEvent.Outcome?.Evidence.Count ?? 0;
                IsComplete = true;
                SetActivity("Assessment complete", assessmentEvent.Outcome is { } outcome
                    ? $"{Format(outcome.Case.Urgency)} urgency · {Format(outcome.Case.Route)}"
                    : assessmentEvent.Message);
                break;
        }
    }

    public void MarkApprovalPending(DateTimeOffset timestamp, string toolName)
    {
        Intervene(
            AssessmentStageKind.McpApprovalAction,
            timestamp,
            $"{toolName} is paused before execution.");
        SetActivity("Human decision required", $"Review the pending {toolName} request.");
    }

    public void MarkApprovalDecision(DateTimeOffset timestamp, bool approved)
    {
        string actionKind = ExecutionProvider == AssessmentExecutionProvider.AzureFoundry
            ? "MCP action"
            : "local reservation";
        string provider = ExecutionProvider == AssessmentExecutionProvider.AzureFoundry
            ? "Azure Foundry agent"
            : "local deterministic assessment";
        Intervene(
            AssessmentStageKind.McpApprovalAction,
            timestamp,
            approved ? $"Package reservation approved; {provider} is resuming." : $"Package reservation rejected; {provider} is resuming without a reservation.");
        SetActivity("Assessment resuming", approved ? $"Approved {actionKind} is continuing." : $"Rejected {actionKind} will not execute.");
    }

    public void Fail(DateTimeOffset timestamp, string detail)
    {
        Terminate(timestamp, AssessmentStageStatus.Failed, detail);
        SetActivity("Assessment failed", detail);
    }

    public void Cancel(DateTimeOffset timestamp, string detail)
    {
        Terminate(timestamp, AssessmentStageStatus.Cancelled, detail);
        SetActivity("Assessment cancelled", detail);
    }

    private void CompleteGuardrailsIfRunning(DateTimeOffset timestamp)
    {
        AssessmentStageState stage = _stages[AssessmentStageKind.Guardrails];
        if (stage.Status == AssessmentStageStatus.Running)
        {
            Complete(AssessmentStageKind.Guardrails, timestamp, "No deterministic escalation changed the input assessment.");
        }
        else if (stage.CompletedAt is null)
        {
            stage.CompletedAt = timestamp;
        }
    }

    private void CompleteWaitingMcp(DateTimeOffset timestamp)
    {
        AssessmentStageState stage = _stages[AssessmentStageKind.McpApprovalAction];
        if (stage.Status == AssessmentStageStatus.Waiting)
        {
            stage.StartedAt = timestamp;
            string detail = ExecutionProvider == AssessmentExecutionProvider.AzureFoundry
                ? "No MCP approval or action was requested."
                : "No reservation was requested.";
            Complete(AssessmentStageKind.McpApprovalAction, timestamp, detail);
        }
    }

    private void SetRunning(AssessmentStageKind kind, DateTimeOffset timestamp, string result)
    {
        AssessmentStageState stage = _stages[kind];
        stage.StartedAt ??= timestamp;
        stage.Status = AssessmentStageStatus.Running;
        stage.Result = result;
    }

    private void Complete(AssessmentStageKind kind, DateTimeOffset timestamp, string result)
    {
        AssessmentStageState stage = _stages[kind];
        stage.StartedAt ??= timestamp;
        stage.CompletedAt = timestamp;
        stage.Status = AssessmentStageStatus.Complete;
        stage.Result = result;
    }

    private void Intervene(AssessmentStageKind kind, DateTimeOffset timestamp, string result)
    {
        AssessmentStageState stage = _stages[kind];
        if (stage.CompletedAt is not null)
        {
            stage.StartedAt = timestamp;
        }
        else
        {
            stage.StartedAt ??= timestamp;
        }

        stage.CompletedAt = null;
        stage.Status = AssessmentStageStatus.Intervention;
        stage.Result = result;
    }

    private void Terminate(
        DateTimeOffset timestamp,
        AssessmentStageStatus terminalStatus,
        string detail)
    {
        List<AssessmentStageState> terminalStages =
        [
            .. Stages.Where(stage =>
                stage.Status is AssessmentStageStatus.Running or AssessmentStageStatus.Intervention)
        ];
        if (terminalStages.Count == 0 && !IsComplete)
        {
            AssessmentStageState provider = _stages[AssessmentStageKind.FoundryAgent];
            AssessmentStageState persistence = _stages[AssessmentStageKind.Persistence];
            AssessmentStageState? next =
                provider.Status == AssessmentStageStatus.Complete &&
                persistence.Status == AssessmentStageStatus.Waiting
                    ? persistence
                    : Stages.FirstOrDefault(stage => stage.Status == AssessmentStageStatus.Waiting);
            if (next is not null)
            {
                terminalStages.Add(next);
            }
        }

        foreach (AssessmentStageState stage in terminalStages)
        {
            stage.StartedAt ??= timestamp;
            stage.CompletedAt = timestamp;
            stage.Status = terminalStatus;
            stage.Result = detail;
        }

        IsComplete = false;
    }

    private void SetActivity(string activity, string detail)
    {
        CurrentActivity = activity;
        CurrentDetail = detail;
    }

    private static string Format<T>(T value) where T : struct, Enum =>
        FloodSupportCaptions.Format(value);
}
