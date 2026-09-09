using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using PublicSectorAgentDemos.Demo1.FoundationAndGround;

namespace PublicSectorAgentDemos.Demo1.Web.Tests;

public sealed class AppRouteTests
{
    [Fact]
    public void SharedIdentityRegistrationUsesOneDefaultAzureCredential()
    {
        using WebTestFactory factory = new();
        TokenCredential credential = factory.Services.GetRequiredService<TokenCredential>();

        Assert.IsType<DefaultAzureCredential>(credential);
        Assert.Same(credential, factory.Services.GetRequiredService<TokenCredential>());
    }

    [Fact]
    public async Task HomeHealthAndSessionUseTheRealRoutesAndExactFixture()
    {
        using WebTestFactory factory = new();
        using HttpClient client = factory.LocalClient();
        using HttpResponseMessage health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Contains("local-only", await health.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using HttpResponseMessage home = await client.GetAsync("/");
        string html = await home.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("foundation-original", html, StringComparison.Ordinal);
        Assert.Contains("ground-original", html, StringComparison.Ordinal);
        Assert.Contains("assess-quality", html, StringComparison.Ordinal);
        Assert.Contains("AI-generated assessment", html, StringComparison.Ordinal);
        Assert.Contains("not approved financial advice", html, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", home.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", home.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("nosniff", home.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(home.Headers.CacheControl!.NoStore);
        using HttpResponseMessage script = await client.GetAsync("/app.js");
        using HttpResponseMessage style = await client.GetAsync("/app.css");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal(HttpStatusCode.OK, style.StatusCode);

        using HttpResponseMessage sessionResponse = await client.GetAsync("/api/session");
        using JsonDocument session = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PublicSectorAgentDemos.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        using JsonDocument fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName,
            "data", "ground", "v1", "fixture-set.json")));
        string expected = fixtures.RootElement.GetProperty("fixtures").EnumerateArray()
            .Single(item => item.GetProperty("scenarioId").GetString() == "MPM-005").GetProperty("prompt").GetString()!;
        Assert.Equal(expected, session.RootElement.GetProperty("prompt").GetString());
        Assert.Equal(8000, session.RootElement.GetProperty("maxPromptCharacters").GetInt32());
        JsonElement agents = session.RootElement.GetProperty("agents");
        Assert.Equal("gpt-5-mini", agents.GetProperty("model").GetString());
        Assert.Equal("low", agents.GetProperty("foundationReasoning").GetString());
        Assert.Equal("low", agents.GetProperty("groundReasoning").GetString());
        Assert.Equal("minimal", agents.GetProperty("groundRetrievalReasoning").GetString());
        Assert.True(agents.GetProperty("timingComparable").GetBoolean());
        Assert.Contains("matched model reasoning settings", agents.GetProperty("timingExplanation").GetString(), StringComparison.Ordinal);
        JsonElement assessment = session.RootElement.GetProperty("assessment");
        Assert.True(assessment.GetProperty("available").GetBoolean());
        Assert.Equal("gpt-5-mini", assessment.GetProperty("model").GetString());
        Assert.Equal(90, assessment.GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal(131072, assessment.GetProperty("inputBytes").GetInt32());
        string cookie = sessionResponse.Headers.GetValues("Set-Cookie").Single();
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(factory.Agent.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BothAgentsStartBeforeEitherCompletesAndEachOutcomeStreamsIndependently(bool groundFirst)
    {
        using WebTestFactory factory = new();
        using HttpClient client = factory.LocalClient();
        string token = await WebTestFactory.SessionTokenAsync(client);
        const string prompt = "Unchanged prompt with trailing whitespace.\r\n  ";
        using HttpRequestMessage request = WebTestFactory.Compare(token, prompt);
        Task<HttpResponseMessage> pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await factory.Agent.WaitForCallsAsync(2);
        PendingAgentCall[] calls = factory.Agent.Calls.ToArray();
        Assert.All(calls, call => Assert.Equal(prompt, call.Prompt));
        Assert.Contains(calls, call => call.AgentName == Demo1AgentCatalog.FoundationAgentName);
        Assert.Contains(calls, call => call.AgentName == Demo1AgentCatalog.GroundAgentName);
        Assert.All(calls, call => Assert.False(call.Completion.Task.IsCompleted));
        string firstName = groundFirst ? Demo1AgentCatalog.GroundAgentName : Demo1AgentCatalog.FoundationAgentName;
        PendingAgentCall first = calls.Single(call => call.AgentName == firstName);
        PendingAgentCall second = calls.Single(call => call.AgentName != firstName);
        first.Complete("> exact quotation\n<script>alert('untrusted')</script>");
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("application/x-ndjson", response.Content.Headers.ContentType!.MediaType);
        using StreamReader reader = new(await response.Content.ReadAsStreamAsync());
        using JsonDocument firstUpdate = JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
        Assert.Equal(groundFirst ? "ground" : "foundation", firstUpdate.RootElement.GetProperty("agent").GetString());
        Assert.Equal("completed", firstUpdate.RootElement.GetProperty("state").GetString());
        string comparisonId = firstUpdate.RootElement.GetProperty("comparisonId").GetString()!;
        Assert.Equal(32, comparisonId.Length);
        JsonElement answer = firstUpdate.RootElement.GetProperty("answer");
        Assert.Contains("<script>", answer.GetProperty("originalText").GetString()!, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", answer.GetProperty("renderedHtml").GetString()!, StringComparison.Ordinal);
        Assert.Contains("<blockquote>exact quotation</blockquote>", answer.GetProperty("renderedHtml").GetString()!, StringComparison.Ordinal);
        Assert.False(second.Completion.Task.IsCompleted);
        second.Completion.TrySetException(new HttpRequestException("Sensitive upstream detail must not appear."));
        string nextLine = (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!;
        using JsonDocument secondUpdate = JsonDocument.Parse(nextLine);
        Assert.Equal(groundFirst ? "foundation" : "ground", secondUpdate.RootElement.GetProperty("agent").GetString());
        Assert.Equal("failed", secondUpdate.RootElement.GetProperty("state").GetString());
        Assert.Equal(comparisonId, secondUpdate.RootElement.GetProperty("comparisonId").GetString());
        Assert.DoesNotContain("Sensitive upstream detail", nextLine, StringComparison.Ordinal);
        Assert.True(secondUpdate.RootElement.GetProperty("elapsedMs").GetInt64() >= 0);
        Assert.Null(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task AFailedAgentDoesNotWaitForOrCancelTheOtherAgent()
    {
        using WebTestFactory factory = new();
        using HttpClient client = factory.LocalClient();
        using HttpRequestMessage request = WebTestFactory.Compare(await WebTestFactory.SessionTokenAsync(client));
        Task<HttpResponseMessage> pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await factory.Agent.WaitForCallsAsync(2);
        PendingAgentCall[] calls = factory.Agent.Calls.ToArray();
        calls[0].Completion.TrySetException(new InvalidDataException("Invalid upstream content."));
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        using StreamReader reader = new(await response.Content.ReadAsStreamAsync());
        using JsonDocument failed = JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
        Assert.Equal("failed", failed.RootElement.GetProperty("state").GetString());
        Assert.False(calls[1].Completion.Task.IsCompleted);
        Assert.False(calls[1].CancellationObserved.Task.IsCompleted);
        calls[1].Complete();
        using JsonDocument completed = JsonDocument.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
        Assert.Equal("completed", completed.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public async Task CancellationReachesBothPendingAgentInvocations()
    {
        using WebTestFactory factory = new();
        using HttpClient client = factory.LocalClient();
        using HttpRequestMessage request = WebTestFactory.Compare(await WebTestFactory.SessionTokenAsync(client));
        using CancellationTokenSource cancellation = new();
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancellation.Token);
        await factory.Agent.WaitForCallsAsync(2);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await Task.WhenAll(factory.Agent.Calls.Select(call => call.CancellationObserved.Task)).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task OnlyTwoComparisonsCanRunAndSlotsAreReleasedAfterCancellation()
    {
        using WebTestFactory factory = new();
        using HttpClient client = factory.LocalClient();
        string token = await WebTestFactory.SessionTokenAsync(client);
        using CancellationTokenSource cancellation = new();
        using HttpRequestMessage first = WebTestFactory.Compare(token);
        using HttpRequestMessage second = WebTestFactory.Compare(token);
        Task<HttpResponseMessage> firstPending = client.SendAsync(first, cancellation.Token);
        Task<HttpResponseMessage> secondPending = client.SendAsync(second, cancellation.Token);
        await factory.Agent.WaitForCallsAsync(4);
        using HttpRequestMessage third = WebTestFactory.Compare(token);
        using HttpResponseMessage rejected = await client.SendAsync(third);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(5), rejected.Headers.RetryAfter!.Delta);
        Assert.Equal(4, factory.Agent.Calls.Count);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstPending);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondPending);
        await Task.WhenAll(factory.Agent.Calls.Select(call => call.CancellationObserved.Task)).WaitAsync(TimeSpan.FromSeconds(10));
        using HttpRequestMessage retry = WebTestFactory.Compare(token);
        Task<HttpResponseMessage> retryPending = client.SendAsync(retry);
        await factory.Agent.WaitForCallsAsync(2);
        foreach (PendingAgentCall call in factory.Agent.Calls.Skip(4)) call.Complete();
        using HttpResponseMessage retried = await retryPending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n ")]
    public async Task EmptyPromptsAreRejected(string? prompt)
    {
        using WebTestFactory factory = new();
        using HttpClient client = factory.LocalClient();
        using HttpRequestMessage request = WebTestFactory.Compare(await WebTestFactory.SessionTokenAsync(client), prompt);
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Agent.Calls);
    }

    [Theory]
    [InlineData(8001, HttpStatusCode.BadRequest)]
    [InlineData(33000, HttpStatusCode.RequestEntityTooLarge)]
    public async Task OversizedPromptsAndBodiesAreRejected(int length, HttpStatusCode expected)
    {
        using WebTestFactory factory = new();
        using HttpClient client = factory.LocalClient();
        using HttpRequestMessage request = WebTestFactory.Compare(await WebTestFactory.SessionTokenAsync(client), new string('x', length));
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(factory.Agent.Calls);
    }

    [Fact]
    public async Task ExactlyEightThousandCharactersAreAcceptedWithoutTrimming()
    {
        using WebTestFactory factory = new();
        using HttpClient client = factory.LocalClient();
        string prompt = new('x', 8000);
        using HttpRequestMessage request = WebTestFactory.Compare(await WebTestFactory.SessionTokenAsync(client), prompt);
        Task<HttpResponseMessage> pending = client.SendAsync(request);
        await factory.Agent.WaitForCallsAsync(2);
        foreach (PendingAgentCall call in factory.Agent.Calls)
        {
            Assert.Equal(prompt, call.Prompt);
            call.Complete();
        }
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("{", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("null", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("{\"prompt\":42}", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("{\"prompt\":\"hi\",\"agent\":\"another-agent\"}", "application/json", HttpStatusCode.BadRequest)]
    [InlineData("{\"prompt\":\"hi\"}", "text/plain", HttpStatusCode.UnsupportedMediaType)]
    public async Task InvalidPayloadsAreRejected(string body, string contentType, HttpStatusCode expected)
    {
        using WebTestFactory factory = new();
        using HttpClient client = factory.LocalClient();
        using HttpRequestMessage request = WebTestFactory.Compare(await WebTestFactory.SessionTokenAsync(client));
        request.Content = new StringContent(body, Encoding.UTF8, contentType);
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(factory.Agent.Calls);
    }

    [Theory]
    [InlineData("http://attacker.example", "same-origin", true, HttpStatusCode.Forbidden)]
    [InlineData("null", "same-origin", true, HttpStatusCode.Forbidden)]
    [InlineData(null, "same-origin", true, HttpStatusCode.Forbidden)]
    [InlineData("http://localhost:5090", "cross-site", true, HttpStatusCode.Forbidden)]
    [InlineData("http://localhost:5090", "same-origin", false, HttpStatusCode.BadRequest)]
    public async Task OriginAndAntiforgeryAreRequired(string? origin, string fetchSite, bool tokenPresent, HttpStatusCode expected)
    {
        using WebTestFactory factory = new();
        using HttpClient client = factory.LocalClient();
        using HttpRequestMessage request = WebTestFactory.Compare(await WebTestFactory.SessionTokenAsync(client));
        request.Headers.Remove("Origin");
        request.Headers.Remove("Sec-Fetch-Site");
        request.Headers.Add("Sec-Fetch-Site", fetchSite);
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (!tokenPresent) request.Headers.Remove("X-Demo1-CSRF");
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
        Assert.Empty(factory.Agent.Calls);
    }

    [Fact]
    public async Task QualityAssessmentUsesTheExactSnapshotAndCachesTheSuccessfulResult()
    {
        using WebTestFactory factory = new();
        using HttpClient client = factory.LocalClient();
        string token = await WebTestFactory.SessionTokenAsync(client);
        const string prompt = "A custom assessment prompt.";
        string comparisonId = await CompleteComparisonAsync(factory, client, token, prompt,
            "Foundation exact answer.", "Ground exact answer.");

        using HttpRequestMessage request = WebTestFactory.Assess(token, comparisonId);
        Task<HttpResponseMessage> pending = client.SendAsync(request);
        PendingAssessmentCall call = await factory.Assessment.WaitForCallAsync();
        Assert.Equal(prompt, call.Snapshot.Prompt);
        Assert.Equal("Foundation exact answer.", call.Snapshot.Foundation.OriginalText);
        Assert.Equal("Ground exact answer.", call.Snapshot.Ground.OriginalText);
        Assert.Contains("Custom prompt", call.Snapshot.Reference.Basis, StringComparison.Ordinal);
        call.Complete();
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("completed", result.RootElement.GetProperty("state").GetString());
        JsonElement assessment = result.RootElement.GetProperty("assessment");
        Assert.Equal(5, assessment.GetProperty("criteria").GetArrayLength());
        Assert.All(assessment.GetProperty("criteria").EnumerateArray(), criterion =>
            Assert.Equal(1, criterion.GetProperty("difference").GetInt32()));
        Assert.False(assessment.GetProperty("cached").GetBoolean());

        using HttpRequestMessage repeat = WebTestFactory.Assess(token, comparisonId);
        using HttpResponseMessage repeated = await client.SendAsync(repeat);
        using JsonDocument cached = JsonDocument.Parse(await repeated.Content.ReadAsStringAsync());
        Assert.True(cached.RootElement.GetProperty("assessment").GetProperty("cached").GetBoolean());
        Assert.Single(factory.Assessment.Calls);
        Assert.Equal(2, factory.Agent.Calls.Count);
    }

    [Fact]
    public async Task OversizedQualityAssessmentRequestReturnsPayloadTooLarge()
    {
        using WebTestFactory factory = new();
        using HttpClient client = factory.LocalClient();
        string token = await WebTestFactory.SessionTokenAsync(client);
        using HttpRequestMessage request = WebTestFactory.Assess(token, new string('a', 32));
        request.Content = new StringContent(new string('x', DemoLimits.RequestBytes + 1), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(factory.Assessment.Calls);
    }

    [Fact]
    public async Task QualityAssessmentRequiresTheOwningBrowserSession()
    {
        using WebTestFactory factory = new();
        using HttpClient owner = factory.LocalClient();
        string ownerToken = await WebTestFactory.SessionTokenAsync(owner);
        string comparisonId = await CompleteComparisonAsync(factory, owner, ownerToken,
            "Owned prompt.", "Foundation.", "Ground.");

        using HttpClient other = factory.LocalClient();
        string otherToken = await WebTestFactory.SessionTokenAsync(other);
        using HttpRequestMessage request = WebTestFactory.Assess(otherToken, comparisonId);
        using HttpResponseMessage response = await other.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Assessment.Calls);
    }

    [Fact]
    public async Task IncompleteAnswerPairCannotBeAssessed()
    {
        using WebTestFactory factory = new();
        using HttpClient client = factory.LocalClient();
        string token = await WebTestFactory.SessionTokenAsync(client);
        using HttpRequestMessage compare = WebTestFactory.Compare(token, "Prompt.");
        Task<HttpResponseMessage> pending = client.SendAsync(compare);
        await factory.Agent.WaitForCallsAsync(2);
        PendingAgentCall[] calls = factory.Agent.Calls.ToArray();
        calls.Single(call => call.AgentName == Demo1AgentCatalog.FoundationAgentName).CompleteWithStatus("incomplete", "Partial.");
        calls.Single(call => call.AgentName == Demo1AgentCatalog.GroundAgentName).Complete("Complete.");
        using HttpResponseMessage comparison = await pending;
        string[] lines = (await comparison.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        using JsonDocument update = JsonDocument.Parse(lines[0]);
        string comparisonId = update.RootElement.GetProperty("comparisonId").GetString()!;
        using HttpRequestMessage assess = WebTestFactory.Assess(token, comparisonId);
        using HttpResponseMessage response = await client.SendAsync(assess);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(factory.Assessment.Calls);
    }

    [Fact]
    public async Task AssessmentCancellationReachesTheJudgeAndReleasesCapacity()
    {
        using WebTestFactory factory = new();
        using HttpClient client = factory.LocalClient();
        string token = await WebTestFactory.SessionTokenAsync(client);
        string comparisonId = await CompleteComparisonAsync(factory, client, token,
            "Prompt.", "Foundation.", "Ground.");
        using CancellationTokenSource cancellation = new();
        using HttpRequestMessage request = WebTestFactory.Assess(token, comparisonId);
        Task<HttpResponseMessage> pending = client.SendAsync(request, cancellation.Token);
        PendingAssessmentCall call = await factory.Assessment.WaitForCallAsync();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await call.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using HttpRequestMessage retry = WebTestFactory.Assess(token, comparisonId);
        Task<HttpResponseMessage> retryPending = client.SendAsync(retry);
        PendingAssessmentCall retryCall = await factory.Assessment.WaitForCallAsync();
        retryCall.Complete();
        using HttpResponseMessage response = await retryPending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<string> CompleteComparisonAsync(
        WebTestFactory factory,
        HttpClient client,
        string token,
        string prompt,
        string foundation,
        string ground)
    {
        using HttpRequestMessage request = WebTestFactory.Compare(token, prompt);
        Task<HttpResponseMessage> pending = client.SendAsync(request);
        await factory.Agent.WaitForCallsAsync(2);
        PendingAgentCall[] calls = factory.Agent.Calls.ToArray();
        calls.Single(call => call.AgentName == Demo1AgentCatalog.FoundationAgentName).Complete(foundation);
        calls.Single(call => call.AgentName == Demo1AgentCatalog.GroundAgentName).Complete(ground);
        using HttpResponseMessage response = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        response.EnsureSuccessStatusCode();
        string[] lines = (await response.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using JsonDocument first = JsonDocument.Parse(lines[0]);
        using JsonDocument second = JsonDocument.Parse(lines[1]);
        string id = first.RootElement.GetProperty("comparisonId").GetString()!;
        Assert.Equal(id, second.RootElement.GetProperty("comparisonId").GetString());
        return id;
    }

    [Theory]
    [InlineData("evil.example:5090")]
    [InlineData("localhost.evil.example:5090")]
    [InlineData("localhost.:5090")]
    [InlineData("localhost:5091")]
    [InlineData("localhost")]
    public async Task HostHeaderRebindingIsRejectedOnEverySurface(string host)
    {
        using WebTestFactory factory = new();
        using HttpClient client = factory.LocalClient();
        client.DefaultRequestHeaders.Host = host;
        foreach (string path in new[] { "/", "/health", "/api/session", "/app.js" })
        {
            using HttpResponseMessage response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("192.0.2.1")]
    [InlineData("::ffff:192.0.2.1")]
    [InlineData(null)]
    public async Task NonLoopbackOrMissingPeersAreRejected(string? peer)
    {
        using WebTestFactory factory = new() { RemoteAddress = peer is null ? null : IPAddress.Parse(peer) };
        using HttpClient client = factory.LocalClient();
        using HttpResponseMessage response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1:5090", "127.0.0.1")]
    [InlineData("[::1]:5090", "::1")]
    [InlineData("localhost:5090", "::ffff:127.0.0.1")]
    public async Task ExplicitLoopbackHostsAndPeersAreAccepted(string host, string peer)
    {
        using WebTestFactory factory = new() { RemoteAddress = IPAddress.Parse(peer) };
        using HttpClient client = factory.LocalClient();
        client.DefaultRequestHeaders.Host = host;
        using HttpResponseMessage response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("Forwarded")]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Forwarded-Host")]
    public async Task ProxyHeadersAreRejected(string header)
    {
        using WebTestFactory factory = new();
        using HttpClient client = factory.LocalClient();
        client.DefaultRequestHeaders.Add(header, "127.0.0.1");
        using HttpResponseMessage response = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
