namespace GovernanceCouncil.Core.Models;

using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DossierSource
{
    Upload,
    Url,
    Markdown
}

public record Dossier
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("dossierId")]
    public required string DossierId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("source")]
    public required DossierSource Source { get; init; }

    [JsonPropertyName("sourceUrl")]
    public string? SourceUrl { get; init; }

    [JsonPropertyName("originalBlobPath")]
    public required string OriginalBlobPath { get; init; }

    [JsonPropertyName("markdownBlobPath")]
    public required string MarkdownBlobPath { get; init; }

    [JsonPropertyName("uploadedDate")]
    public required DateTimeOffset UploadedDate { get; init; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; init; }
}
