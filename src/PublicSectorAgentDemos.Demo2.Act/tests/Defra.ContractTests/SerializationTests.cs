using System.Text;
using System.Text.Json;
using Defra.Audit;
using Defra.Contracts.V1;

namespace Defra.ContractTests;

public sealed class SerializationTests
{
    [Fact]
    public void EvidenceLabels_SerializeAsApprovedDemo4Values()
    {
        string json = JsonSerializer.Serialize(Enum.GetValues<EvidenceLabel>());

        Assert.Equal(
            """["Live","Cached","Sampled","Synthetic","Inferred","Unavailable"]""",
            json);
    }

    [Fact]
    public void AnswerEnvelope_SerializesStableV1Shape()
    {
        SixBlockAnswerEnvelope envelope = new(
            new CorrelationId("correlation-1"),
            Enum.GetValues<AnswerBlockKind>().Select(kind => new AnswerBlock(kind, kind.ToString())));

        string json = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using JsonDocument document = JsonDocument.Parse(json);

        Assert.Equal("1.0.0", document.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("correlation-1", document.RootElement.GetProperty("correlationId").GetProperty("value").GetString());
        JsonElement blocks = document.RootElement.GetProperty("blocks");
        Assert.Equal(6, blocks.GetArrayLength());
        Assert.Equal(
            [
                "DirectAnswer",
                "EvidenceSummary",
                "ProvenanceAndFreshness",
                "CaveatsAndUncertainty",
                "RecommendedFollowUp",
                "ToolTrace"
            ],
            blocks.EnumerateArray().Select(block => block.GetProperty("kind").GetString()));
    }

    [Fact]
    public async Task AuditWriter_AppendsOneSanitizedEventPerLine()
    {
        await using MemoryStream stream = new();
        await using (JsonLinesAuditWriter writer = new(stream, leaveOpen: true))
        {
            await writer.AppendAsync(CreateEvent("event-1"));
            stream.Position = 0;
            await writer.AppendAsync(CreateEvent("event-2"));
        }

        string[] lines = Encoding.UTF8.GetString(stream.ToArray())
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);
        Assert.Contains("\"kind\":\"toolDenied\"", lines[0], StringComparison.Ordinal);
        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement auditEvent = document.RootElement;
        Assert.Equal("2026-07-13T12:00:00+00:00", auditEvent.GetProperty("timestamp").GetString());
        Assert.Equal("correlation-1", auditEvent.GetProperty("correlationId").GetProperty("value").GetString());
        Assert.Equal("demo4", auditEvent.GetProperty("demo").GetString());
        Assert.Equal("demo-agent", auditEvent.GetProperty("agent").GetString());
        Assert.Equal("tool-execution", auditEvent.GetProperty("stage").GetString());
        Assert.Equal("search", auditEvent.GetProperty("tool").GetString());
        Assert.Equal("deny", auditEvent.GetProperty("decision").GetString());
        Assert.Equal("1.0.0", auditEvent.GetProperty("policyVersion").GetString());
        Assert.Equal("policy-denied", auditEvent.GetProperty("outcome").GetString());
        Assert.Equal(12, auditEvent.GetProperty("durationMs").GetInt64());
        Assert.Equal(new string('a', 64), auditEvent.GetProperty("inputHash").GetString());
        Assert.DoesNotContain("prompt", lines[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arguments", lines[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuditEvent_RejectsFreeTextIdentifiersAndInvalidInputHash()
    {
        Assert.Throws<ArgumentException>(
            () => CreateEvent("full prompt content"));

        Assert.Throws<ArgumentException>(
            () => new SanitizedAuditEvent(
                "event-1",
                new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
                new CorrelationId("correlation-1"),
                AuditEventKind.ToolDenied,
                "demo4",
                "demo-agent",
                "tool-execution",
                "search",
                "deny",
                "1.0.0",
                "policy-denied",
                12,
                "not-a-hash"));
    }

    private static SanitizedAuditEvent CreateEvent(string eventId) =>
        new(
            eventId,
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero),
            new CorrelationId("correlation-1"),
            AuditEventKind.ToolDenied,
            "demo4",
            "demo-agent",
            "tool-execution",
            "search",
            "deny",
            "1.0.0",
            "policy-denied",
            12,
            new string('a', 64),
            policyId: "demo-policy",
            traceId: "trace-1");
}
