namespace GovernanceCouncil.Agents.Ingestion;

using GovernanceCouncil.Core.Interfaces;
using GovernanceCouncil.Core.Models;
using Microsoft.Extensions.Logging;

/// <summary>
/// Handles document ingestion — uploading files and fetching URLs.
/// Stores originals in blob storage and creates markdown representations.
/// </summary>
public sealed class DossierIngestionService
{
    private readonly IDossierStore _dossierStore;
    private readonly IDossierBlobService _blobService;
    private readonly ILogger<DossierIngestionService> _logger;

    public DossierIngestionService(
        IDossierStore dossierStore,
        IDossierBlobService blobService,
        ILogger<DossierIngestionService>? logger = null)
    {
        _dossierStore = dossierStore;
        _blobService = blobService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DossierIngestionService>.Instance;
    }

    /// <summary>
    /// Uploads markdown content directly as a dossier.
    /// The markdown is stored as both the original and the agent-readable version.
    /// </summary>
    public async Task<Dossier> UploadMarkdownAsync(string markdown, string title, CancellationToken ct = default)
    {
        var dossierId = $"DS-{DateTime.UtcNow:yyyy}-{Guid.NewGuid().ToString("N")[..8]}";

        using var mdStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(markdown));
        var originalPath = await _blobService.UploadOriginalAsync(mdStream, $"{dossierId}.md", ct);
        var markdownPath = await _blobService.UploadMarkdownAsync(markdown, dossierId, ct);

        var dossier = new Dossier
        {
            Id = dossierId,
            DossierId = dossierId,
            Title = title,
            Source = DossierSource.Markdown,
            OriginalBlobPath = originalPath,
            MarkdownBlobPath = markdownPath,
            UploadedDate = DateTimeOffset.UtcNow,
            FileName = $"{dossierId}.md"
        };

        await _dossierStore.UpsertAsync(dossier, ct);
        _logger.LogInformation("Markdown uploaded as dossier {DossierId}", dossierId);
        return dossier;
    }
}
