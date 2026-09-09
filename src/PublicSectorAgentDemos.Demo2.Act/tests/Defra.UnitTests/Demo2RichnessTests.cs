using Defra.Contracts.V1;
using System.Text.Json;
using Demo2.Web.Agent;
using Demo2.Web.Components.UI;
using Demo2.Web.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Defra.UnitTests;

public sealed class Demo2RichnessTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ScenarioCatalogue_CoversFiveTruthfulEditablePaths()
    {
        Assert.Equal(5, WelfareScenarioCatalogue.All.Count);
        Assert.Equal(
            Enum.GetValues<WelfareScenarioPath>().Order(),
            WelfareScenarioCatalogue.All.Select(scenario => scenario.Path).Order());
        Assert.All(WelfareScenarioCatalogue.All, scenario =>
        {
            Assert.False(string.IsNullOrWhiteSpace(scenario.Id));
            Assert.True(scenario.ComplaintText.Length >= 20);
            Assert.False(string.IsNullOrWhiteSpace(scenario.ExpectedBehavior));
        });
        Assert.Contains(
            "Local approval and Azure MCP approval",
            WelfareScenarioCatalogue.GetRequired("approval-dispatch").ExpectedBehavior,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ComplaintForm_LoadScenarioStartsANewCase()
    {
        WelfareScenario scenario = WelfareScenarioCatalogue.GetRequired("approval-dispatch");
        ComplaintFormModel form = new()
        {
            CaseId = "FLOOD-OLD",
            FarmReference = "AREA-OLD",
            ComplaintText = "Existing situation report that must be replaced."
        };

        form.LoadScenario(scenario, "FLOOD-NEW");

        Assert.Equal("FLOOD-NEW", form.CaseId);
        Assert.Equal("AREA-1001", form.FarmReference);
        Assert.Equal(scenario.ComplaintText, form.ComplaintText);
    }

    [Fact]
    public void BoardReducer_MapsActualEventsAndFinalCounters()
    {
        WelfareAssessmentBoardState board = new();
        board.Start(Now, AssessmentExecutionProvider.AzureFoundry);
        board.Apply(Event(AssessmentEventKind.Accepted, "Input allowed.", 1));
        board.Apply(Event(AssessmentEventKind.EmergencyFloorApplied, "High floor applied.", 2));
        board.Apply(Event(AssessmentEventKind.AgentStarted, "Agent invoked.", 3));
        board.Apply(Event(AssessmentEventKind.McpApprovalRequested, "Approval required.", 4));
        board.Apply(Event(AssessmentEventKind.McpApprovalDecided, "Approval granted.", 5));
        board.Apply(Event(AssessmentEventKind.AgentToolResultReceived, "Tool result received.", 6));
        board.Apply(Event(AssessmentEventKind.AgentResponseCompleted, "Validating response.", 7));
        board.Apply(Event(AssessmentEventKind.AgentCompleted, "Response validated.", 8));
        board.Apply(Event(AssessmentEventKind.PersistenceStarted, "Saving state.", 9));
        board.Apply(Event(AssessmentEventKind.Persisted, "State persisted.", 10));
        board.Apply(new(
            AssessmentEventKind.Completed,
            "Complete.",
            Now.AddSeconds(11),
            Outcome()));

        Assert.True(board.IsComplete);
        Assert.Equal(3, board.EvidenceCount);
        Assert.Equal(2, board.GuardrailCount);
        Assert.Equal("Assessment complete", board.CurrentActivity);
        Assert.All(board.Stages, stage =>
            Assert.NotEqual(AssessmentStageStatus.Waiting, stage.Status));
        Assert.Equal(
            AssessmentStageStatus.Intervention,
            board.Stages.Single(stage =>
                stage.Kind == AssessmentStageKind.Guardrails).Status);
        Assert.NotNull(board.Stages[0].Elapsed);
    }

    [Fact]
    public void BoardConfiguration_UsesTruthfulProviderLabelsAndTraceTitles()
    {
        WelfareAssessmentBoardState board = new();

        board.Configure(AssessmentExecutionProvider.LocalDeterministic);

        Assert.Equal(
            "Local deterministic assessment",
            board.Stages.Single(stage => stage.Kind == AssessmentStageKind.FoundryAgent).Title);
        Assert.Equal(
            "Local approval/action",
            board.Stages.Single(stage => stage.Kind == AssessmentStageKind.McpApprovalAction).Title);
        AssessmentTimelineItem localItem = AssessmentTimelineItem.From(
            new(
                AssessmentEventKind.AgentStarted,
                "Local deterministic assessment started.",
                Now,
                ExecutionProvider: AssessmentExecutionProvider.LocalDeterministic),
            sequence: 1);
        Assert.Equal("Local deterministic assessment started", localItem.Title);

        board.Configure(AssessmentExecutionProvider.AzureFoundry);

        Assert.Equal(
            "Azure Foundry agent",
            board.Stages.Single(stage => stage.Kind == AssessmentStageKind.FoundryAgent).Title);
        Assert.Equal(
            "MCP approval/action",
            board.Stages.Single(stage => stage.Kind == AssessmentStageKind.McpApprovalAction).Title);
    }

    [Fact]
    public void TimelineItems_UseSequenceToDisambiguateSameTimestampEvents()
    {
        DateTimeOffset timestamp = new(2026, 7, 17, 18, 10, 36, TimeSpan.Zero);
        WelfareAssessmentEvent first = new(
            AssessmentEventKind.AgentToolCallStarted,
            "First tool update.",
            timestamp,
            ExecutionProvider: AssessmentExecutionProvider.AzureFoundry);
        WelfareAssessmentEvent second = first with { Message = "Second tool update." };

        AssessmentTimelineItem firstItem = AssessmentTimelineItem.From(first, sequence: 1);
        AssessmentTimelineItem secondItem = AssessmentTimelineItem.From(second, sequence: 2);

        Assert.NotEqual(firstItem.Id, secondItem.Id);
    }

    [Fact]
    public void DispatchApprovalPresentation_ExposesValidatedDecisionContext()
    {
        DateTimeOffset requestedAt = DateTimeOffset.UtcNow;
        AgentMcpApprovalRequest request = new(
            "approval-request",
            "reserveSupportPackage",
            new Dictionary<string, object?>
            {
                ["actionRequestId"] = "approval-1234567890abcdef1234567890abcdef",
                ["areaReference"] = "AREA-1001",
                ["incidentReference"] = "INC-12345678",
                ["packageId"] = "PKG-RIV-060",
                ["packageVersion"] = 1,
                ["justification"] = "Accommodation becomes full at 18:00.",
                ["reservationGeneration"] = "initial"
            },
            ["actionRequestId", "areaReference", "incidentReference", "packageId", "packageVersion", "justification", "reservationGeneration"],
            requestedAt)
        {
            ExpiresAt = requestedAt.AddMinutes(30)
        };

        Assert.True(DispatchApprovalPresentation.TryCreate(
            request,
            "AREA-1001",
            out DispatchApprovalPresentation? presentation));
        Assert.Equal("Accommodation becomes full at 18:00.", presentation!.Justification);
        Assert.Equal("PKG-RIV-060", presentation.Package.PackageId);
        Assert.Equal(8100m, presentation.Package.EstimatedCostGbp);
        Assert.Equal(7, presentation.ExactArguments.Count);
    }

    [Fact]
    public void DispatchApprovalPresentation_FailsClosedForMismatchedArea()
    {
        AgentMcpApprovalRequest request = new(
            "approval-request",
            "reserveSupportPackage",
            new Dictionary<string, object?>
            {
                ["actionRequestId"] = "approval-1234567890abcdef1234567890abcdef",
                ["areaReference"] = "AREA-1001",
                ["incidentReference"] = "INC-12345678",
                ["packageId"] = "PKG-RIV-060",
                ["packageVersion"] = 1,
                ["justification"] = "Accommodation becomes full at 18:00.",
                ["reservationGeneration"] = "initial"
            },
            ["actionRequestId", "areaReference", "incidentReference", "packageId", "packageVersion", "justification", "reservationGeneration"],
            DateTimeOffset.UtcNow)
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };

        Assert.False(DispatchApprovalPresentation.TryCreate(
            request,
            "AREA-1002",
            out _));
    }

    [Fact]
    public void DispatchApprovalPresentation_AcceptsTypedNestedRequestArguments()
    {
        JsonElement nested = JsonSerializer.SerializeToElement(new
        {
            actionRequestId = "approval-1234567890abcdef1234567890abcdef",
            areaReference = "AREA-1001",
            incidentReference = "INC-12345678",
            packageId = "PKG-RIV-060",
            packageVersion = 1,
            justification = "Accommodation becomes full at 18:00.",
            reservationGeneration = "initial"
        });
        AgentMcpApprovalRequest request = new(
            "approval-request",
            "reserveSupportPackage",
            new Dictionary<string, object?>
            {
                ["request"] = nested
            },
            ["actionRequestId", "areaReference", "incidentReference", "packageId", "packageVersion", "justification", "reservationGeneration"],
            DateTimeOffset.UtcNow)
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30)
        };

        Assert.True(DispatchApprovalPresentation.TryCreate(
            request,
            "AREA-1001",
            out DispatchApprovalPresentation? presentation));
        Assert.Equal("Accommodation becomes full at 18:00.", presentation!.Justification);
        Assert.Equal("request", Assert.Single(presentation.ExactArguments).Key);
    }

    [Fact]
    public void BoardFailure_TerminallyFailsEveryActiveStageAtActualTimestamp()
    {
        WelfareAssessmentBoardState board = new();
        board.Start(Now, AssessmentExecutionProvider.LocalDeterministic);
        board.Apply(Event(AssessmentEventKind.Accepted, "Input allowed.", 1));
        DateTimeOffset failedAt = Now.AddSeconds(4);

        board.Fail(failedAt, "Provider failed.");

        AssessmentStageState guardrails = board.Stages.Single(stage =>
            stage.Kind == AssessmentStageKind.Guardrails);
        Assert.Equal(AssessmentStageStatus.Failed, guardrails.Status);
        Assert.Equal(failedAt, guardrails.CompletedAt);
        Assert.DoesNotContain(board.Stages, stage =>
            stage.Status is AssessmentStageStatus.Running or AssessmentStageStatus.Intervention);
    }

    [Fact]
    public void BoardCancellation_TerminallyCancelsEveryActiveStageAtActualTimestamp()
    {
        WelfareAssessmentBoardState board = new();
        board.Start(Now, AssessmentExecutionProvider.LocalDeterministic);
        DateTimeOffset cancelledAt = Now.AddSeconds(2);

        board.Cancel(cancelledAt, "Operator cancelled.");

        AssessmentStageState input = board.Stages.Single(stage =>
            stage.Kind == AssessmentStageKind.InputScreening);
        Assert.Equal(AssessmentStageStatus.Cancelled, input.Status);
        Assert.Equal(cancelledAt, input.CompletedAt);
        Assert.DoesNotContain(board.Stages, stage =>
            stage.Status is AssessmentStageStatus.Running or AssessmentStageStatus.Intervention);
    }

    [Fact]
    public void BoardFailure_BetweenAgentCompletionAndPersistenceStart_FailsPersistence()
    {
        WelfareAssessmentBoardState board = BoardAtPersistenceGap();
        DateTimeOffset failedAt = Now.AddSeconds(6);

        board.Fail(failedAt, "Conversation persistence failed.");

        AssessmentStageState persistence = board.Stages.Single(stage =>
            stage.Kind == AssessmentStageKind.Persistence);
        Assert.Equal(AssessmentStageStatus.Failed, persistence.Status);
        Assert.Equal(failedAt, persistence.StartedAt);
        Assert.Equal(failedAt, persistence.CompletedAt);
        Assert.Equal("Conversation persistence failed.", persistence.Result);
    }

    [Fact]
    public void BoardCancellation_BetweenAgentCompletionAndPersistenceStart_CancelsPersistence()
    {
        WelfareAssessmentBoardState board = BoardAtPersistenceGap();
        DateTimeOffset cancelledAt = Now.AddSeconds(6);

        board.Cancel(cancelledAt, "Conversation persistence cancelled.");

        AssessmentStageState persistence = board.Stages.Single(stage =>
            stage.Kind == AssessmentStageKind.Persistence);
        Assert.Equal(AssessmentStageStatus.Cancelled, persistence.Status);
        Assert.Equal(cancelledAt, persistence.StartedAt);
        Assert.Equal(cancelledAt, persistence.CompletedAt);
        Assert.Equal("Conversation persistence cancelled.", persistence.Result);
    }

    [Theory]
    [InlineData(AssessmentEventKind.RepeatOffenderEscalated)]
    [InlineData(AssessmentEventKind.ContinuationFloorApplied)]
    public void PostProviderGuardrail_ReopensStageAtInterventionTimestamp(
        AssessmentEventKind interventionKind)
    {
        WelfareAssessmentBoardState board = new();
        board.Start(Now, AssessmentExecutionProvider.LocalDeterministic);
        board.Apply(Event(AssessmentEventKind.Accepted, "Input allowed.", 1));
        board.Apply(Event(AssessmentEventKind.AgentStarted, "Provider started.", 2));
        board.Apply(Event(AssessmentEventKind.AgentCompleted, "Provider validated.", 4));
        Assert.Equal(AssessmentStageStatus.Waiting, board.Stages.Single(stage =>
            stage.Kind == AssessmentStageKind.Persistence).Status);

        AssessmentStageState guardrails = board.Stages.Single(stage =>
            stage.Kind == AssessmentStageKind.Guardrails);
        Assert.Equal(Now.AddSeconds(2), guardrails.CompletedAt);
        DateTimeOffset interventionAt = Now.AddSeconds(7);

        board.Apply(new(
            interventionKind,
            "Urgency floor applied.",
            interventionAt,
            ExecutionProvider: AssessmentExecutionProvider.LocalDeterministic));

        Assert.Equal(AssessmentStageStatus.Intervention, guardrails.Status);
        Assert.Equal(interventionAt, guardrails.StartedAt);
        Assert.Null(guardrails.CompletedAt);
        Assert.Null(guardrails.Elapsed);

        board.Apply(Event(AssessmentEventKind.PersistenceStarted, "Saving final state.", 8));

        AssessmentStageState persistence = board.Stages.Single(stage =>
            stage.Kind == AssessmentStageKind.Persistence);
        Assert.Equal(AssessmentStageStatus.Running, persistence.Status);
        Assert.Equal(Now.AddSeconds(8), persistence.StartedAt);
    }

    [Fact]
    public void StructuredBuffer_WithholdsPartialJsonUntilStrictValidation()
    {
        StructuredAgentResponseBuffer buffer = new();
        buffer.Append("{\"urgency\":\"Medium\",\"route\":\"StandardSupportReview\",");

        Assert.Throws<AgentResponseFormatException>(
            () => buffer.Complete("response-partial", "conversation-1"));

        buffer.Append(
            "\"summary\":\"Accommodation support requires review.\",\"evidence\":[\"situation report\"]," +
            "\"repeatedUnmetNeedsCount\":0,\"sourceConflictPresent\":false,\"approvalRecommended\":false}");
        AgentAssessment assessment = buffer.Complete("response-final", "conversation-1");

        Assert.Equal(WelfareUrgency.Medium, assessment.Urgency);
        Assert.Equal(WelfareRoute.StandardInspectorReview, assessment.Route);
    }

    [Fact]
    public void StreamingProjector_EmitsOnlyAuthoritativeToolLifecycleUpdates()
    {
        AgentStreamingEventProjector projector = new();
        McpServerToolCallContent call = new("call-1", "getSituationReports", "flood-support");
        AgentResponseUpdate text = new(ChatRole.Assistant, "{partial-json");
        AgentResponseUpdate started = new(ChatRole.Assistant, [call]) { CreatedAt = Now };
        AgentResponseUpdate result = new(
            ChatRole.Tool,
            [new McpServerToolResultContent("call-1")]) { CreatedAt = Now.AddSeconds(1) };
        McpServerToolCallContent dispatch = new("call-2", "reserveSupportPackage", "flood-support");
        AgentResponseUpdate approval = new(
            ChatRole.Assistant,
            [new ToolApprovalRequestContent("approval-1", dispatch)])
        {
            CreatedAt = Now.AddSeconds(2)
        };

        Assert.Empty(projector.Project(text));
        AgentAssessmentUpdate callUpdate = Assert.Single(projector.Project(started));
        Assert.Equal(AgentAssessmentUpdateKind.ToolCallStarted, callUpdate.Kind);
        Assert.Empty(projector.Project(started));
        Assert.Equal(
            AgentAssessmentUpdateKind.ToolResultReceived,
            Assert.Single(projector.Project(result)).Kind);
        AgentAssessmentUpdate approvalUpdate = Assert.Single(projector.Project(approval));
        Assert.Equal(AgentAssessmentUpdateKind.ApprovalRequested, approvalUpdate.Kind);
        Assert.Equal("reserveSupportPackage", approvalUpdate.ToolName);
        Assert.DoesNotContain("partial-json", approvalUpdate.Message, StringComparison.Ordinal);
    }

    private static WelfareAssessmentBoardState BoardAtPersistenceGap()
    {
        WelfareAssessmentBoardState board = new();
        board.Start(Now, AssessmentExecutionProvider.LocalDeterministic);
        board.Apply(Event(AssessmentEventKind.Accepted, "Input allowed.", 1));
        board.Apply(Event(AssessmentEventKind.AgentStarted, "Provider started.", 2));
        board.Apply(Event(AssessmentEventKind.AgentCompleted, "Provider validated.", 4));
        return board;
    }

    private static WelfareAssessmentEvent Event(
        AssessmentEventKind kind,
        string message,
        int seconds) =>
        new(kind, message, Now.AddSeconds(seconds));

    private static WelfareAssessmentOutcome Outcome()
    {
        WelfareCaseRecord record = new(
            "FLOOD-BOARD-1",
            Guid.Parse("93ed346b-74a2-4627-aa33-26b7715fd453"),
            "AREA-1001",
            Now,
            "hash",
            WelfareUrgency.High,
            WelfareRoute.ImmediateHumanEscalation,
            WelfareCaseStatus.VetDispatched,
            "Immediate response required.",
            ["pre-model-emergency-high"],
            AgentBypassed: false,
            "response-1",
            "approval-1",
            1,
            Now.AddSeconds(10));
        return new(record, null, ["situation", "accommodation", "transport"], Array.Empty<ToolTraceItem>());
    }
}
