namespace GovernanceCouncil.Data;

using System.Text;
using Azure;
using Azure.Storage.Blobs;
using GovernanceCouncil.Core.Interfaces;
using Microsoft.Extensions.Logging;

internal sealed class BlobDossierService(BlobServiceClient blobServiceClient, ILogger<BlobDossierService> logger) : IDossierBlobService
{
    private const string OriginalContainer = "dossiers-original";
    private const string MarkdownContainer = "dossiers-markdown";

    public async Task<string> UploadOriginalAsync(Stream content, string fileName, CancellationToken ct = default)
    {
        try
        {
            var containerClient = blobServiceClient.GetBlobContainerClient(OriginalContainer);
            await containerClient.CreateIfNotExistsAsync(cancellationToken: ct);

            var blobPath = $"{Guid.NewGuid()}/{fileName}";
            var blobClient = containerClient.GetBlobClient(blobPath);
            await blobClient.UploadAsync(content, overwrite: true, cancellationToken: ct);

            return blobPath;
        }
        catch (RequestFailedException ex)
        {
            logger.LogError(ex, "Failed to upload original blob {FileName}", fileName);
            throw;
        }
    }

    public async Task<string> UploadMarkdownAsync(string markdown, string dossierId, CancellationToken ct = default)
    {
        try
        {
            var containerClient = blobServiceClient.GetBlobContainerClient(MarkdownContainer);
            await containerClient.CreateIfNotExistsAsync(cancellationToken: ct);

            var blobPath = $"{dossierId}.md";
            var blobClient = containerClient.GetBlobClient(blobPath);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(markdown));
            await blobClient.UploadAsync(stream, overwrite: true, cancellationToken: ct);

            return blobPath;
        }
        catch (RequestFailedException ex)
        {
            logger.LogError(ex, "Failed to upload markdown for dossier {DossierId}", dossierId);
            throw;
        }
    }

    public async Task<string> GetMarkdownAsync(string blobPath, CancellationToken ct = default)
    {
        try
        {
            var containerClient = blobServiceClient.GetBlobContainerClient(MarkdownContainer);
            var blobClient = containerClient.GetBlobClient(blobPath);
            var response = await blobClient.DownloadContentAsync(ct);

            return response.Value.Content.ToString();
        }
        catch (RequestFailedException ex)
        {
            logger.LogError(ex, "Failed to download markdown blob {BlobPath}", blobPath);
            throw;
        }
    }

    public async Task DeleteBlobAsync(string blobPath, CancellationToken ct = default)
    {
        try
        {
            // Try both containers — the path could be in either
            var markdownClient = blobServiceClient.GetBlobContainerClient(MarkdownContainer);
            await markdownClient.GetBlobClient(blobPath).DeleteIfExistsAsync(cancellationToken: ct);

            var originalClient = blobServiceClient.GetBlobContainerClient(OriginalContainer);
            await originalClient.GetBlobClient(blobPath).DeleteIfExistsAsync(cancellationToken: ct);

            logger.LogInformation("Deleted blobs for path {BlobPath}", blobPath);
        }
        catch (RequestFailedException ex)
        {
            logger.LogWarning(ex, "Failed to delete blob {BlobPath}", blobPath);
        }
    }
}
