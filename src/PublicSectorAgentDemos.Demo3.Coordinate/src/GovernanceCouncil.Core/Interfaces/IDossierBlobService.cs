namespace GovernanceCouncil.Core.Interfaces;

public interface IDossierBlobService
{
    Task<string> UploadOriginalAsync(Stream content, string fileName, CancellationToken ct = default);
    Task<string> UploadMarkdownAsync(string markdown, string dossierId, CancellationToken ct = default);
    Task<string> GetMarkdownAsync(string blobPath, CancellationToken ct = default);
    Task DeleteBlobAsync(string blobPath, CancellationToken ct = default);
}
