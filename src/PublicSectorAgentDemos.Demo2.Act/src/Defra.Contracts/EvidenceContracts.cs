using System.Text.Json.Serialization;

namespace Defra.Contracts.V1;

[JsonConverter(typeof(JsonStringEnumConverter<EvidenceLabel>))]
public enum EvidenceLabel
{
    Live,
    Cached,
    Sampled,
    Synthetic,
    Inferred,
    Unavailable
}

public enum FreshnessStatus
{
    Current,
    Stale,
    Unknown
}

public sealed record Provenance(
    string SourceId,
    string SourceName,
    Uri? SourceUri,
    DateTimeOffset RetrievedAt,
    DateTimeOffset? PublishedAt);

public sealed record EvidenceReference(
    string EvidenceId,
    EvidenceLabel Label,
    FreshnessStatus Freshness,
    Provenance Provenance,
    string SafeSummary);
