using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PublicSectorAgentDemos.Presenter;

namespace PublicSectorAgentDemos.Presenter.Tests;

public sealed class PresenterWarmUpTests
{
    [Fact]
    public async Task WarmUp_SkipsUnconfiguredPatriotsWithoutNetworkAccess()
    {
        RecordingHandler handler = new(_ => new(HttpStatusCode.OK));
        PresenterWarmUpService service = new(new HttpClient(handler), new StubTokenProvider());
        PresenterSession patriots = new(
            "patriots-coordinate", "Patriots Coordinate", "Proof.", "",
            "Dossier.", "External.", "Saved result.", true,
            new("disabled", "", null), Configured: false);
        patriots.Validate();
        PresenterSessionCatalog catalog = Catalog(
            Session("owned", "Owned", "local-app", "http://localhost:5101/"), patriots);

        IReadOnlyList<WarmUpResult> results = await service.WarmAsync(catalog, CancellationToken.None);

        Assert.Single(handler.Requests);
        Assert.Equal(WarmUpOutcome.Success, results[0].Outcome);
        Assert.Equal(WarmUpOutcome.NotConfigured, results[1].Outcome);
    }

    [Fact]
    public void RequiredSessionsCannotDisableTheirWarmUp()
    {
        PresenterSession session = new(
            "act", "Act", "Proof.", "", "Input.", "Note.", "Saved result.", false,
            new("disabled", "", null), Configured: false);

        Assert.Throws<InvalidDataException>(session.Validate);
    }

