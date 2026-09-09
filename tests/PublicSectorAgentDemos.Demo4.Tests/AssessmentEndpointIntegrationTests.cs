using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PublicSectorAgentDemos.Contracts.Demo4;
using PublicSectorAgentDemos.Demo4.Application;
using PublicSectorAgentDemos.Demo4.HostedAgents;

namespace PublicSectorAgentDemos.Demo4.Tests;

public sealed class AssessmentEndpointIntegrationTests
{
    [Fact]
    public async Task AuthenticatedCompletedAssessmentWritesOnce()
    {
        ValidatedInvestigationResult result = AtomicResult();
        using AssessmentApiFactory factory = new(new(
            "completed",
            ["The assessment completed."],
            [
                new(
                    "call-memory-relevant",
                    Demo4MemoryContract.SearchToolName,
                    true,
                    """{"consulted":true,"records":[],"returnedCount":0}"""),
                .. result.ToolExecutions
            ]));
        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/case-pattern-assessments",
            Request());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, factory.Agent.CallCount);
        Assert.Single(factory.Memory.Records);
    }

    [Fact]
    public async Task UnrelatedControlSkipsMemoryAndReturnsACompletedAssessment()
    {
        CaseScenarioCatalog scenarios = new(FixturePath("fixture-set.json"));
        CaseScenario unrelated = scenarios.GetRequired(TestData.UnrelatedScenarioId);
        CaseInvestigationToolProvider provider = CreateProvider(scenarios);
        CasePatternAssessment assessment = await provider.AssessAsync(
            unrelated.ScenarioId,
            CancellationToken.None);
        HostedAgentTurnResult turn = new(
            "completed",
            ["The unrelated control assessment returned."],
            [
                new(
                    "call-assessment-unrelated",
                    CaseInvestigationToolProvider.AssessmentToolName,
                    true,
                    AtomicAssessmentValidator.SerializeAssessment(assessment))
            ]);
        using AssessmentApiFactory factory = new(turn);
        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/case-pattern-assessments",
            new CasePatternAssessmentRequest(
                "investigation-unrelated-control",
                unrelated.ScenarioId,
                unrelated.Prompt));
        AssessmentProcessingReceipt? receipt =
            await response.Content.ReadFromJsonAsync<AssessmentProcessingReceipt>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(receipt);
        Assert.Equal("completed", receipt.Status);
        Assert.Equal(ExpectedMemoryAction.Skip, unrelated.ExpectedMemoryAction);
        Assert.False(unrelated.ExpectedMemoryRead);
        Assert.True(unrelated.ExpectedMemoryWrite);
        Assert.DoesNotContain(
            turn.ToolExecutions!,
            execution => execution.ToolName == Demo4MemoryContract.SearchToolName);
        Assert.Single(factory.Memory.Records);
    }

    [Fact]
    public async Task AuthenticatedInvalidAssessmentReturnsGenericErrorWithoutWrite()
    {
        CaseScenarioCatalog scenarios = new(FixturePath("fixture-set.json"));
        CaseScenario invalidScenario = scenarios.GetRequired(
            TestData.InvalidScenarioId);
        CasePatternAssessment invalid = await CreateProvider(scenarios).AssessAsync(
            invalidScenario.ScenarioId,
            CancellationToken.None);
        using AssessmentApiFactory factory = new(new(
            "completed",
            ["The assessment completed."],
            [
                new(
                    "call-assessment-1",
                    "assess_case_pattern",
                    true,
                    AtomicAssessmentValidator.SerializeAssessment(invalid))
            ]));
        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/case-pattern-assessments",
            new CasePatternAssessmentRequest(
                "investigation-invalid-assessment",
                invalidScenario.ScenarioId,
                invalidScenario.Prompt));
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("Assessment processing failed.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("atomic", body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(factory.Memory.Records);
        Assert.False(invalidScenario.ExpectedMemoryWrite);
    }

    [Fact]
    public async Task AuthenticatedFailedTurnReturnsGenericErrorWithoutWrite()
    {
        using AssessmentApiFactory factory = new(new("failed", []));
        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/case-pattern-assessments",
            Request());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Empty(factory.Memory.Records);
    }

    [Fact]
    public async Task AuthenticatedMemoryFailureReturnsGenericJsonError()
    {
        ValidatedInvestigationResult result = AtomicResult();
        using AssessmentApiFactory factory = new(new(
            "completed",
            ["The assessment completed."],
            result.ToolExecutions));
        factory.Memory.Failure = new MemoryWriteIndeterminateException(
            "The test memory write is indeterminate.");
        using HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/case-pattern-assessments",
            Request());
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Assessment processing failed.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("indeterminate", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnauthenticatedAssessmentDoesNotInvokeAgentOrWrite()
    {
        using AssessmentApiFactory factory = new(new("failed", []));
        using HttpClient client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        using HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/case-pattern-assessments",
            Request());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, factory.Agent.CallCount);
        Assert.Empty(factory.Memory.Records);
    }

    [Fact]
    public async Task RootPageLoadsWithoutInvokingTheAgent()
    {
        using AssessmentApiFactory factory = new(new("failed", []));
        using HttpClient client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        using HttpResponseMessage response = await client.GetAsync("/");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "Cross-government control investigation notebook",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            TestData.BaselineScenarioId,
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            TestData.RecurrenceScenarioId,
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            TestData.RuledOutScenarioId,
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            TestData.UnrelatedScenarioId,
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            TestData.InvalidScenarioId,
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, factory.Agent.CallCount);
    }

    [Fact]
    public async Task RootPageExplainsTheRecommendedRunOrder()
    {
        using AssessmentApiFactory factory = new(new("failed", []));
        using HttpClient client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        using HttpResponseMessage response = await client.GetAsync("/");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Run the four cards in order", body, StringComparison.Ordinal);
        Assert.Equal(
            [
                TestData.BaselineScenarioId,
                TestData.RecurrenceScenarioId,
                TestData.RuledOutScenarioId,
                TestData.UnrelatedScenarioId
            ],
            ScenarioOrder(body));
        Assert.Contains("Run 1", body, StringComparison.Ordinal);
        Assert.Contains("Run 4", body, StringComparison.Ordinal);
        Assert.Contains("/Cases", body, StringComparison.Ordinal);
        Assert.Contains("/Memory", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaseDataPageShowsTheCanonicalToolFixturesWithoutUsingMemoryOrTheAgent()
    {
        using AssessmentApiFactory factory = new(new("failed", []));
        using HttpClient client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        using HttpResponseMessage response = await client.GetAsync("/Cases");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Canonical case data", body, StringComparison.Ordinal);
        Assert.Contains("CG-8101", body, StringComparison.Ordinal);
        Assert.Contains("grant-payment-release-8101-a", body, StringComparison.Ordinal);
        Assert.Contains("CONTEXT-GRANTS-PAYMENT-RELEASE", body, StringComparison.Ordinal);
        Assert.Contains("assess_case_pattern", body, StringComparison.Ordinal);
        Assert.Contains("separate from the shared seven-day Memory notebook", body, StringComparison.Ordinal);
        Assert.Equal(0, factory.Agent.CallCount);
        Assert.Empty(factory.Memory.Records);
    }

    [Fact]
    public async Task InvestigationPageShowsTheMatchVerdictAndTheWrittenRecord()
    {
        NotebookRecordEnvelope prior = PriorRecord("CG-8202", BaselineReasonCode);
        ValidatedInvestigationResult result = AtomicResult();
        using AssessmentApiFactory factory = new(new(
            "completed",
            ["The assessment completed."],
            [
                new(
                    "call-memory-1",
                    Demo4MemoryContract.SearchToolName,
                    true,
                    SearchJson(prior)),
                .. result.ToolExecutions
            ]));
        using HttpClient client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        string body = await PostInvestigationAsync(client);
        NotebookRecordEnvelope written = Assert.Single(factory.Memory.Records);

        Assert.Contains("What changed because of Memory", body, StringComparison.Ordinal);
        Assert.Contains(
            "Memory changed the read of this case.",
            body,
            StringComparison.Ordinal);
        Assert.Contains("prior case reference CG-8202", body, StringComparison.Ordinal);
        Assert.Contains(
            "Matched: repeat control failure in another case",
            body,
            StringComparison.Ordinal);
        Assert.Contains(
            "record-card verdict-matchedrepeatcontrolfailure",
            body,
            StringComparison.Ordinal);
        Assert.Contains("Memory searched once", body, StringComparison.Ordinal);
        Assert.Contains("memory-prior-1", body, StringComparison.Ordinal);
        Assert.Contains(prior.RecordId, body, StringComparison.Ordinal);
        Assert.Contains("Record written to the shared notebook", body, StringComparison.Ordinal);
        Assert.Contains(written.RecordId, body, StringComparison.Ordinal);
        Assert.Contains(written.ReasonCode, body, StringComparison.Ordinal);
        Assert.EndsWith(
            "The notebook matched the same reason code in prior case reference CG-8202.",
            written.RecommendedFollowUp,
            StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvestigationPageReportsASkippedMemoryReadWithoutAPriorReference()
    {
        ValidatedInvestigationResult result = AtomicResult();
        using AssessmentApiFactory factory = new(new(
            "completed",
            ["The assessment completed."],
            result.ToolExecutions));
        using HttpClient client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        string body = await PostInvestigationAsync(client);
        NotebookRecordEnvelope written = Assert.Single(factory.Memory.Records);

        Assert.Contains("Memory not consulted", body, StringComparison.Ordinal);
        Assert.Contains(
            MemoryContinuityReader.NotConsultedStatement,
            body,
            StringComparison.Ordinal);
        Assert.EndsWith(
            MemoryContinuityReader.NotConsultedStatement,
            written.RecommendedFollowUp,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryPageListsTheWrittenRecordsWithoutTheAgent()
    {
        using AssessmentApiFactory factory = new(new("failed", []));
        factory.Memory.Records.Add(PriorRecord("CG-8101", BaselineReasonCode));
        factory.Memory.RejectedItemCount = 2;
        using HttpClient client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        using HttpResponseMessage response = await client.GetAsync("/Memory");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Shared notebook records", body, StringComparison.Ordinal);
        Assert.Contains("CG-8101", body, StringComparison.Ordinal);
        Assert.Contains(
            factory.Memory.Records[0].RecordId,
            body,
            StringComparison.Ordinal);
        Assert.Contains("Items rejected", body, StringComparison.Ordinal);
        Assert.Equal(0, factory.Agent.CallCount);
        Assert.DoesNotContain("<form", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<button", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("synthetic", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MemoryPageShowsAnEmptyStateForAnEmptyNotebook()
    {
        using AssessmentApiFactory factory = new(new("failed", []));
        using HttpClient client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        using HttpResponseMessage response = await client.GetAsync("/Memory");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("The shared notebook holds no valid record", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryPageReportsASafeErrorState()
    {
        using AssessmentApiFactory factory = new(new("failed", []));
        factory.Memory.ListFailure = new MemoryNotebookReadException(
            "Memory item list returned HTTP 403.");
        using HttpClient client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        using HttpResponseMessage response = await client.GetAsync("/Memory");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains(
            "The shared notebook could not be listed safely.",
            body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HTTP 403", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MemoryPageRendersForBrowsersWhileTheApiStaysProtected()
    {
        using AssessmentApiFactory factory = new(new("failed", []));
        using HttpClient client = factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        using HttpResponseMessage page = await client.GetAsync("/Memory");
        using HttpResponseMessage api = await client.PostAsJsonAsync(
            "/api/case-pattern-assessments",
            Request());

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, api.StatusCode);
        Assert.Equal(0, factory.Agent.CallCount);
        Assert.Empty(factory.Memory.Records);
    }

    private const string BaselineReasonCode =
        "supplier-assurance-evidence-missing-before-payment";

    private static string[] ScenarioOrder(string body) =>
        System.Text.RegularExpressions.Regex.Matches(
                body,
                "data-scenario-id=\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .ToArray();

    private static async Task<string> PostInvestigationAsync(HttpClient client)
    {
        using HttpResponseMessage page = await client.GetAsync("/");
        string html = await page.Content.ReadAsStringAsync();
        System.Text.RegularExpressions.Match token =
            System.Text.RegularExpressions.Regex.Match(
                html,
                "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success, "The investigation form had no request verification token.");
        using FormUrlEncodedContent form = new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token.Groups[1].Value,
            ["ScenarioId"] = TestData.BaselineScenarioId,
            ["Prompt"] = "Assess the configured supplier assurance release control."
        });
        using HttpResponseMessage response = await client.PostAsync(
            "/?handler=Investigate",
            form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static string SearchJson(NotebookRecordEnvelope record)
    {
        DateTimeOffset updatedAt = TestData.Now.AddHours(-2);
        return System.Text.Json.JsonSerializer.Serialize(
            new Demo4MemorySearchResult(
                true,
                [new("memory-prior-1", updatedAt, record)],
                1,
                updatedAt),
            new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web));
    }

    private static NotebookRecordEnvelope PriorRecord(
        string caseReference,
        string reasonCode)
    {
        ValidatedInvestigationResult seed = TestData.Result(
            InvestigationOutcome.Completed,
            []);
        return NotebookRecordCodec.Create(
            seed with
            {
                InvestigationReference = "investigation-prior-1",
                Assessment = seed.Assessment with
                {
                    CaseReference = caseReference,
                    ReasonCode = reasonCode
                }
            },
            TestData.Now.AddDays(-1));
    }

    private static CasePatternAssessmentRequest Request() => new(
        "investigation-1001",
        TestData.BaselineScenarioId,
        "Assess the configured supplier assurance release control.");

    private static string FixturePath(string fileName) =>
        Path.Combine(
            AppContext.BaseDirectory,
            "data",
            "hosted",
            "v2",
            fileName);

    private static CaseInvestigationToolProvider CreateProvider(
        CaseScenarioCatalog scenarios) =>
        new(
            scenarios,
            new CaseContextCatalog(FixturePath("context-fixture-set.json")));

    private static ValidatedInvestigationResult AtomicResult()
    {
        ValidatedInvestigationResult seed = TestData.Result(
            InvestigationOutcome.Completed,
            []);
        return seed with
        {
            ToolExecutions =
            [
                new(
                    "call-assessment-1",
                    "assess_case_pattern",
                    true,
                    AtomicAssessmentValidator.SerializeAssessment(seed.Assessment))
            ]
        };
    }

    private sealed class AssessmentApiFactory(HostedAgentTurnResult turn)
        : WebApplicationFactory<global::Program>
    {
        public StubHostedAgentClient Agent { get; } = new(turn);

        public RecordingMemoryClient Memory { get; } = new();

        public HttpClient CreateAuthenticatedClient()
        {
            HttpClient client = CreateClient(new()
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false
            });
            client.DefaultRequestHeaders.Add("X-Test-Identity", "authenticated");
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AzureAd:ClientId"] = "11111111-1111-1111-1111-111111111111",
                    ["AzureAd:TenantId"] = "22222222-2222-2222-2222-222222222222",
                    ["FOUNDRY_PROJECT_ENDPOINT"] =
                        "https://demo.services.ai.azure.com/api/projects/control-investigations",
                    ["DEMO4_HOSTED_AGENT_ENDPOINT"] =
                        "https://demo.services.ai.azure.com/api/projects/control-investigations/applications/demo4-hosted-agent/protocols/openai/responses?api-version=v1"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHostedCasePatternAgentClient>();
                services.RemoveAll<IFoundryMemoryItemClient>();
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<IHostedCasePatternAgentClient>(Agent);
                services.AddSingleton<IFoundryMemoryItemClient>(Memory);
                services.AddSingleton<TimeProvider>(
                    new FixedTimeProvider(TestData.Now));
                services
                    .AddAuthentication(TestAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName,
                        _ => { });
            });
        }
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

        public Exception? Failure { get; set; }

        public Exception? ListFailure { get; set; }

        public int RejectedItemCount { get; set; }

        public Task CreateAndVerifyAsync(
            NotebookRecordEnvelope record,
            string serializedRecord,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<MemoryNotebookListing> ListValidRecordsAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ListFailure is not null)
            {
                return Task.FromException<MemoryNotebookListing>(ListFailure);
            }

            return Task.FromResult(new MemoryNotebookListing(
                Records.Select(record => new MemoryNotebookEntry(
                    $"memory-{record.RecordId}",
                    now,
                    record)).ToArray(),
                Records.Count + RejectedItemCount,
                RejectedItemCount));
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Demo4TestIdentity";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Test-Identity"))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            ClaimsPrincipal principal = new(
                new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "demo4-test-caller")],
                    SchemeName));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
