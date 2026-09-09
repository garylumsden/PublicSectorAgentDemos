using Defra.Audit;
using Defra.Contracts.V1;
using Defra.Policy;
using Demo2.Web.Agent;
using Demo2.Web.Configuration;
using Demo2.Web.Domain;
using Demo2.Web.Observability;
using Demo2.Web.Persistence;
using Demo2.Web.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Defra.UnitTests;

public sealed class Demo2UnitTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid OwnerObjectId =
        Guid.Parse("f35428f3-8c45-4f39-82b9-2569a89cfb91");

    [Fact]
    public async Task EmergencySignal_SetsHighBeforeAgentAndStillRunsAgent()
    {
        Demo2TestHarness harness = new();
        harness.Agent.Response = AgentResponse(
            WelfareUrgency.Low,
            WelfareRoute.RecordAndClose,
            repeatCount: 0);

        WelfareAssessmentOutcome outcome = await harness.AssessAsync(
            "CASE-EMERGENCY",
            "AREA-1001",
            "A flood displacement has accommodation reaches capacity and is in immediate danger.");

        Assert.Equal(1, harness.Agent.CallCount);
        Assert.False(outcome.Case.AgentBypassed);
        Assert.Equal(WelfareUrgency.High, outcome.Case.Urgency);
        Assert.Equal(WelfareRoute.ImmediateHumanEscalation, outcome.Case.Route);
        Assert.Contains("pre-model-emergency-high", outcome.Case.PolicyReasons);
        Assert.Null(outcome.Approval);
    }

    [Fact]
    public async Task LocalContract_ProjectsTruthfulProviderMetadataOnEveryEvent()
    {
        Demo2TestHarness harness = new();
        List<WelfareAssessmentEvent> events = [];

        await foreach (WelfareAssessmentEvent assessmentEvent in harness.AssessmentService.AssessAsync(
                           new WelfareAssessmentCommand(
                               "CASE-LOCAL-PROVIDER",
                               "AREA-1002",
                               "Several households need temporary accommodation and support review.",
                               Now,
                               OwnerObjectId)))
        {
            events.Add(assessmentEvent);
        }

        Assert.NotEmpty(events);
        Assert.All(events, assessmentEvent =>
            Assert.Equal(AssessmentExecutionProvider.LocalDeterministic, assessmentEvent.ExecutionProvider));
        WelfareAssessmentEvent started = Assert.Single(events, assessmentEvent =>
            assessmentEvent.Kind == AssessmentEventKind.AgentStarted);
        Assert.Equal("Local assessment started.", started.Message);
    }

    [Fact]
    public async Task RepeatOffender_IsRaisedToHighAfterAgentResponse()
    {
        Demo2TestHarness harness = new();
        harness.Agent.Response = AgentResponse(
            WelfareUrgency.Low,
            WelfareRoute.RecordAndClose,
            repeatCount: 2);

        WelfareAssessmentOutcome outcome = await harness.AssessAsync(
            "CASE-REPEAT",
            "AREA-1001",
            "Unmet accommodation and transport needs were reported again.");

        Assert.Equal(1, harness.Agent.CallCount);
        Assert.False(outcome.Case.AgentBypassed);
        Assert.Equal(WelfareUrgency.High, outcome.Case.Urgency);
        Assert.Equal(WelfareRoute.PriorityInspectorReview, outcome.Case.Route);
        Assert.Contains("repeated-unmet-needs-high", outcome.Case.PolicyReasons);
        Assert.Null(outcome.Approval);
    }

    [Fact]
    public async Task PersistenceStart_FollowsPostProviderGuardrailEvent()
    {
        Demo2TestHarness harness = new();
        harness.Agent.Response = AgentResponse(
            WelfareUrgency.Low,
            WelfareRoute.RecordAndClose,
            repeatCount: 2);
        List<WelfareAssessmentEvent> events = [];

        await foreach (WelfareAssessmentEvent assessmentEvent in harness.AssessmentService.AssessAsync(
                           new WelfareAssessmentCommand(
                               "CASE-PERSISTENCE-ORDER",
                               "AREA-1001",
                               "Unmet accommodation and transport needs were reported again.",
                               Now,
                               OwnerObjectId)))
        {
            events.Add(assessmentEvent);
        }

        AssessmentEventKind[] sequence = events.Select(assessmentEvent => assessmentEvent.Kind).ToArray();
        int agentCompleted = Array.IndexOf(sequence, AssessmentEventKind.AgentCompleted);
        int guardrail = Array.IndexOf(sequence, AssessmentEventKind.RepeatOffenderEscalated);
        int persistenceStarted = Array.IndexOf(sequence, AssessmentEventKind.PersistenceStarted);
        int persisted = Array.IndexOf(sequence, AssessmentEventKind.Persisted);

        Assert.True(agentCompleted >= 0);
        Assert.True(agentCompleted < guardrail);
        Assert.True(guardrail < persistenceStarted);
        Assert.True(persistenceStarted < persisted);
    }

    [Fact]
    public async Task BenignSourceContext_DoesNotEscalateLowAssessment()
    {
        Demo2TestHarness harness = new();
        harness.Agent.Response = AgentResponse(
            WelfareUrgency.Low,
            WelfareRoute.RecordAndClose,
            repeatCount: 1,
            breedContextUsed: true);

        WelfareAssessmentOutcome outcome = await harness.AssessAsync(
            "CASE-BENIGN",
            "AREA-1002",
            "Meadowfield information only: confirm available accommodation before a request.");

        Assert.Equal(WelfareUrgency.Low, outcome.Case.Urgency);
        Assert.Equal(WelfareRoute.RecordAndClose, outcome.Case.Route);
        Assert.Null(outcome.Approval);
        Assert.DoesNotContain("repeated-unmet-needs-high", outcome.Case.PolicyReasons);
    }

    [Fact]
    public async Task Continuation_CannotDowngradePreviousUrgency()
    {
        Demo2TestHarness harness = new();
        harness.Agent.Response = AgentResponse(
            WelfareUrgency.Medium,
            WelfareRoute.StandardInspectorReview,
            repeatCount: 0);
        _ = await harness.AssessAsync(
            "CASE-CONTINUATION",
            "AREA-1003",
            "Crowding at the temporary accommodation site requires review.");

        harness.Agent.Response = AgentResponse(
            WelfareUrgency.Low,
            WelfareRoute.RecordAndClose,
            repeatCount: 0);
        WelfareAssessmentOutcome continuation = await harness.AssessAsync(
            "CASE-CONTINUATION",
            "AREA-1003",
            "A follow-up suggests conditions may have improved.");

        Assert.Equal(WelfareUrgency.Medium, continuation.Case.Urgency);
        Assert.Equal(WelfareRoute.StandardInspectorReview, continuation.Case.Route);
        Assert.Contains("continuation-no-downgrade", continuation.Case.PolicyReasons);
        Assert.Equal(2, continuation.Case.Version);
    }

    [Fact]
    public async Task LocalMode_DoesNotCreateMcpDispatchApproval()
    {
        Demo2TestHarness harness = new();
        WelfareAssessmentOutcome outcome = await harness.AssessAsync(
            "CASE-DISPATCH",
            "AREA-1001",
            "A flood displacement has accommodation reaches capacity.");
        InvalidOperationException blocked = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.AdminWorkflow.DispatchVetAsync(outcome.Case.CaseId));
        Assert.Contains("approval", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(outcome.Approval);
        Assert.Equal(WelfareCaseStatus.Assessed, outcome.Case.Status);
    }

    [Fact]
    public async Task AdminOverride_PersistsDecision()
    {
        Demo2TestHarness harness = new();
        WelfareAssessmentOutcome outcome = await harness.AssessAsync(
            "CASE-OVERRIDE",
            "AREA-1002",
            "Meadowfield information only: confirm available support.");

        InspectorDecisionRecord decision = await harness.AdminWorkflow.OverrideUrgencyAsync(
            outcome.Case.CaseId,
            WelfareUrgency.High,
            "inspector-evidence");

        Assert.Equal(WelfareUrgency.Low, decision.PreviousUrgency);
        Assert.Equal(WelfareUrgency.High, decision.OverrideUrgency);
        IReadOnlyList<InspectorDecisionRecord> decisions =
            await harness.Store.ListDecisionsForCaseAsync(outcome.Case.CaseId);
        Assert.Single(decisions);
        WelfareCaseRecord persisted = Assert.IsType<WelfareCaseRecord>(
            await harness.Store.GetAsync(outcome.Case.CaseId));
        Assert.Equal(WelfareUrgency.High, persisted.Urgency);
    }

    [Fact]
    public async Task InMemoryPersistence_CoversCasesConversationsApprovalsAndDecisions()
    {
        InMemoryDemo2StateStore store = new();
        CorrelationId correlationId = new("demo2-persistence");
        ApprovalRequest request = new(
            "approval-persistence",
            correlationId,
            "reserveSupportPackage",
            ["caseId"],
            "test",
            Now,
            Now.AddMinutes(30));
        WelfareCaseRecord caseRecord = new(
            "CASE-PERSIST",
            OwnerObjectId,
            "AREA-1001",
            Now,
            new string('a', 64),
            WelfareUrgency.High,
            WelfareRoute.PriorityInspectorReview,
            WelfareCaseStatus.PendingApproval,
            "Synthetic summary.",
            ["test"],
            false,
            "response-1",
            request.RequestId,
            1,
            Now);
        ConversationRecord conversation = new(
            caseRecord.CaseId,
            "conversation-1",
            "response-1",
            Now);
        WelfareApprovalRecord approval = new(
            caseRecord.CaseId,
            caseRecord.Version,
            request,
            null,
            null,
            Now);
        InspectorDecisionRecord inspectorDecision = new(
            "decision-1",
            caseRecord.CaseId,
            WelfareUrgency.Medium,
            WelfareUrgency.High,
            Guid.NewGuid(),
            "test-override",
            Now);

        await store.UpsertAsync(caseRecord);
        await ((IConversationStore)store).UpsertAsync(conversation);
        await store.UpsertAsync(approval);
        await store.AddAsync(inspectorDecision);

        Assert.Equal(caseRecord, await store.GetAsync(caseRecord.CaseId));
        Assert.Equal(conversation, await ((IConversationStore)store).GetAsync(caseRecord.CaseId));
        Assert.Equal(approval, await store.GetAsync(caseRecord.CaseId, request.RequestId));
        Assert.Equal(
            inspectorDecision,
            Assert.Single(await store.ListDecisionsForCaseAsync(caseRecord.CaseId)));
    }

    [Fact]
    public void AgentResponseParser_ParsesStrictContractAndRedactsPii()
    {
        const string response =
            """
            ```json
            {
              "urgency": "Low",
              "route": "RecordAndClose",
              "summary": "Synthetic result for keeper@example.test",
              "evidence": ["source context"],
              "repeatedUnmetNeedsCount": 1,
              "sourceConflictPresent": true,
              "approvalRecommended": false
            }
            ```
            """;

        AgentAssessment parsed = AgentResponseParser.Parse(
            response,
            "response-1",
            "conversation-1");

        Assert.Equal(WelfareUrgency.Low, parsed.Urgency);
        Assert.True(parsed.BreedContextUsed);
        Assert.Contains("[redacted-email]", parsed.Summary, StringComparison.Ordinal);
        Assert.Throws<AgentResponseFormatException>(
            () => AgentResponseParser.Parse(
                """{"urgency":"Low","route":"RecordAndClose","summary":"ok","evidence":[],"repeatedUnmetNeedsCount":0,"sourceConflictPresent":false,"approvalRecommended":false,"extra":true}""",
                "response-2",
                "conversation-2"));
    }

    [Fact]
    public void AzureEnvironmentSettings_EnableManagedServices()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AZURE_AI_PROJECT_ENDPOINT"] = "https://project.example.test",
                    ["AZURE_AI_MODEL_DEPLOYMENT_NAME"] = "gpt-5.4-mini",
                    ["AZURE_AI_REASONING_EFFORT"] = "low",
                    ["DEFRA_TOOLS_MCP_URL"] = "https://tools.example.test/mcp/flood-support",
                    ["DEFRA_TOOLS_AUDIENCE"] = "api://tenant/tools",
                    ["AZURE_COSMOS_ENDPOINT"] = "https://cosmos.example.test",
                    ["AZURE_COSMOS_DATABASE_NAME"] = "defra-demo",
                    ["AZURE_COSMOS_CONTAINER_NAME"] = "demo2-cases"
                })
            .Build();
        Demo2Options options = new();

        Demo2EnvironmentConfiguration.Apply(
            options,
            configuration,
            new TestHostEnvironment());

        Assert.Equal(Demo2AgentModes.Azure, options.Agent.Mode);
        Assert.Equal(Demo2PersistenceProviders.Cosmos, options.Persistence.Provider);
        Assert.Equal("gpt-5.4-mini", options.Agent.ModelDeploymentName);
        Assert.Equal("low", options.Agent.ReasoningEffort);
        Assert.Equal("https://tools.example.test/mcp/flood-support", options.Toolbox.Endpoint);
        Assert.False(new Demo2OptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void ProductionConfiguration_FailsClosedWithoutManagedServiceEndpoints()
    {
        Demo2Options options = new();
        Demo2EnvironmentConfiguration.Apply(
            options,
            new ConfigurationBuilder().Build(),
            new TestHostEnvironment());

        ValidateOptionsResult validation = new Demo2OptionsValidator().Validate(null, options);

        Assert.True(validation.Failed);
        Assert.Contains(
            validation.Failures,
            failure => failure.Contains("ProjectEndpoint", StringComparison.Ordinal));
        Assert.Contains(
            validation.Failures,
            failure => failure.Contains("ModelDeploymentName", StringComparison.Ordinal));
        Assert.Contains(
            validation.Failures,
            failure => failure.Contains("CosmosEndpoint", StringComparison.Ordinal));
    }

    private static AgentAssessment AgentResponse(
        WelfareUrgency urgency,
        WelfareRoute route,
        int repeatCount,
        bool breedContextUsed = false) =>
        new(
            urgency,
            route,
            "Synthetic agent summary.",
            ["synthetic evidence"],
            repeatCount,
            breedContextUsed,
            false,
            $"response-{Guid.NewGuid():N}",
            $"conversation-{Guid.NewGuid():N}");

    private sealed class Demo2TestHarness
    {
        public Demo2TestHarness()
        {
            Demo2Options options = CreateOptions();
            OptionsWrapper<Demo2Options> wrappedOptions = new(options);
            Clock = new FixedTimeProvider(Now);
            Rules = new TestRuleProvider();
            ToolPolicy = new TestToolPolicyProvider();
            ComplaintInputGuard inputGuard = new(ToolPolicy, wrappedOptions);
            WelfareDeterministicPolicy deterministicPolicy = new(Rules);
            Audit = new Demo2AuditTrail(new NoOpAuditWriter(), Rules, Clock);
            Store = new InMemoryDemo2StateStore();
            Agent = new FakeAgentClient();
            AssessmentService = new WelfareAssessmentService(
                inputGuard,
                deterministicPolicy,
                Agent,
                Store,
                Store,
                Store,
                ToolPolicy,
                Audit,
                wrappedOptions,
                Clock);
            AdminWorkflow = new Demo2AdminWorkflowService(
                Store,
                Store,
                Store,
                new InMemoryWelfareToolboxClient(wrappedOptions, Clock),
                ToolPolicy,
                wrappedOptions,
                Audit,
                Clock);
        }

        public FixedTimeProvider Clock { get; }

        public TestRuleProvider Rules { get; }

        public TestToolPolicyProvider ToolPolicy { get; }

        public Demo2AuditTrail Audit { get; }

        public InMemoryDemo2StateStore Store { get; }

        public FakeAgentClient Agent { get; }

        public WelfareAssessmentService AssessmentService { get; }

        public Demo2AdminWorkflowService AdminWorkflow { get; }

        public async Task<WelfareAssessmentOutcome> AssessAsync(
            string caseId,
            string? farmReference,
            string complaint)
        {
            WelfareAssessmentOutcome? outcome = null;
            await foreach (WelfareAssessmentEvent assessmentEvent in AssessmentService.AssessAsync(
                new WelfareAssessmentCommand(
                    caseId,
                    farmReference,
                    complaint,
                    Now,
                    OwnerObjectId)))
            {
                outcome = assessmentEvent.Outcome ?? outcome;
            }

            return Assert.IsType<WelfareAssessmentOutcome>(outcome);
        }

        public static Demo2Options CreateOptions() =>
            new()
            {
                MaxComplaintCharacters = 6_000,
                ApprovalLifetimeMinutes = 30,
                Agent = new Demo2AgentOptions
                {
                    Mode = Demo2AgentModes.LocalContract,
                    Name = "cross-government-flood-support",
                    MaxOutputTokens = 4_000,
                    MaxToolCalls = 4
                },
                Toolbox = new Demo2ToolboxOptions
                {
                    Name = "flood-support",
                    Endpoint = "http://localhost:5100/mcp/flood-support",
                    DispatchToolName = "reserveSupportPackage"
                },
                Persistence = new Demo2PersistenceOptions
                {
                    Provider = Demo2PersistenceProviders.InMemory
                },
                Audit = new Demo2AuditOptions
                {
                    Path = "audit/demo2-tests.jsonl"
                }
            };
    }

    private sealed class FakeAgentClient : IWelfareAgentClient
    {
        public AgentAssessment Response { get; set; } = AgentResponse(
            WelfareUrgency.Low,
            WelfareRoute.RecordAndClose,
            repeatCount: 0);

        public int CallCount { get; private set; }

        public Task<AgentAssessment> AssessAsync(
            AgentAssessmentRequest request,
            AgentMcpApprovalHandler? approvalHandler = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(
                Response with
                {
                    ConversationId = request.ConversationId ?? Response.ConversationId
                });
        }
    }

    private sealed class TestRuleProvider : IWelfareRuleProvider
    {
        public WelfareRuleSet Rules { get; } = new(
            "1.0.0",
            "demo2-flood-support-rules",
            true,
            180,
            2,
            90,
            60,
            30,

            ["flood-displacement", "accommodation reaches capacity", "stranded households", "immediate-danger"],
            ["reserveSupportPackage"],
            false);
    }

    private sealed class TestToolPolicyProvider : IDemo2ToolPolicyProvider
    {
        public PolicyConfiguration Policy { get; } = new(
            "demo2-tool-access",
            "1.0.0",
            ["getSituationReports", "findAccommodation", "checkTransportCapacity", "reserveSupportPackage"],
            [
                new("getSituationReports", false),
                new("findAccommodation", false),
                new("checkTransportCapacity", false),
                new("reserveSupportPackage", true)
            ],
            new(6_000, 6, 2_000, 6),
            [
                @"ignore\s+(all\s+)?(previous|prior|system)\s+instructions",
                @"reveal\s+(the\s+)?(system|developer)\s+prompt",
                @"(disable|bypass|override)\s+(policy|safety|approval)"
            ],
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["domainRules"] = "config/domain/flood-support-rules.v1.json"
            });
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Demo2.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class NoOpAuditWriter : IAppendOnlyAuditWriter
    {
        public ValueTask AppendAsync(
            SanitizedAuditEvent auditEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }
    }
}
