using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PublicSectorAgentDemos.Demo1.FoundationAndGround;

namespace PublicSectorAgentDemos.Demo1.Web.Tests;

public sealed class FoundryClientTests
{
    private const string Response = """
        {"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"> exact words\n<script>unsafe</script>\n  ","annotations":[{"type":"url_citation","title":"<img onerror=alert(1)>","url":"javascript:alert(1)"}]}]}]}
        """;

    [Fact]
    public async Task RestInvocationUsesRegisteredAgentsFoundryScopeAndOnlyTheUnchangedUserInput()
    {
        RecordingHandler handler = new(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Response, Encoding.UTF8, "application/json")
        });
        using HttpClient httpClient = new(handler);
        RecordingCredential credential = new();
        FoundryAgentClient client = new(httpClient, credential,
            FoundryProjectSettings.Parse("https://test.services.ai.azure.com/api/projects/example"));
        const string prompt = "A prompt with \"quotes\", \n and trailing whitespace.  ";
        foreach (string name in new[] { Demo1AgentCatalog.FoundationAgentName, Demo1AgentCatalog.GroundAgentName })
        {
            AgentAnswer answer = await client.InvokeAsync(name, prompt, CancellationToken.None);
            Assert.Equal(Response, answer.OriginalResponse);
            Assert.Equal("> exact words\n<script>unsafe</script>\n  ", answer.OriginalText);
            Assert.Single(answer.Citations);
            Assert.Equal("javascript:alert(1)", answer.Citations[0].Reference);
        }

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(credential.Scopes, scope => Assert.Equal("https://ai.azure.com/.default", scope));
        Assert.Collection(handler.Requests,
            request => AssertRequest(request, Demo1AgentCatalog.FoundationAgentName, prompt),
            request => AssertRequest(request, Demo1AgentCatalog.GroundAgentName, prompt));
    }

    private static void AssertRequest(RecordedRequest request, string name, string prompt)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://test.services.ai.azure.com/api/projects/example/agents/{name}/endpoint/protocols/openai/responses?api-version=v1", request.Uri.AbsoluteUri);
        Assert.Equal("Bearer local-test-token", request.Authorization);
        using JsonDocument body = JsonDocument.Parse(request.Body);
        Assert.Single(body.RootElement.EnumerateObject());
        JsonElement input = Assert.Single(body.RootElement.GetProperty("input").EnumerateArray());
        Assert.Equal("user", input.GetProperty("role").GetString());
        Assert.Equal(prompt, input.GetProperty("content").GetString());
        Assert.Equal(2, input.EnumerateObject().Count());
    }

    [Fact]
    public async Task QualityJudgeUsesDirectResponsesMediumReasoningStrictSchemaAndNoTools()
    {
        string result = JsonSerializer.Serialize(new
        {
            summary = "A qualified comparison.",
            criteria = QualityCriteria.All.Select(id => new
            {
                id,
                foundation = new { score = 3, explanation = "Foundation explanation.", quote = "Foundation answer." },
                ground = new { score = 4, explanation = "Ground explanation.", quote = "Ground answer." }
            }),
            reference_checks = Array.Empty<object>()
        });
        string serviceResponse = JsonSerializer.Serialize(new
        {
            status = "completed",
            output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = result } } } }
        });
        RecordingHandler handler = new(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(serviceResponse, Encoding.UTF8, "application/json")
        });
        using HttpClient httpClient = new(handler);
        RecordingCredential credential = new();
        FoundryQualityAssessmentClient client = new(httpClient, credential,
            FoundryProjectSettings.Parse("https://test.services.ai.azure.com/api/projects/example"),
            QualityAssessmentSettings.Parse(null));
        ComparisonSnapshot snapshot = CreateSnapshot();

        QualityAssessment assessment = await client.AssessAsync(snapshot, CancellationToken.None);

        Assert.Equal("A qualified comparison.", assessment.Summary);
        Assert.Equal(5, assessment.Criteria.Count);
        Assert.All(assessment.Criteria, criterion => Assert.Equal(1, criterion.Difference));
        Assert.Equal("gpt-5-mini", assessment.Model);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("https://test.services.ai.azure.com/api/projects/example/openai/v1/responses", request.Uri.AbsoluteUri);
        Assert.Equal("https://ai.azure.com/.default", Assert.Single(credential.Scopes));
        using JsonDocument body = JsonDocument.Parse(request.Body);
        Assert.Equal("gpt-5-mini", body.RootElement.GetProperty("model").GetString());
        Assert.Equal(QualityAssessmentRubric.Instructions, body.RootElement.GetProperty("instructions").GetString());
        Assert.Equal("medium", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(body.RootElement.GetProperty("store").GetBoolean());
        Assert.Equal("json_schema", body.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
        Assert.Equal(1, body.RootElement.GetProperty("input").GetArrayLength());
    }

    [Fact]
    public async Task QualityJudgeReplacesFabricatedQuotationsWithExactAnswerText()
    {
        string result = JsonSerializer.Serialize(new
        {
            summary = "Comparison.",
            criteria = QualityCriteria.All.Select(id => new
            {
                id,
                foundation = new { score = 3, explanation = "Explanation.", quote = "Fabricated quotation." },
                ground = new { score = 3, explanation = "Explanation.", quote = "Ground answer." }
            }),
            reference_checks = Array.Empty<object>()
        });
        string serviceResponse = JsonSerializer.Serialize(new
        {
            status = "completed",
            output = new[] { new { content = new[] { new { type = "output_text", text = result } } } }
        });
        RecordingHandler handler = new(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(serviceResponse, Encoding.UTF8, "application/json")
        });
        using HttpClient httpClient = new(handler);
        FoundryQualityAssessmentClient client = new(httpClient, new RecordingCredential(),
            FoundryProjectSettings.Parse("https://test.services.ai.azure.com/api/projects/example"),
            QualityAssessmentSettings.Parse("gpt-5-mini"));
        QualityAssessment assessment = await client.AssessAsync(CreateSnapshot(), CancellationToken.None);
        Assert.All(assessment.Criteria, criterion => Assert.Equal("Foundation answer.", criterion.Foundation.Quote));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task QualityJudgeCapsScoresWhenReviewedRequirementsAreContradicted()
    {
        string result = JsonSerializer.Serialize(new
        {
            summary = "Comparison.",
            criteria = QualityCriteria.All.Select(id => new
            {
                id,
                foundation = new { score = 5, explanation = "Foundation explanation.", quote = "Foundation answer." },
                ground = new { score = 5, explanation = "Ground explanation.", quote = "Ground answer." }
            }),
            reference_checks = new object[]
            {
                new { reference_index = 0, foundation = "missing", ground = "aligned" },
                new { reference_index = 1, foundation = "missing", ground = "contradicted" },
                new { reference_index = 2, foundation = "aligned", ground = "aligned" }
            }
        });
        string serviceResponse = JsonSerializer.Serialize(new
        {
            status = "completed",
            output = new[] { new { content = new[] { new { type = "output_text", text = result } } } }
        });
        RecordingHandler handler = new(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(serviceResponse, Encoding.UTF8, "application/json")
        });
        using HttpClient httpClient = new(handler);
        FoundryQualityAssessmentClient client = new(httpClient, new RecordingCredential(),
            FoundryProjectSettings.Parse("https://test.services.ai.azure.com/api/projects/example"),
            QualityAssessmentSettings.Parse(null));

        QualityAssessment assessment = await client.AssessAsync(
            CreateSnapshot(["Treasury approval.", "Parliamentary notification.", "Checklist threshold."]),
            CancellationToken.None);

        Assert.Equal(4, assessment.Criteria.Single(item => item.Id == QualityCriteria.Correctness).Foundation.Score);
        Assert.Equal(4, assessment.Criteria.Single(item => item.Id == QualityCriteria.RelevanceCompleteness).Foundation.Score);
        Assert.Equal(3, assessment.Criteria.Single(item => item.Id == QualityCriteria.Correctness).Ground.Score);
        Assert.Equal(3, assessment.Criteria.Single(item => item.Id == QualityCriteria.UncertaintyHandling).Ground.Score);
        Assert.Equal(3, assessment.Criteria.Single(item => item.Id == QualityCriteria.NextActionUsefulness).Ground.Score);
    }

    [Fact]
    public async Task QualityJudgeAllowsBoundedResponseMetadataLargerThanStructuredOutputLimit()
    {
        string result = JsonSerializer.Serialize(new
        {
            summary = "Comparison.",
            criteria = QualityCriteria.All.Select(id => new
            {
                id,
                foundation = new { score = 3, explanation = "Explanation.", quote = "Foundation answer." },
                ground = new { score = 3, explanation = "Explanation.", quote = "Ground answer." }
            }),
            reference_checks = Array.Empty<object>()
        });
        string serviceResponse = JsonSerializer.Serialize(new
        {
            status = "completed",
            output = new object[]
            {
                new { type = "reasoning", encrypted_content = new string('x', DemoLimits.AssessmentOutputBytes + 1) },
                new { type = "message", content = new[] { new { type = "output_text", text = result } } }
            }
        });
        RecordingHandler handler = new(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(serviceResponse, Encoding.UTF8, "application/json")
        });
        using HttpClient httpClient = new(handler);
        FoundryQualityAssessmentClient client = new(httpClient, new RecordingCredential(),
            FoundryProjectSettings.Parse("https://test.services.ai.azure.com/api/projects/example"),
            QualityAssessmentSettings.Parse(null));

        QualityAssessment assessment = await client.AssessAsync(CreateSnapshot(), CancellationToken.None);

        Assert.Equal("Comparison.", assessment.Summary);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task QualityJudgeRejectsNonCompletedServiceResponses(string status)
    {
        RecordingHandler handler = new(_ => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                status,
                output = new[] { new { content = new[] { new { type = "output_text", text = "{}" } } } }
            })
        });
        using HttpClient httpClient = new(handler);
        FoundryQualityAssessmentClient client = new(httpClient, new RecordingCredential(),
            FoundryProjectSettings.Parse("https://test.services.ai.azure.com/api/projects/example"),
            QualityAssessmentSettings.Parse(null));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.AssessAsync(CreateSnapshot(), CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    private static ComparisonSnapshot CreateSnapshot(string[]? reviewRubric = null) => new()
    {
        Id = new string('a', 32),
        Owner = "owner",
        Prompt = "Prompt.",
        Foundation = AgentAnswer.Parse("{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Foundation answer.\"}]}]}"),
        Ground = AgentAnswer.Parse("{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Ground answer.\"}]}]}"),
        Reference = new("Fixture MPM-005.", "MPM-005", "treasury-consent", "assessment", [], reviewRubric ?? [],
            new(0, 0, [], "No evidence."), new(0, 0, [], "No evidence."), ["Limited references."]),
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Redirect)]
    public async Task ServiceErrorsAreNotRetriedOrReturnedAsAnswers(HttpStatusCode status)
    {
        RecordingHandler handler = new(_ => new(status) { Content = new StringContent("Sensitive service error") });
        using HttpClient httpClient = new(handler);
        FoundryAgentClient client = new(httpClient, new RecordingCredential(),
            FoundryProjectSettings.Parse("https://test.services.ai.azure.com/api/projects/example"));
        HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() => client.InvokeAsync(
            Demo1AgentCatalog.FoundationAgentName, "prompt", CancellationToken.None));
        Assert.DoesNotContain("Sensitive service error", error.Message, StringComparison.Ordinal);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task UnknownAgentsCannotBeInvoked()
    {
        RecordingHandler handler = new(_ => new(HttpStatusCode.OK));
        using HttpClient httpClient = new(handler);
        FoundryAgentClient client = new(httpClient, new RecordingCredential(),
            FoundryProjectSettings.Parse("https://test.services.ai.azure.com/api/projects/example"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.InvokeAsync("unregistered", "prompt", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OversizedAgentResponsesAreRejected()
    {
        RecordingHandler handler = new(_ => new(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', DemoLimits.ResponseBytes + 1))
        });
        using HttpClient httpClient = new(handler);
        FoundryAgentClient client = new(httpClient, new RecordingCredential(),
            FoundryProjectSettings.Parse("https://test.services.ai.azure.com/api/projects/example"));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.InvokeAsync(
            Demo1AgentCatalog.GroundAgentName, "prompt", CancellationToken.None));
    }

    [Fact]
    public async Task StreamLimitDoesNotDependOnContentLength()
    {
        using MemoryStream atLimit = new(new byte[DemoLimits.RequestBytes]);
        Assert.Equal(DemoLimits.RequestBytes,
            (await DemoLimits.ReadBoundedAsync(atLimit, DemoLimits.RequestBytes, CancellationToken.None)).Length);
        using MemoryStream overLimit = new(new byte[DemoLimits.RequestBytes + 1]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DemoLimits.ReadBoundedAsync(overLimit, DemoLimits.RequestBytes, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://test.services.ai.azure.com/api/projects/example")]
    [InlineData("https://test.services.ai.azure.com/")]
    [InlineData("https://test.services.ai.azure.com/api/projects/example/agents/x")]
    [InlineData("https://test.services.ai.azure.com/api/projects/example?key=secret")]
    [InlineData("https://test.services.ai.azure.com/api/projects/example#fragment")]
    [InlineData("https://user@host.services.ai.azure.com/api/projects/example")]
    [InlineData("https://test.services.ai.azure.com.attacker.example/api/projects/example")]
    [InlineData("https://test.services.ai.azure.com:8080/api/projects/example")]
    public void InvalidProjectEndpointsAreRejectedWithoutEchoingTheValue(string? value)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => FoundryProjectSettings.Parse(value));
        Assert.StartsWith("AZURE_AI_FOUNDRY_ENDPOINT must be", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("key=secret", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormattingEncodesHtmlAndDoesNotActivateModelLinksOrCitationContent()
    {
        const string text = "# Heading\n## Section\n### Detail\n> <img src=x onerror=alert(1)>\n<script>alert(1)</script>\n[click](javascript:alert(1))\n& &#60;svg onload=alert(1)&#62;";
        string html = SafeMarkdown.Render(text);
        Assert.Contains("<h3>Heading</h3>", html, StringComparison.Ordinal);
        Assert.Contains("<h4>Detail</h4>", html, StringComparison.Ordinal);
        Assert.Contains("<blockquote>&lt;img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<a ", html, StringComparison.Ordinal);
        Assert.Contains("&amp;#60;", html, StringComparison.Ordinal);
        AgentAnswer answer = AgentAnswer.Parse(Response);
        Assert.Equal(Response, answer.OriginalResponse);
        Assert.DoesNotContain("<img", answer.RenderedHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void IncompleteRefusedAndUncitedResponsesRemainAvailableWithoutAQualityGate()
    {
        const string original = """
            {"status":"incomplete","output":[{"type":"message","content":[{"type":"output_text","text":"  Uncited answer.\n"},{"type":"refusal","refusal":"Original refusal."}]}]}
            """;
        AgentAnswer answer = AgentAnswer.Parse(original);
        Assert.Equal("  Uncited answer.\n\nOriginal refusal.", answer.OriginalText);
        Assert.Equal(original, answer.OriginalResponse);
        Assert.Equal("incomplete", answer.ResponseStatus);
        Assert.Empty(answer.Citations);
        AgentAnswer empty = AgentAnswer.Parse("{\"status\":\"failed\",\"error\":{\"message\":\"original service response\"}}");
        Assert.Equal("failed", empty.ResponseStatus);
        Assert.Contains("original service response", empty.OriginalResponse, StringComparison.Ordinal);
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Authorization, string Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri!, request.Headers.Authorization!.ToString(),
                await request.Content!.ReadAsStringAsync(cancellationToken)));
            return response(request);
        }
    }

    private sealed class RecordingCredential : TokenCredential
    {
        public List<string> Scopes { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scopes.AddRange(requestContext.Scopes);
            return new("local-test-token", DateTimeOffset.UtcNow.AddMinutes(5));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
