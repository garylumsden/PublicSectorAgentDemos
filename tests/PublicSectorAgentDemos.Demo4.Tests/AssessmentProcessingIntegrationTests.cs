using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PublicSectorAgentDemos.Contracts.Demo4;
using PublicSectorAgentDemos.Demo4.Application;

namespace PublicSectorAgentDemos.Demo4.Tests;

public sealed class AssessmentProcessingIntegrationTests
{
    [Fact]
    public async Task OneValidAtomicCompletedAssessmentProducesOneMemoryWrite()
    {
        ValidatedInvestigationResult result = AtomicResult();
        StubHostedAgentClient agent = new(new(
            "completed",
            ["The assessment completed."],
            result.ToolExecutions));
        RecordingMemoryClient memory = new();
        AssessmentProcessingService processor = CreateProcessor(agent, memory);

        AssessmentProcessingReceipt receipt = await processor.ProcessAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal("completed", receipt.Status);
        Assert.Equal(1, agent.CallCount);
        Assert.Single(memory.Records);
    }

    [Fact]
    public async Task InvalidAtomicAssessmentProducesNoMemoryWrite()
    {
        ValidatedInvestigationResult invalid = TestData.Result(
            InvestigationOutcome.Completed,
            []);
        invalid = invalid with
        {
            Assessment = invalid.Assessment with { Evidence = [] }
        };
        StubHostedAgentClient agent = new(new(
            "completed",
            ["The assessment completed."],
            [
                new(
                    "call-assessment-1",
                    "assess_case_pattern",
                    true,
                    AtomicAssessmentValidator.SerializeAssessment(invalid.Assessment))
            ]));
        RecordingMemoryClient memory = new();
        AssessmentProcessingService processor = CreateProcessor(agent, memory);

        await Assert.ThrowsAsync<HostedAssessmentProcessingException>(
            () => processor.ProcessAsync(Request(), CancellationToken.None));

        Assert.Equal(1, agent.CallCount);
        Assert.Empty(memory.Records);
    }

    [Fact]
    public async Task FailedHostedTurnProducesNoMemoryWrite()
    {
        StubHostedAgentClient agent = new(new("failed", []));
        RecordingMemoryClient memory = new();
        AssessmentProcessingService processor = CreateProcessor(agent, memory);

        await Assert.ThrowsAsync<HostedAssessmentProcessingException>(
            () => processor.ProcessAsync(Request(), CancellationToken.None));

        Assert.Equal(1, agent.CallCount);
        Assert.Empty(memory.Records);
    }

    [Fact]
    public async Task ModelAuthoredToolLedgerCannotProduceAMemoryWrite()
    {
        ValidatedInvestigationResult fabricated = AtomicResult();
        StubHostedAgentClient agent = new(new(
            "completed",
            [AtomicAssessmentValidator.Serialize(fabricated)],
            []));
        RecordingMemoryClient memory = new();
        AssessmentProcessingService processor = CreateProcessor(agent, memory);

        await Assert.ThrowsAsync<HostedAssessmentProcessingException>(
            () => processor.ProcessAsync(Request(), CancellationToken.None));

        Assert.Empty(memory.Records);
    }

    [Fact]
    public async Task MissingFunctionResultMetadataProducesNoMemoryWrite()
    {
        StubHostedAgentClient agent = new(new(
            "completed",
            ["The model claims that the assessment completed."],
            [new("call-assessment-1", "assess_case_pattern", false, null)]));
        RecordingMemoryClient memory = new();
        AssessmentProcessingService processor = CreateProcessor(agent, memory);

        await Assert.ThrowsAsync<HostedAssessmentProcessingException>(
            () => processor.ProcessAsync(Request(), CancellationToken.None));

        Assert.Empty(memory.Records);
    }

    [Fact]
    public async Task ProductionHostedClientUsesTheApplicationIdentityToken()
    {
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        FoundryHostedCasePatternAgentClient client = new(
            httpClient,
            new StubTokenProvider(),
            new HostedAgentOptions
            {
                ResponsesEndpoint =
                    "https://demo.services.ai.azure.com/api/projects/control-investigations/applications/demo4-hosted-agent/protocols/openai/responses?api-version=v1"
            });

        HostedAgentTurnResult turn = await client.InvokeAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal("completed", turn.Status);
        Assert.Equal(["{}"], turn.OutputTexts);
        Assert.Equal("Bearer", handler.AuthenticationScheme);
        Assert.Equal("managed-identity-token", handler.AuthenticationToken);
    }

    [Fact]
    public async Task ProductionClientMetadataProducesExactlyOneMemoryWrite()
    {
        CasePatternAssessment assessment = TestData.Result(
            InvestigationOutcome.Completed,
            []).Assessment;
        AtomicResponseHandler handler = new(
            AtomicAssessmentValidator.SerializeAssessment(assessment));
        using HttpClient httpClient = new(handler);
        FoundryHostedCasePatternAgentClient client = new(
            httpClient,
            new StubTokenProvider(),
            new HostedAgentOptions
            {
                ResponsesEndpoint =
                    "https://demo.services.ai.azure.com/api/projects/control-investigations/applications/demo4-hosted-agent/protocols/openai/responses?api-version=v1",
                PollIntervalMilliseconds = 0
            });
        RecordingMemoryClient memory = new();
        AssessmentProcessingService processor = CreateProcessor(client, memory);

        AssessmentProcessingReceipt receipt = await processor.ProcessAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal("completed", receipt.Status);
        Assert.Single(memory.Records);
    }

