using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PublicSectorAgentDemos.Contracts.Demo4;
using PublicSectorAgentDemos.Demo4.Application;
using PublicSectorAgentDemos.Demo4.HostedAgents;

namespace PublicSectorAgentDemos.Demo4.Tests;

public sealed class FoundryHostedAgentLifecycleTests
{
    [Fact]
    public async Task PollsAndContinuesOnlyAReviewedSkillApproval()
    {
        QueueHandler handler = new(
            """{"id":"conversation-1"}""",
            """{"id":"response-1","status":"queued"}""",
            """{"id":"response-1","status":"in_progress"}""",
            """
            {"id":"response-1","status":"completed","agent_session_id":"session-1","output":[
              {"type":"mcp_approval_request","id":"approval-1","server_label":"agent_framework","name":"load_skill","arguments":"{\"skillName\":\"case-pattern-guidance\"}"}
            ]}
            """,
            CompletedAssessmentResponse());
        using HttpClient http = new(handler);
        FoundryHostedCasePatternAgentClient client = CreateClient(http);

        HostedAgentTurnResult result = await client.InvokeAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal("completed", result.Status);
        Assert.Single(result.ToolExecutions!);
        Assert.Equal("assess_case_pattern", result.ToolExecutions![0].ToolName);
        Assert.Equal(5, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("HostedAgents=V1Preview", request.Feature);
            Assert.Equal("?api-version=v1", request.Query);
        });
        RecordedRequest continuation = handler.Requests[4];
        Assert.Equal(HttpMethod.Post, continuation.Method);
        Assert.Contains("\"conversation\":\"conversation-1\"", continuation.Body);
        Assert.Contains("\"agent_session_id\":\"session-1\"", continuation.Body);
        Assert.Contains("\"approval_request_id\":\"approval-1\"", continuation.Body);
        Assert.DoesNotContain("previous_response_id", continuation.Body);
        HostedAgentGuardDecision guardDecision = new HostedAgentInputGuard().Screen(
            Encoding.UTF8.GetBytes(continuation.Body));
        Assert.True(guardDecision.IsAllowed);
    }

    [Fact]
    public async Task RejectsAnUnreviewedSkillApproval()
    {
        QueueHandler handler = new(
            """{"id":"conversation-1"}""",
            """
            {"id":"response-1","status":"completed","agent_session_id":"session-1","output":[
              {"type":"mcp_approval_request","id":"approval-1","server_label":"agent_framework","name":"load_skill","arguments":{"skillName":"operational-admin"}}
            ]}
            """);
        using HttpClient http = new(handler);
        FoundryHostedCasePatternAgentClient client = CreateClient(http);

        HostedAssessmentProcessingException exception =
            await Assert.ThrowsAsync<HostedAssessmentProcessingException>(
                () => client.InvokeAsync(Request(), CancellationToken.None));

        Assert.Equal("The hosted assessment did not complete.", exception.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task StopsAfterTheBoundedPollLimit()
    {
        QueueHandler handler = new(
            """{"id":"conversation-1"}""",
            """{"id":"response-1","status":"queued"}""",
            """{"id":"response-1","status":"queued"}""",
            """{"id":"response-1","status":"in_progress"}""");
        using HttpClient http = new(handler);
        FoundryHostedCasePatternAgentClient client = CreateClient(
            http,
            maximumPollAttempts: 2);

        await Assert.ThrowsAsync<HostedAssessmentProcessingException>(
            () => client.InvokeAsync(Request(), CancellationToken.None));

        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task EvaluatesCompletionReturnedByTheFinalAllowedPoll()
    {
        QueueHandler handler = new(
            """{"id":"conversation-1"}""",
            """{"id":"response-1","status":"queued"}""",
            """{"id":"response-1","status":"in_progress"}""",
            CompletedAssessmentResponse());
        using HttpClient http = new(handler);
        FoundryHostedCasePatternAgentClient client = CreateClient(
            http,
            maximumPollAttempts: 2);

        HostedAgentTurnResult result = await client.InvokeAsync(
            Request(),
            CancellationToken.None);

        Assert.Equal("completed", result.Status);
        Assert.Single(result.ToolExecutions!);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task ProjectsATerminalFailureAsAGenericError()
    {
        QueueHandler handler = new(
            """{"id":"conversation-1"}""",
            """{"id":"response-1","status":"failed","error":{"code":"internal-sensitive-code","message":"sensitive service detail"}}""");
        using HttpClient http = new(handler);
        FoundryHostedCasePatternAgentClient client = CreateClient(http);

        HostedAssessmentProcessingException exception =
            await Assert.ThrowsAsync<HostedAssessmentProcessingException>(
                () => client.InvokeAsync(Request(), CancellationToken.None));

        Assert.Equal("The hosted assessment did not complete.", exception.Message);
        Assert.DoesNotContain("sensitive", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelsWhileWaitingToPoll()
    {
        QueueHandler handler = new(
            """{"id":"conversation-1"}""",
            """{"id":"response-1","status":"queued"}""");
        using HttpClient http = new(handler);
        FoundryHostedCasePatternAgentClient client = CreateClient(
            http,
            pollIntervalMilliseconds: 5_000);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(25));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.InvokeAsync(Request(), cancellation.Token));

        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("https://demo.services.ai.azure.com/api/projects/control-investigations/applications/demo4-hosted-agent/protocols/openai/responses")]
    [InlineData("https://demo.services.ai.azure.com/api/projects/control-investigations/applications/demo4-hosted-agent/protocols/openai/responses?api-version=2025-04-01-preview")]
    [InlineData("https://demo.services.ai.azure.com/api/projects/control-investigations/applications/demo4-hosted-agent/protocols/openai/responses?api-version=v1&trace=true")]
    [InlineData("https://demo.services.ai.azure.com/api/projects/control-investigations/applications/demo4-hosted-agent/protocols/openai/responses?API-VERSION=v1")]
    public void RejectsAnEndpointWithoutTheExactResponsesApiVersion(string endpoint)
    {
        using HttpClient http = new(new QueueHandler());

        Assert.Throws<InvalidOperationException>(() =>
            new FoundryHostedCasePatternAgentClient(
                http,
                new TokenProvider(),
                new HostedAgentOptions
                {
                    ResponsesEndpoint = endpoint,
                    ModelDeploymentName = "gpt-5.4-mini"
                }));
    }

    [Fact]
    public void ProjectorRejectsFunctionOutputWithoutAFunctionCall()
    {
        using JsonDocument response = JsonDocument.Parse(
            """{"id":"response-1","output":[{"type":"function_call_output","call_id":"call-1","output":"{}"}]}""");

        Assert.Throws<HostedAssessmentProcessingException>(
            () => FoundryResponsesMetadataProjector.Project(response.RootElement));
    }

    [Fact]
    public void ProjectorUnwrapsAToolboxEnvelopeAroundTheMemorySearchResult()
    {
        const string searchResult = """{"consulted":true,"records":[],"returnedCount":0}""";
        using JsonDocument response = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = "response-1",
            output = new object[]
            {
                new
                {
                    type = "function_call",
                    call_id = "call-memory-1",
                    name = Demo4MemoryContract.SearchToolName,
                    arguments = """{"query":"prior control failure"}"""
                },
                new
                {
                    type = "function_call_output",
                    call_id = "call-memory-1",
                    output = JsonSerializer.Serialize(new
                    {
                        content = new[]
                        {
                            new { type = "text", text = searchResult }
                        }
                    })
                }
            }
        }));

        TrustedToolExecution execution = Assert.Single(
            FoundryResponsesMetadataProjector.Project(response.RootElement));

        Assert.Equal(Demo4MemoryContract.SearchToolName, execution.ToolName);
        Assert.True(execution.Succeeded);
        Assert.Equal(searchResult, execution.StructuredResultJson);
    }

    [Fact]
    public void ProjectorRejectsAMemorySearchResultThatIsNotAJsonObject()
    {
        using JsonDocument response = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            id = "response-1",
            output = new object[]
            {
                new
                {
                    type = "function_call",
                    call_id = "call-memory-1",
                    name = Demo4MemoryContract.SearchToolName,
                    arguments = """{"query":"prior control failure"}"""
                },
                new
                {
                    type = "function_call_output",
                    call_id = "call-memory-1",
                    output = "the notebook returned two prior cases"
                }
            }
        }));

        TrustedToolExecution execution = Assert.Single(
            FoundryResponsesMetadataProjector.Project(response.RootElement));

        Assert.False(execution.Succeeded);
        Assert.Null(execution.StructuredResultJson);
    }

    private static string CompletedAssessmentResponse()
    {
        string assessment = AtomicAssessmentValidator.SerializeAssessment(
            TestData.Result(InvestigationOutcome.Completed, []).Assessment);
        return JsonSerializer.Serialize(new
        {
            id = "response-2",
            status = "completed",
            output = new object[]
            {
                new
                {
                    type = "function_call",
                    call_id = "call-assessment-1",
                    name = "assess_case_pattern",
                    arguments = """{"scenarioId":"CASE-BASELINE-GRANTS"}"""
                },
                new
                {
                    type = "function_call_output",
                    call_id = "call-assessment-1",
                    output = assessment
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
    }

    private static FoundryHostedCasePatternAgentClient CreateClient(
        HttpClient httpClient,
        int maximumPollAttempts = 120,
        int pollIntervalMilliseconds = 0) =>
        new(
            httpClient,
            new TokenProvider(),
            new HostedAgentOptions
            {
                ResponsesEndpoint =
                    "https://demo.services.ai.azure.com/api/projects/control-investigations/applications/demo4-hosted-agent/protocols/openai/responses?api-version=v1",
                ModelDeploymentName = "gpt-5.4-mini",
                MaximumPollAttempts = maximumPollAttempts,
                PollIntervalMilliseconds = pollIntervalMilliseconds
            });

    private static CasePatternAssessmentRequest Request() => new(
        "investigation-1001",
        "CASE-BASELINE-GRANTS",
        "Assess the configured supplier assurance release control.");

    private sealed class TokenProvider : IFoundryAccessTokenProvider
    {
        public ValueTask<AccessToken> GetTokenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AccessToken(
                "managed-identity-token",
                DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }

    private sealed class QueueHandler(params string[] responses) : HttpMessageHandler
    {
        private readonly Queue<string> _responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(
                request.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query,
                body,
                request.Headers.GetValues("Foundry-Features").Single()));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _responses.Dequeue(),
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Path,
        string Query,
        string Body,
        string Feature);
}