    [Fact]
    public async Task WarmUp_UsesConfiguredRequestShapesAndKeepsFailuresIndependent()
    {
        RecordingHandler handler = new(
            request => request.RequestUri!.Port switch
            {
                5101 => new(HttpStatusCode.OK),
                5102 => throw new HttpRequestException("Not running."),
                _ => new(HttpStatusCode.BadGateway)
            });
        PresenterWarmUpService service = new(
            new HttpClient(handler),
            new StubTokenProvider());
        PresenterSessionCatalog catalog = Catalog(
            Session("foundry", "Foundry", "foundry", "https://demo.services.ai.azure.com/openai/v1/responses", "model-a"),
            Session("local", "Local", "local-app", "http://localhost:5101/"),
            Session("stopped", "Stopped", "local-app", "http://localhost:5102/"),
            Session("demo4", "Demo4", "demo4", "http://localhost:5103/health"));

        IReadOnlyList<WarmUpResult> results = await service.WarmAsync(
            catalog,
            CancellationToken.None);

        Assert.Equal(
            [
                WarmUpOutcome.Failure,
                WarmUpOutcome.Success,
                WarmUpOutcome.NotRunning,
                WarmUpOutcome.Failure
            ],
            results.Select(result => result.Outcome));
        Assert.Equal(4, handler.Requests.Count);

        CapturedRequest foundry = handler.Requests.Single(request => request.Uri.Host.StartsWith("demo", StringComparison.Ordinal));
        Assert.Equal(HttpMethod.Post, foundry.Method);
        Assert.Equal(
            "https://demo.services.ai.azure.com/openai/v1/responses",
            foundry.Uri.AbsoluteUri);
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "test-token"), foundry.Authorization);
        using JsonDocument body = JsonDocument.Parse(foundry.Body!);
        Assert.Equal("model-a", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("Reply with OK.", body.RootElement.GetProperty("input").GetString());
        Assert.Equal(16, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal(3, body.RootElement.EnumerateObject().Count());

        Assert.All(
            handler.Requests.Where(request => request.Uri.Host == "localhost"),
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Null(request.Authorization);
                Assert.Null(request.Body);
            });
        Assert.Contains(
            handler.Requests,
            request => request.Uri.AbsoluteUri == "http://localhost:5103/health");
    }

    [Fact]
    public async Task WarmUp_AcceptsCloudAuthenticationRedirects()
    {
        // The external Act deployment sits behind EasyAuth. An unauthenticated warm-up request
        // receives a sign-in redirect. That HTTP 3xx response, not a 2xx, must count as ready.
        RecordingHandler handler = new(
            request => request.RequestUri!.Port switch
            {
                5201 => new(HttpStatusCode.Found),
                5202 => new(HttpStatusCode.NotFound),
                _ => new(HttpStatusCode.BadGateway)
            });
        PresenterWarmUpService service = new(
            new HttpClient(handler),
            new StubTokenProvider());
        PresenterSessionCatalog catalog = Catalog(
            Session("redirect", "Redirect", "local-app", "https://act.example:5201/health"),
            Session("not-found", "NotFound", "local-app", "https://act.example:5202/health"));

        IReadOnlyList<WarmUpResult> results = await service.WarmAsync(catalog, CancellationToken.None);

        Assert.Equal(WarmUpOutcome.Success, results.Single(result => result.SessionId == "redirect").Outcome);
        Assert.Equal(WarmUpOutcome.Failure, results.Single(result => result.SessionId == "not-found").Outcome);
    }

    [Fact]
    public async Task Coordinator_DeduplicatesRepeatedWarmUp()
    {
        RecordingHandler handler = new(_ => new(HttpStatusCode.OK));
        PresenterSessionCatalog catalog = Catalog(
            Session("local", "Local", "local-app", "http://localhost:5101/"));
        PresenterWarmUpCoordinator coordinator = new(
            new PresenterWarmUpService(new HttpClient(handler), new StubTokenProvider()),
            catalog,
            TimeProvider.System);

        WarmUpRunSnapshot first = coordinator.Start();
        WarmUpRunSnapshot second = coordinator.Start();
        await coordinator.WaitForCurrentRunAsync();
        WarmUpRunSnapshot third = coordinator.Start();

        Assert.Equal(first.RunId, second.RunId);
        Assert.Equal(first.RunId, third.RunId);
        Assert.Single(handler.Requests);
        Assert.Equal("completed", coordinator.GetSnapshot().State);
    }

    [Theory]
    [InlineData(200, """{"origin":"local-static-embedding","dimensions":50,"vocabularyCount":10000}""", WarmUpOutcome.Success)]
    [InlineData(200, """{"origin":"cloud","dimensions":50,"vocabularyCount":10000}""", WarmUpOutcome.Failure)]
    [InlineData(200, """{"origin":"local-static-embedding","dimensions":0,"vocabularyCount":10000}""", WarmUpOutcome.Failure)]
    [InlineData(200, """{"origin":"local-static-embedding","dimensions":50,"vocabularyCount":"10000"}""", WarmUpOutcome.Failure)]
    [InlineData(200, "<html>Unrelated application</html>", WarmUpOutcome.Failure)]
    [InlineData(302, "{}", WarmUpOutcome.Failure)]
    [InlineData(503, "{}", WarmUpOutcome.Failure)]
    public async Task TokensExtra_UsesOnlyTheLocalManifestAndKeepsMainResultsIndependent(
        int statusCode, string content, WarmUpOutcome expected)
    {
        RecordingHandler handler = new(request => request.RequestUri!.Port == 5041
            ? new((HttpStatusCode)statusCode) { Content = new StringContent(content) }
            : new(HttpStatusCode.OK));
        PresenterSessionCatalog catalog = Catalog(
            Session("local", "Local", "local-app", "http://localhost:5101/")) with
        {
            Extras = [TokensSession()]
        };
        PresenterWarmUpCoordinator coordinator = new(
            new PresenterWarmUpService(new HttpClient(handler), new RejectingTokenProvider()),
            catalog,
            TimeProvider.System);

        coordinator.Start();
        await coordinator.WaitForCurrentRunAsync();
        WarmUpRunSnapshot snapshot = coordinator.GetSnapshot();

        Assert.Equal("completed", snapshot.State);
        Assert.Equal(WarmUpOutcome.Success, Assert.Single(snapshot.Results, result => !result.Extra).Outcome);
        Assert.Equal(expected, Assert.Single(snapshot.Results, result => result.Extra).Outcome);
        CapturedRequest request = Assert.Single(handler.Requests, request => request.Uri.Port == 5041);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/api/embeddings/manifest", request.Uri.AbsolutePath);
        Assert.Null(request.Authorization);
        Assert.Null(request.Body);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData("not-configured", WarmUpOutcome.NotConfigured)]
    [InlineData("failed", WarmUpOutcome.Failure)]
    public async Task TokensExtra_SkipsUnavailableStartupWithoutNetworkOrCredentials(string status, WarmUpOutcome outcome)
    {
        RecordingHandler handler = new(_ => throw new InvalidOperationException("No HTTP request is permitted."));
        PresenterSession tokens = TokensSession() with
        {
            Configured = false, StartupStatus = status, LaunchUrl = "", WarmUp = new("disabled", "", null)
        };
        tokens.Validate();
        PresenterWarmUpService service = new(new HttpClient(handler), new RejectingTokenProvider());

        IReadOnlyList<WarmUpResult> results = await service.WarmAsync(
            new(1, [], [tokens]), CancellationToken.None);

        Assert.Empty(handler.Requests);
        WarmUpResult result = Assert.Single(results);
        Assert.True(result.Extra);
        Assert.Equal(outcome, result.Outcome);
    }

    [Fact]
    public async Task TokensExtra_StoppedApplicationIsNotReady()
    {
        RecordingHandler handler = new(_ => throw new HttpRequestException("Connection refused."));
        PresenterWarmUpService service = new(new HttpClient(handler), new RejectingTokenProvider());

        IReadOnlyList<WarmUpResult> results = await service.WarmAsync(
            new(1, [], [TokensSession()]), CancellationToken.None);

        Assert.Equal(WarmUpOutcome.NotRunning, Assert.Single(results).Outcome);
    }

    private static PresenterSession TokensSession() =>
        new("tokens-and-credits", "Tokens and Credits", "Proof.", "http://localhost:5041/",
            "Input.", "Note.", "Fallback.", true,
            new("tokens-local", "http://localhost:5041/api/embeddings/manifest", null),
            StartupStatus: "ready");

    private sealed class RejectingTokenProvider : IFoundryBearerTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Local warm-up must not request an Azure token.");
    }

    private static PresenterSessionCatalog Catalog(params PresenterSession[] sessions) =>
        new(1, sessions);

    private static PresenterSession Session(
        string id,
        string title,
        string kind,
        string endpoint,
        string? model = null) =>
        new(
            id,
            title,
            "Proof.",
            endpoint,
            "Exact input.",
            "Tab note.",
            "Saved fallback.",
            false,
            new(kind, endpoint, model));

    private sealed class StubTokenProvider : IFoundryBearerTokenProvider
    {
        public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult("test-token");
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
            {
                Requests.Add(
                    new(
                        request.Method,
                        request.RequestUri!,
                        request.Headers.Authorization,
                        body));
            }
            return responseFactory(request);
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri Uri,
        AuthenticationHeaderValue? Authorization,
        string? Body);
}
