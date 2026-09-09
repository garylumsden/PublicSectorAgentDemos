namespace GovernanceCouncil.Core.Models;

using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NexusType
{
    Implication,
    Contradiction,
    Dependency,
    Supersession,
    Reinforcement,
    Tension
}

public record Nexus
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("sourceAssessmentId")]
    public required string SourceAssessmentId { get; init; }

    [JsonPropertyName("targetAssessmentId")]
    public required string TargetAssessmentId { get; init; }

    [JsonPropertyName("nexusType")]
    public required NexusType NexusType { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("discoveredBy")]
    public required string DiscoveredBy { get; init; }

    [JsonPropertyName("discoveredDate")]
    public required DateTimeOffset DiscoveredDate { get; init; }

    [JsonPropertyName("nexusPoints")]
    public required List<NexusPoint> NexusPoints { get; init; }

    [JsonPropertyName("confidence")]
    public required string Confidence { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

public record NexusPoint
{
    [JsonPropertyName("sourceExcerpt")]
    public required string SourceExcerpt { get; init; }

    [JsonPropertyName("targetExcerpt")]
    public required string TargetExcerpt { get; init; }

    [JsonPropertyName("relationship")]
    public required string Relationship { get; init; }
}
