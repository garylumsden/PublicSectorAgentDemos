using System.Text.Json.Serialization;

namespace Defra.Contracts.V1;

[JsonConverter(typeof(JsonStringEnumConverter<AnswerBlockKind>))]
public enum AnswerBlockKind
{
    DirectAnswer,
    EvidenceSummary,
    ProvenanceAndFreshness,
    CaveatsAndUncertainty,
    RecommendedFollowUp,
    ToolTrace
}

public sealed record AnswerBlock(AnswerBlockKind Kind, string Content);

public sealed record SixBlockAnswerEnvelope
{
    private static readonly AnswerBlockKind[] RequiredKinds = Enum.GetValues<AnswerBlockKind>();

    public SixBlockAnswerEnvelope(
        CorrelationId correlationId,
        IEnumerable<AnswerBlock> blocks,
        IReadOnlyList<EvidenceReference>? evidence = null,
        IReadOnlyList<ToolTraceItem>? toolTrace = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        AnswerBlock[] blockArray = blocks.ToArray();
        if (blockArray.Length != RequiredKinds.Length ||
            RequiredKinds.Any(kind => blockArray.Count(block => block.Kind == kind) != 1))
        {
            throw new ArgumentException("The answer envelope must contain each of the six block kinds exactly once.", nameof(blocks));
        }

        if (blockArray.Any(block => string.IsNullOrWhiteSpace(block.Content)))
        {
            throw new ArgumentException("Answer blocks cannot be empty.", nameof(blocks));
        }

        SchemaVersion = "1.0.0";
        CorrelationId = correlationId;
        Blocks = Array.AsReadOnly(blockArray);
        Evidence = Array.AsReadOnly((evidence ?? []).ToArray());
        ToolTrace = Array.AsReadOnly((toolTrace ?? []).ToArray());
    }

    public string SchemaVersion { get; }

    public CorrelationId CorrelationId { get; }

    public IReadOnlyList<AnswerBlock> Blocks { get; }

    public IReadOnlyList<EvidenceReference> Evidence { get; }

    public IReadOnlyList<ToolTraceItem> ToolTrace { get; }
}
