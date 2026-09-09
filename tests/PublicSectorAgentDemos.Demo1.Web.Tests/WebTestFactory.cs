using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PublicSectorAgentDemos.Demo1.Web;

namespace PublicSectorAgentDemos.Demo1.Web.Tests;

internal sealed class WebTestFactory : WebApplicationFactory<Program>
{
    public ControlledAgentClient Agent { get; } = new();
    public ControlledQualityAssessmentClient Assessment { get; } = new();
    public IPAddress? RemoteAddress { get; init; } = IPAddress.Loopback;

    public HttpClient LocalClient() => CreateClient(new()
    {
        BaseAddress = new Uri("http://localhost:5090"),
        AllowAutoRedirect = false
    });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["AZURE_AI_FOUNDRY_ENDPOINT"] = "https://test.services.ai.azure.com/api/projects/test"
            }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAgentClient>();
            services.AddSingleton<IAgentClient>(Agent);
            services.RemoveAll<IQualityAssessmentClient>();
            services.AddSingleton<IQualityAssessmentClient>(Assessment);
            // TestServer has no socket peer. Supply its peer before the real boundary runs.
            services.AddSingleton<IStartupFilter>(new PeerFilter(RemoteAddress));
        });
    }

    public static async Task<string> SessionTokenAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.GetAsync("/api/session");
        response.EnsureSuccessStatusCode();
        using JsonDocument session = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return session.RootElement.GetProperty("requestToken").GetString()!;
    }

    public static HttpRequestMessage Compare(string token, string? prompt = "The exact shared prompt.\n ") =>
        ProtectedPost("/api/compare", token, new { prompt });

    public static HttpRequestMessage Assess(string token, string comparisonId) =>
        ProtectedPost("/api/assess-quality", token, new { comparisonId });

    private static HttpRequestMessage ProtectedPost(string path, string token, object body)
    {
        HttpRequestMessage request = new(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Origin", "http://localhost:5090");
        request.Headers.Add("Sec-Fetch-Site", "same-origin");
        request.Headers.Add("X-Demo1-CSRF", token);
        return request;
    }

    private sealed class PeerFilter(IPAddress? address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Connection.RemoteIpAddress = address;
                return continuation(context);
            });
            next(app);
        };
    }
}

internal sealed class ControlledAgentClient : IAgentClient
{
    private readonly SemaphoreSlim arrivals = new(0);
    public ConcurrentQueue<PendingAgentCall> Calls { get; } = new();

    public async Task<AgentAnswer> InvokeAsync(string agentName, string prompt, CancellationToken cancellationToken)
    {
        PendingAgentCall call = new(agentName, prompt);
        Calls.Enqueue(call);
        arrivals.Release();
        try
        {
            return await call.Completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            call.CancellationObserved.TrySetResult();
            throw;
        }
    }

    public async Task WaitForCallsAsync(int count)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        for (int index = 0; index < count; index++)
        {
            await arrivals.WaitAsync(timeout.Token);
        }
    }
}

internal sealed class ControlledQualityAssessmentClient : IQualityAssessmentClient
{
    private readonly SemaphoreSlim arrivals = new(0);
    public ConcurrentQueue<PendingAssessmentCall> Calls { get; } = new();

    public async Task<QualityAssessment> AssessAsync(ComparisonSnapshot snapshot, CancellationToken cancellationToken)
    {
        PendingAssessmentCall call = new(snapshot);
        Calls.Enqueue(call);
        arrivals.Release();
        try
        {
            return await call.Completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            call.CancellationObserved.TrySetResult();
            throw;
        }
    }

    public async Task<PendingAssessmentCall> WaitForCallAsync()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        await arrivals.WaitAsync(timeout.Token);
        return Calls.Last();
    }
}

internal sealed record PendingAssessmentCall(ComparisonSnapshot Snapshot)
{
    public TaskCompletionSource<QualityAssessment> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Complete() => Completion.TrySetResult(new(
        "Ground is stronger on one criterion; other differences are limited.",
        QualityCriteria.All.Select(id => new CriterionAssessment(id, QualityCriteria.Label(id),
            new(3, "Foundation explanation.", Snapshot.Foundation.OriginalText),
            new(4, "Ground explanation.", Snapshot.Ground.OriginalText), 1)).ToArray(),
        QualityAssessmentRubric.Version, "gpt-5-mini", Snapshot.Reference.Basis,
        Snapshot.Reference.Limitations, 12));
}

internal sealed record PendingAgentCall(string AgentName, string Prompt)
{
    public TaskCompletionSource<AgentAnswer> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Complete(string text = "An original answer.") => CompleteWithStatus("completed", text);

    public void CompleteWithStatus(string status, string text) => Completion.TrySetResult(AgentAnswer.Parse(
        JsonSerializer.Serialize(new
        {
            status,
            output = new[] { new { type = "message", content = new[] { new { type = "output_text", text } } } }
        })));
}
