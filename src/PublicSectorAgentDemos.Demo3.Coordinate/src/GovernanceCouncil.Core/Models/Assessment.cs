namespace GovernanceCouncil.Core.Models;

using System.Text.Json.Serialization;

public record Assessment
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("assessmentId")]
    public required string AssessmentId { get; init; }

    [JsonPropertyName("dossierId")]
    public required string DossierId { get; init; }

    [JsonPropertyName("dossierTitle")]
    public required string DossierTitle { get; init; }

    [JsonPropertyName("deliberationId")]
    public required string DeliberationId { get; init; }

    [JsonPropertyName("reviewDate")]
    public required DateTimeOffset ReviewDate { get; init; }

    [JsonPropertyName("overallRecommendation")]
    public required string OverallRecommendation { get; init; }

    [JsonPropertyName("chairSummary")]
    public string? ChairSummary { get; init; }

    [JsonPropertyName("participants")]
    public List<string>? Participants { get; init; }

    [JsonPropertyName("deliberationSummary")]
    public List<MemberContribution>? DeliberationSummary { get; init; }

    [JsonPropertyName("votes")]
    public required Dictionary<string, MemberVote> Votes { get; init; }

    [JsonPropertyName("conditions")]
    public required List<string> Conditions { get; init; }

    [JsonPropertyName("dissent")]
    public required List<Dissent> Dissent { get; init; }

    [JsonPropertyName("risks")]
    public required List<Risk> Risks { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>Vector embedding of the assessment summary (text-embedding-3-small, 1536-d), computed
    /// once at creation and used for Cosmos vector Top-K nexus retrieval. Null for legacy records.</summary>
    [JsonPropertyName("embedding")]
    public float[]? Embedding { get; init; }

    [JsonPropertyName("embeddingModel")]
    public string? EmbeddingModel { get; init; }
}

public record MemberVote
{
    [JsonPropertyName("position")]
    public required string Position { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }
}

public record Dissent
{
    [JsonPropertyName("member")]
    public required string Member { get; init; }

    [JsonPropertyName("concern")]
    public required string Concern { get; init; }
}

public record Risk
{
    [JsonPropertyName("category")]
    public required string Category { get; init; }

    [JsonPropertyName("description")]
    public required string Description { get; init; }

    [JsonPropertyName("mitigation")]
    public required string Mitigation { get; init; }
}

public record MemberContribution
{
    [JsonPropertyName("member")]
    public required string Member { get; init; }

    [JsonPropertyName("position")]
    public required string Position { get; init; }

    [JsonPropertyName("keyPoints")]
    public required string KeyPoints { get; init; }
}
