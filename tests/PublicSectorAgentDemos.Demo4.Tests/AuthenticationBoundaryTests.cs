using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PublicSectorAgentDemos.Contracts.Demo4;
using PublicSectorAgentDemos.Demo4.Application;

namespace PublicSectorAgentDemos.Demo4.Tests;

/// <summary>
/// Demo 4 has two authentication layers.
/// App Service EasyAuth v2 gates every browser route except the health probe.
/// Microsoft Identity Web validates bearer tokens for the assessment API.
/// These tests cover the in-process half of that contract.
/// </summary>
public sealed class AuthenticationBoundaryTests
{
    private const string ApiClientId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task TheAssessmentApiRejectsEveryRequestWithoutAValidatedToken()
    {
        using AuthBoundaryFactory factory = new();
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage anonymous = await client.PostAsJsonAsync(
            "/api/case-pattern-assessments",
            Request());

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains(
            anonymous.Headers.WwwAuthenticate,
            header => string.Equals(header.Scheme, "Bearer", StringComparison.Ordinal));
        Assert.Equal(0, factory.Agent.CallCount);
        Assert.Empty(factory.Memory.Records);
    }

    [Fact]
    public async Task TheHealthProbeStaysAnonymousForThePlatform()
    {
        using AuthBoundaryFactory factory = new();
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage response = await client.GetAsync("/health");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("healthy", body, StringComparison.Ordinal);
        Assert.Equal(0, factory.Agent.CallCount);
    }

    [Fact]
    public async Task TheMemoryNotebookAcceptsReadsAndRejectsWrites()
    {
        using AuthBoundaryFactory factory = new();
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage read = await client.GetAsync("/Memory");
        using HttpResponseMessage write = await client.PostAsync(
            "/Memory",
            new FormUrlEncodedContent([]));
        using HttpResponseMessage delete = await client.DeleteAsync("/Memory");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.False(write.IsSuccessStatusCode);
        Assert.False(delete.IsSuccessStatusCode);
        Assert.InRange((int)write.StatusCode, 400, 499);
        Assert.InRange((int)delete.StatusCode, 400, 499);
        Assert.Empty(factory.Memory.Records);
    }

    [Fact]
    public async Task BrowserRoutesStayAvailableToTheEasyAuthCookieSession()
    {
        using AuthBoundaryFactory factory = new();
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage root = await client.GetAsync("/");
        using HttpResponseMessage cases = await client.GetAsync("/Cases");
        using HttpResponseMessage memory = await client.GetAsync("/Memory");
        using HttpResponseMessage style = await client.GetAsync("/css/site.css");

        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Equal(HttpStatusCode.OK, cases.StatusCode);
        Assert.Equal(HttpStatusCode.OK, memory.StatusCode);
        Assert.Equal(HttpStatusCode.OK, style.StatusCode);
        Assert.Equal(0, factory.Agent.CallCount);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Cases")]
    [InlineData("/Memory")]
    public async Task BrowserPagesPreserveSameOriginHeadersForEasyAuth(string path)
    {
        using AuthBoundaryFactory factory = new();
        using HttpClient client = CreateClient(factory);

        using HttpResponseMessage page = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("same-origin", Assert.Single(page.Headers.GetValues("Referrer-Policy")));
        Assert.Contains(
            "form-action 'self'",
            Assert.Single(page.Headers.GetValues("Content-Security-Policy")),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid-antiforgery-token")]
    public async Task InvestigationRejectsMissingOrInvalidAntiforgeryTokens(string? token)
    {
        using AuthBoundaryFactory factory = new();
        using HttpClient client = CreateClient(factory);
        using HttpResponseMessage page = await client.GetAsync("/");
        Dictionary<string, string> fields = new()
        {
            ["ScenarioId"] = TestData.BaselineScenarioId,
            ["Prompt"] = "Assess case CG-8101."
        };
        if (token is not null)
        {
            fields["__RequestVerificationToken"] = token;
        }

        using HttpResponseMessage response = await client.PostAsync(
            "/?handler=Investigate",
            new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, factory.Agent.CallCount);
        Assert.Empty(factory.Memory.Records);
    }

    [Fact]
    public void BearerValidationChecksSignatureIssuerAudienceAndLifetime()
    {
        using AuthBoundaryFactory factory = new();
        JwtBearerOptions options = factory.Services
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.True(options.RequireHttpsMetadata);
        Assert.True(options.TokenValidationParameters.ValidateIssuer);
        Assert.True(options.TokenValidationParameters.ValidateIssuerSigningKey);
        Assert.True(options.TokenValidationParameters.ValidateAudience);
        Assert.True(options.TokenValidationParameters.ValidateLifetime);
        Assert.True(options.TokenValidationParameters.RequireSignedTokens);
        Assert.True(options.TokenValidationParameters.RequireExpirationTime);
        Assert.Equal(TimeSpan.FromMinutes(2), options.TokenValidationParameters.ClockSkew);

        Assert.Equal(
            "https://login.microsoftonline.com/22222222-2222-2222-2222-222222222222/v2.0",
            options.Authority);
        Assert.Equal(ApiClientId, options.Audience);
        Assert.Equal(ApiClientId, options.TokenValidationParameters.ValidAudience);
        Assert.NotNull(options.TokenValidationParameters.AudienceValidator);
        Assert.NotNull(options.TokenValidationParameters.IssuerValidator);
    }

    private static HttpClient CreateClient(AuthBoundaryFactory factory) =>
        factory.CreateClient(new()
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

    private static CasePatternAssessmentRequest Request() => new(
        "D4-auth-boundary",
        TestData.BaselineScenarioId,
        "Assess case CG-8101.");

    private sealed class AuthBoundaryFactory : WebApplicationFactory<global::Program>
    {
        public CountingAgentClient Agent { get; } = new();

        public CountingMemoryClient Memory { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AzureAd:Instance"] = "https://login.microsoftonline.com/",
                    ["AzureAd:TenantId"] = "22222222-2222-2222-2222-222222222222",
                    ["AzureAd:ClientId"] = ApiClientId,
                    ["AzureAd:Audience"] = ApiClientId,
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
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(TestData.Now));
            });
        }
    }

    private sealed class CountingAgentClient : IHostedCasePatternAgentClient
    {
        public int CallCount { get; private set; }

        public Task<HostedAgentTurnResult> InvokeAsync(
            CasePatternAssessmentRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new HostedAgentTurnResult("failed", []));
        }
    }

    private sealed class CountingMemoryClient : IFoundryMemoryItemClient
    {
        public List<NotebookRecordEnvelope> Records { get; } = [];

        public Task CreateAndVerifyAsync(
            NotebookRecordEnvelope record,
            string serializedRecord,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.Add(record);
            return Task.CompletedTask;
        }

        public Task<MemoryNotebookListing> ListValidRecordsAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new MemoryNotebookListing([], 0, 0));
        }
    }
}
