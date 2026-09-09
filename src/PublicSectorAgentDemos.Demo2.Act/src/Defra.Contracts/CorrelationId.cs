namespace Defra.Contracts.V1;

public readonly record struct CorrelationId
{
    public CorrelationId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length > 128 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.' and not ':'))
        {
            throw new ArgumentException(
                "Correlation IDs must be 1-128 ASCII letters, digits, hyphens, underscores, periods, or colons.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public static CorrelationId Create() => new(Guid.NewGuid().ToString("N"));

    public override string ToString() => Value;
}
