namespace GovernanceCouncil.Core.Models;

using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeliberationStatus
{
    Pending,
    InProgress,
    Completed,
    Failed
}

public record Deliberation
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("deliberationId")]
    public required string DeliberationId { get; init; }

    [JsonPropertyName("dossierId")]
    public required string DossierId { get; init; }

    [JsonPropertyName("assessmentId")]
    public string? AssessmentId { get; init; }

    [JsonPropertyName("status")]
    public required DeliberationStatus Status { get; init; }

    [JsonPropertyName("context")]
    public string? Context { get; init; }

    [JsonPropertyName("submittedDate")]
    public required DateTimeOffset SubmittedDate { get; init; }

    [JsonPropertyName("completedDate")]
    public DateTimeOffset? CompletedDate { get; init; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("conversationId")]
    public string? ConversationId { get; init; }

    [JsonPropertyName("transcript")]
    public List<TranscriptEntry>? Transcript { get; init; }
}

public record TranscriptEntry
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("agentName")]
    public string? AgentName { get; init; }

    [JsonPropertyName("content")]
    public required string Content { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }
}
