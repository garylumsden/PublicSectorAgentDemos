using System.Diagnostics;
using Defra.Contracts.V1;
using Defra.Observability;

namespace Defra.UnitTests;

public sealed class DefraTelemetryTests
{
    [Fact]
    public void AgentAndToolActivities_InheritAmbientRequestTrace()
    {
        using ActivityListener listener = new()
        {
            ShouldListenTo = source =>
                source.Name is DefraActivitySourceNames.AgentCore or DefraActivitySourceNames.Tools,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        using Activity request = new("http.request");
        request.Start();

        using Activity? turn = DefraActivities.StartAgentTurn(
            new CorrelationId("telemetry-parent"),
            "test.agent");
        Assert.NotNull(turn);
        Assert.Equal(request.TraceId, turn.TraceId);
        Assert.Equal(request.SpanId, turn.ParentSpanId);

        using Activity? tool = DefraActivities.StartToolCall(
            new CorrelationId("telemetry-parent"),
            "lookupOutbreakFarm",
            "call-1");
        Assert.NotNull(tool);
        Assert.Equal(turn.TraceId, tool.TraceId);
        Assert.Equal(turn.SpanId, tool.ParentSpanId);
    }
}
