using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using PublicSectorAgentDemos.Demo1.FoundationAndGround;
using PublicSectorAgentDemos.Observability;

namespace PublicSectorAgentDemos.Demo1.Web;

public sealed record AgentUpdate(string Agent, string State, long ElapsedMs, AgentAnswer? Answer = null, string? Error = null, string? ComparisonId = null);

public sealed class ComparisonRunner(IAgentClient client, ILogger<ComparisonRunner> logger) : IDisposable
{
    private readonly SemaphoreSlim slots = new(DemoLimits.ConcurrentComparisons);

    public bool TryEnter() => slots.Wait(0);

    public void Exit() => slots.Release();

    public Task<AgentUpdate>[] Start(string prompt, CancellationToken cancellationToken) =>
    [
        InvokeAsync("foundation", Demo1AgentCatalog.FoundationAgentName, prompt, cancellationToken),
        InvokeAsync("ground", Demo1AgentCatalog.GroundAgentName, prompt, cancellationToken)
    ];

    private async Task<AgentUpdate> InvokeAsync(string stage, string name, string prompt, CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        using Activity? activity = DemoTelemetry.StartActivity("demo1.compare.agent");
        activity?.SetTag("demo.agent.stage", stage);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DemoLimits.AgentTimeout);
        try
        {
            AgentAnswer answer = await client.InvokeAsync(name, prompt, timeout.Token);
            activity?.SetTag("demo.agent.outcome", "completed");
            return new(stage, "completed", elapsed.ElapsedMilliseconds, answer);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            string state = cancellationToken.IsCancellationRequested ? "cancelled" : "timed-out";
            activity?.SetTag("demo.agent.outcome", state);
            logger.LogInformation("Agent request ended: {Stage}, {Outcome}.", stage, state);
            return new(stage, state, elapsed.ElapsedMilliseconds, Error:
                state == "cancelled" ? "Request cancelled." : "The agent exceeded the 180-second limit.");
        }
        catch (Exception exception) when (exception is AuthenticationFailedException or HttpRequestException or
            JsonException or IOException or InvalidDataException or DecoderFallbackException or OperationCanceledException)
        {
            activity?.SetTag("demo.agent.outcome", "failed");
            logger.LogWarning("Agent request failed: {Stage}, {FailureType}.", stage, exception.GetType().Name);
            return new(stage, "failed", elapsed.ElapsedMilliseconds, Error:
                "The agent request failed. Check the local Azure CLI sign-in, project access, and registered agent availability.");
        }
    }

    public void Dispose() => slots.Dispose();
}
