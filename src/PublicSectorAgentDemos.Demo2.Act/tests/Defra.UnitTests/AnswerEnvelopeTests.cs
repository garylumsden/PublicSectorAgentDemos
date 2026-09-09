using Defra.Contracts.V1;

namespace Defra.UnitTests;

public sealed class AnswerEnvelopeTests
{
    [Fact]
    public void Envelope_ContainsEachRequiredBlockExactlyOnce()
    {
        SixBlockAnswerEnvelope envelope = new(new CorrelationId("correlation-1"), CreateBlocks());

        Assert.Equal("1.0.0", envelope.SchemaVersion);
        Assert.Equal(6, envelope.Blocks.Count);
        Assert.Equal(Enum.GetValues<AnswerBlockKind>(), envelope.Blocks.Select(block => block.Kind));
    }

    [Fact]
    public void Envelope_RejectsDuplicateOrMissingKinds()
    {
        AnswerBlock[] blocks = CreateBlocks();
        blocks[5] = new AnswerBlock(AnswerBlockKind.DirectAnswer, "duplicate");

        Assert.Throws<ArgumentException>(() => new SixBlockAnswerEnvelope(new CorrelationId("correlation-1"), blocks));
    }

    private static AnswerBlock[] CreateBlocks() =>
        Enum.GetValues<AnswerBlockKind>()
            .Select(kind => new AnswerBlock(kind, $"{kind} content"))
            .ToArray();
}
