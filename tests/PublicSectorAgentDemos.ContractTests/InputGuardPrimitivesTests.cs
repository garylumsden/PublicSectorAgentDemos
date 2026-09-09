using PublicSectorAgentDemos.Contracts;

namespace PublicSectorAgentDemos.ContractTests;

public sealed class InputGuardPrimitivesTests
{
    [Theory]
    [InlineData("1234567890", 10, 500, true)]
    [InlineData("123456789", 10, 500, false)]
    [InlineData("topic", 1, 120, true)]
    [InlineData("topic\n", 1, 120, false)]
    [InlineData(null, 1, 120, false)]
    public void PreservesDemo2AndDemo4TextBoundaries(
        string? value,
        int minimumCharacters,
        int maximumCharacters,
        bool expected)
    {
        bool actual = InputGuardPrimitives.HasBoundedLengthWithoutControlCharacters(
            value,
            minimumCharacters,
            maximumCharacters);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RejectsAnInvalidRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InputGuardPrimitives.HasBoundedLengthWithoutControlCharacters("value", 2, 1));
    }
}
