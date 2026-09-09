using System.Collections.Concurrent;
using System.Net;
using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PublicSectorAgentDemos.Presenter.Tests;

public sealed class PresenterIdentityTests
{
    [Fact]
    public void SharedIdentityRegistrationUsesOneDefaultAzureCredential()
    {
        using WebApplicationFactory<Program> factory = new();
        TokenCredential credential = factory.Services.GetRequiredService<TokenCredential>();

        Assert.IsType<DefaultAzureCredential>(credential);
        Assert.Same(credential, factory.Services.GetRequiredService<TokenCredential>());
    }

    [Fact]
    public async Task LocalWarmUpDoesNotResolveTheSharedCredentialAndFoundryReusesIt()
    {
        int resolutions = 0;
        RecordingCredential credential = new();
        using WebApplicationFactory<Program> application = new();
        using WebApplicationFactory<Program> factory = application.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TokenCredential>();
                services.AddSingleton<TokenCredential>(_ =>
                {
                    Interlocked.Increment(ref resolutions);
                    return credential;
                });
            }));
        IFoundryBearerTokenProvider provider = factory.Services.GetRequiredService<IFoundryBearerTokenProvider>();
        Assert.Equal(0, resolutions);
        RecordingHandler handler = new();
        using HttpClient httpClient = new(handler);
        PresenterWarmUpService service = new(httpClient, provider);

        IReadOnlyList<WarmUpResult> local = await service.WarmAsync(new(1,
        [
            Session("local", "local-app", "http://localhost:5101/")
        ],
        [
            Session("tokens", "tokens-local", "http://localhost:5041/api/embeddings/manifest")
        ]), CancellationToken.None);

        Assert.All(local, result => Assert.Equal(WarmUpOutcome.Success, result.Outcome));
        Assert.Equal(0, resolutions);
        Assert.Empty(credential.Scopes);
        Assert.All(handler.Requests, request => Assert.Null(request.Authorization));

        IReadOnlyList<WarmUpResult> foundry = await service.WarmAsync(new(1,
        [
            Session("foundation", "foundry", "https://demo.services.ai.azure.com/openai/v1/responses", "model-a"),
            Session("ground", "foundry", "https://demo.services.ai.azure.com/openai/v1/responses", "model-a")
        ]), CancellationToken.None);

        Assert.All(foundry, result => Assert.Equal(WarmUpOutcome.Success, result.Outcome));
        Assert.Equal(1, resolutions);
        Assert.Same(credential, factory.Services.GetRequiredService<TokenCredential>());
        Assert.Equal(2, credential.Scopes.Count);
        Assert.All(credential.Scopes, scope => Assert.Equal("https://ai.azure.com/.default", scope));
        Assert.All(handler.Requests.Where(request => request.Host != "localhost"),
            request => Assert.Equal("Bearer test-shared-token", request.Authorization));
    }

    private static PresenterSession Session(string id, string kind, string endpoint, string? model = null) =>
        new(id, id, "Proof.", endpoint, "Input.", "Note.", "Saved result.", false, new(kind, endpoint, model));

    private sealed class RecordingCredential : TokenCredential
    {
        public ConcurrentQueue<string> Scopes { get; } = new();

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string scope in requestContext.Scopes) Scopes.Enqueue(scope);
            return new("test-shared-token", DateTimeOffset.UtcNow.AddMinutes(5));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public ConcurrentQueue<(string Host, string? Authorization)> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.RequestUri!.Host, request.Headers.Authorization?.ToString()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"origin":"local-static-embedding","dimensions":50,"vocabularyCount":10000}""")
            });
        }
    }
}