    [Fact]
    public void OnlyTheVerifiedAssessmentEntersTheEvidenceLedger()
    {
        ValidatedInvestigationResult atomic = AtomicResult();
        HostedAgentTurnResult turn = new(
            "completed",
            ["The assessment completed."],
            [
                new(
                    "call-context-1",
                    "search_case_context",
                    true,
                    """{"records":[]}"""),
                new(
                    "call-memory-1",
                    Demo4MemoryContract.SearchToolName,
                    true,
                    """{"consulted":true,"records":[]}"""),
                atomic.ToolExecutions[0]
            ]);

        ValidatedInvestigationResult validated = AtomicAssessmentValidator.Validate(
            Request(),
            turn,
            TestData.Now);

        TrustedToolExecution execution = Assert.Single(validated.ToolExecutions);
        Assert.Equal("assess_case_pattern", execution.ToolName);
    }

    [Fact]
    public async Task TheStoredAssessmentStaysByteEquivalentToTheToolOutput()
    {
        ValidatedInvestigationResult seed = TestData.Result(
            InvestigationOutcome.Completed,
            []);
        string assessmentJson = AtomicAssessmentValidator.SerializeAssessment(
            seed.Assessment);
        StubHostedAgentClient agent = new(new(
            "completed",
            ["The assessment completed."],
            [
                new(
                    "call-memory-1",
                    Demo4MemoryContract.SearchToolName,
                    true,
                    """{"consulted":true,"records":[],"returnedCount":0}"""),
                new("call-assessment-1", "assess_case_pattern", true, assessmentJson)
            ]));
        RecordingMemoryClient memory = new();
        AssessmentProcessingService processor = CreateProcessor(agent, memory);

        AssessmentProcessingResult processed = await processor.ProcessDetailedAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal(
            System.Text.Encoding.UTF8.GetBytes(assessmentJson),
            System.Text.Encoding.UTF8.GetBytes(
                AtomicAssessmentValidator.SerializeAssessment(
                    processed.Investigation.Assessment)));
        Assert.True(processed.MemoryRead.Searched);
        Assert.Equal(0, processed.MemoryRead.ReturnedCount);
        NotebookRecordEnvelope written = Assert.Single(memory.Records);
        Assert.Same(written, processed.WrittenRecord);
        Assert.Equal(
            NotebookRecordCodec.Serialize(written),
            processed.WrittenRecordJson);
        Assert.EndsWith(
            MemoryContinuityReader.NoPriorReferenceStatement,
            written.RecommendedFollowUp,
            StringComparison.Ordinal);
        Assert.Equal(
            processed.Investigation.RecommendedFollowUp,
            written.RecommendedFollowUp);
    }

    private static AssessmentProcessingService CreateProcessor(
        IHostedCasePatternAgentClient agent,
        IFoundryMemoryItemClient memory)
    {
        TimeProvider time = new FixedTimeProvider(TestData.Now);
        return new(
            agent,
            new InvestigationMemoryCoordinator(memory, time),
            time);
    }

    private static CasePatternAssessmentRequest Request() => new(
        "investigation-1001",
        "CASE-BASELINE-GRANTS",
        "Assess the configured supplier assurance release control.");

    private static ValidatedInvestigationResult AtomicResult()
    {
        ValidatedInvestigationResult seed = TestData.Result(
            InvestigationOutcome.Completed,
            []);
        TrustedToolExecution assessment = new(
            "call-assessment-1",
            "assess_case_pattern",
            true,
            AtomicAssessmentValidator.SerializeAssessment(seed.Assessment));
        return seed with { ToolExecutions = [assessment] };
    }

    private sealed class StubHostedAgentClient(HostedAgentTurnResult result)
        : IHostedCasePatternAgentClient
    {
        public int CallCount { get; private set; }

        public Task<HostedAgentTurnResult> InvokeAsync(
            CasePatternAssessmentRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingMemoryClient : IFoundryMemoryItemClient
    {
        public List<NotebookRecordEnvelope> Records { get; } = [];

        public Task CreateAndVerifyAsync(
            NotebookRecordEnvelope record,
            string serializedRecord,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(serializedRecord, NotebookRecordCodec.Serialize(record));
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<MemoryNotebookListing> ListValidRecordsAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new MemoryNotebookListing(
                Records.Select(record => new MemoryNotebookEntry(
                    $"memory-{record.RecordId}",
                    now,
                    record)).ToArray(),
                Records.Count,
                0));
        }
    }

    private sealed class StubTokenProvider : IFoundryAccessTokenProvider
    {
        public ValueTask<AccessToken> GetTokenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AccessToken(
                "managed-identity-token",
                DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? AuthenticationScheme { get; private set; }

        public string? AuthenticationToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AuthenticationScheme = request.Headers.Authorization?.Scheme;
            AuthenticationToken = request.Headers.Authorization?.Parameter;
            string response = request.RequestUri!.AbsolutePath.EndsWith(
                "/conversations",
                StringComparison.Ordinal)
                    ? """{"id":"conversation-1"}"""
                    : """{"id":"response-1","status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"{}"}]}]}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class AtomicResponseHandler(string assessmentJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string response = request.RequestUri!.AbsolutePath.EndsWith(
                "/conversations",
                StringComparison.Ordinal)
                    ? """{"id":"conversation-1"}"""
                    : JsonSerializer.Serialize(new
                    {
                        id = "response-1",
                        status = "completed",
                        output = new object[]
                        {
                            new
                            {
                                type = "function_call",
                                call_id = "call-assessment-1",
                                name = "assess_case_pattern",
                                arguments =
                                    """{"scenarioId":"CASE-BASELINE-GRANTS"}"""
                            },
                            new
                            {
                                type = "function_call_output",
                                call_id = "call-assessment-1",
                                output = assessmentJson
                            },
                            new
                            {
                                type = "message",
                                content = new[]
                                {
                                    new
                                    {
                                        type = "output_text",
                                        text = "The assessment completed."
                                    }
                                }
                            }
                        }
                    });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
