namespace PublicSectorAgentDemos.Contracts;

public static class InputGuardPrimitives
{
    public static bool HasBoundedLengthWithoutControlCharacters(
        string? value,
        int minimumCharacters,
        int maximumCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(minimumCharacters);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, minimumCharacters);

        return value is not null &&
               value.Length >= minimumCharacters &&
               value.Length <= maximumCharacters &&
               !value.Any(char.IsControl);
    }
}
