using Defra.Contracts.V1;

namespace Defra.Tools.Mcp.Providers;

public sealed class SyntheticProviderException(
    string code,
    string safeDetail,
    bool isProtocolError = false) : Exception(safeDetail)
{
    public string Code { get; } = code;

    public string SafeDetail { get; } = safeDetail;

    public bool IsProtocolError { get; } = isProtocolError;
}

internal static class SyntheticEvidence
{
    internal static readonly DateTimeOffset DemoRetrievedAt =
        new(2026, 9, 6, 15, 40, 0, TimeSpan.Zero);

    public static EvidenceReference Create(
        string evidenceId,
        string sourceId,
        string sourceName,
        string safeSummary,
        DateTimeOffset publishedAt,
        EvidenceLabel label = EvidenceLabel.Synthetic,
        FreshnessStatus freshness = FreshnessStatus.Current) =>
        new(
            evidenceId,
            label,
            freshness,
            new Provenance(
                sourceId,
                sourceName,
                new Uri($"urn:defra:{sourceId}", UriKind.Absolute),
                publishedAt > DemoRetrievedAt ? publishedAt : DemoRetrievedAt,
                publishedAt),
            safeSummary);
}
