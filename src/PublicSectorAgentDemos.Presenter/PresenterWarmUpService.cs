using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;

namespace PublicSectorAgentDemos.Presenter;

public interface IFoundryBearerTokenProvider
{
    public ValueTask<string> GetTokenAsync(CancellationToken cancellationToken);
}

public sealed class FoundryBearerTokenProvider(Func<TokenCredential> credentialFactory) : IFoundryBearerTokenProvider
{
    private static readonly TokenRequestContext TokenContext =
        new(["https://ai.azure.com/.default"]);
    private readonly Lazy<TokenCredential> _credential = new(
        credentialFactory,
        LazyThreadSafetyMode.ExecutionAndPublication);

    public async ValueTask<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        AccessToken token = await _credential.Value.GetTokenAsync(TokenContext, cancellationToken);
        return token.Token;
    }
}

public enum WarmUpOutcome
{
    Success,
    NotRunning,
    NotConfigured,
    Failure
}

public sealed record WarmUpResult(
    string SessionId,
    string Title,
    WarmUpOutcome Outcome,
    string Message,
    bool Extra = false);

public sealed class PresenterWarmUpService(
    HttpClient httpClient,
    IFoundryBearerTokenProvider tokenProvider)
{
    public async Task<IReadOnlyList<WarmUpResult>> WarmAsync(
        PresenterSessionCatalog catalog,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Task<WarmUpResult>[] tasks = catalog.Sessions
            .Select(session => WarmSessionAsync(session, cancellationToken))
            .Concat((catalog.Extras ?? []).Select(async session =>
                (await WarmSessionAsync(session, cancellationToken)) with { Extra = true }))
            .ToArray();
        return await Task.WhenAll(tasks);
    }

    private async Task<WarmUpResult> WarmSessionAsync(
        PresenterSession session,
        CancellationToken cancellationToken)
    {
        if (!session.Configured)
        {
            if (session.StartupStatus == "failed")
            {
                return new(session.Id, session.Title, WarmUpOutcome.Failure,
                    "Optional application startup failed. Review the masked startup logs.");
            }
            return new(session.Id, session.Title, WarmUpOutcome.NotConfigured,
                "The optional external application is not configured.");
        }

        try
        {
            using HttpRequestMessage request = await CreateRequestAsync(
                session.WarmUp,
                cancellationToken);
            using HttpResponseMessage response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            bool ready = session.WarmUp.Kind == "tokens-local"
                ? response.StatusCode == HttpStatusCode.OK &&
                    await IsLocalManifestAsync(response, cancellationToken)
                : IsAcceptableStatusCode(response.StatusCode);
            return ready
                ? new(session.Id, session.Title, WarmUpOutcome.Success, "Ready.")
                : new(
                    session.Id,
                    session.Title,
                    WarmUpOutcome.Failure,
                    $"Warm-up returned HTTP {(int)response.StatusCode} or an invalid readiness response.");
        }
        catch (HttpRequestException exception) when (
            IsLocal(session.WarmUp.Endpoint) &&
            exception.StatusCode is null)
        {
            return new(
                session.Id,
                session.Title,
                WarmUpOutcome.NotRunning,
                "The local app is not running.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(session.Id, session.Title, WarmUpOutcome.Failure, "Warm-up timed out.");
        }
        catch (Exception)
        {
            return new(session.Id, session.Title, WarmUpOutcome.Failure, "Warm-up failed.");
        }
    }

    private static async Task<bool> IsLocalManifestAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
        JsonElement root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("origin", out JsonElement origin) &&
            origin.ValueKind == JsonValueKind.String && origin.GetString() == "local-static-embedding" &&
            root.TryGetProperty("dimensions", out JsonElement dimensions) &&
            dimensions.ValueKind == JsonValueKind.Number && dimensions.TryGetInt32(out int dimensionCount) && dimensionCount > 0 &&
            root.TryGetProperty("vocabularyCount", out JsonElement vocabulary) &&
            vocabulary.ValueKind == JsonValueKind.Number && vocabulary.TryGetInt32(out int wordCount) && wordCount > 0;
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        PresenterWarmUpTarget target,
        CancellationToken cancellationToken)
    {
        HttpRequestMessage request;
        if (target.Kind == "foundry")
        {
            request = new(HttpMethod.Post, target.Endpoint)
            {
                Content = JsonContent.Create(
                    new
                    {
                        model = target.ModelDeployment,
                        input = "Reply with OK.",
                        max_output_tokens = 16
                    })
            };
            string token = await tokenProvider.GetTokenAsync(cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            request = new(HttpMethod.Get, target.Endpoint);
        }

        request.Headers.UserAgent.ParseAdd("PublicSectorAgentDemos-Presenter/1.0");
        return request;
    }

    private static bool IsLocal(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) &&
        string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);

    // A cloud-hosted app behind EasyAuth (for example, the external Act deployment) answers an
    // unauthenticated warm-up request with a sign-in redirect. Accept HTTP 2xx and 3xx, consistent
    // with the readiness checks in Invoke-DemoReady.ps1's Wait-DemoReadyEndpoint.
    private static bool IsAcceptableStatusCode(HttpStatusCode statusCode)
    {
        int code = (int)statusCode;
        return code is >= 200 and < 400;
    }
}
