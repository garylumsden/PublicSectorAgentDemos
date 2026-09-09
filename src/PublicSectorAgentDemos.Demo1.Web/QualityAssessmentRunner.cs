using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Azure.Identity;
using PublicSectorAgentDemos.Observability;

namespace PublicSectorAgentDemos.Demo1.Web;

public sealed record AssessmentRunResult(string State, QualityAssessment? Assessment = null, string? Error = null);

public sealed class QualityAssessmentRunner(
    IQualityAssessmentClient client,
    QualityAssessmentSettings settings,
    ILogger<QualityAssessmentRunner> logger) : IDisposable
{
    private readonly SemaphoreSlim slot = new(DemoLimits.ConcurrentAssessments);

    public bool TryEnter() => slot.Wait(0);
    public void Exit() => slot.Release();

    public async Task<AssessmentRunResult> RunAsync(ComparisonSnapshot snapshot, CancellationToken cancellationToken)
    {
        using Activity? activity = DemoTelemetry.StartActivity("demo1.compare.assess-quality");
        activity?.SetTag("demo.assessment.model", settings.Model);
        activity?.SetTag("demo.assessment.rubric", QualityAssessmentRubric.Version);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DemoLimits.AssessmentTimeout);
        try
        {
            QualityAssessment assessment = await client.AssessAsync(snapshot, timeout.Token);
            activity?.SetTag("demo.assessment.duration_ms", assessment.ElapsedMs);
            activity?.SetTag("demo.assessment.outcome", "completed");
            return new("completed", assessment);
        }
        catch (AssessmentInputTooLargeException)
        {
            activity?.SetTag("demo.assessment.outcome", "input-too-large");
            return new("input-too-large", Error: "The two answers exceed the 128 KiB assessment input limit.");
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            string state = cancellationToken.IsCancellationRequested ? "cancelled" : "timed-out";
            activity?.SetTag("demo.assessment.outcome", state);
            logger.LogInformation("Quality assessment ended: {Outcome}.", state);
            return new(state, Error: state == "cancelled" ? "Assessment cancelled." : "The assessment exceeded the 90-second limit.");
        }
        catch (Exception exception) when (exception is AuthenticationFailedException or HttpRequestException or
            JsonException or IOException or InvalidDataException or DecoderFallbackException)
        {
            activity?.SetTag("demo.assessment.outcome", "failed");
            logger.LogWarning("Quality assessment failed: {FailureType}: {FailureMessage}.", exception.GetType().Name, exception.Message);
            return new("failed", Error: "The quality assessment failed. The original answers remain available.");
        }
    }

    public void Dispose() => slot.Dispose();
}
