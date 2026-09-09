namespace GovernanceCouncil.Data;

using GovernanceCouncil.Core.Interfaces;
using GovernanceCouncil.Core.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

internal sealed class CosmosDossierStore(Container container, ILogger<CosmosDossierStore> logger) : IDossierStore
{
    public async Task<Dossier> GetAsync(string dossierId, CancellationToken ct = default)
    {
        try
        {
            var response = await container.ReadItemAsync<Dossier>(
                dossierId,
                new PartitionKey(dossierId),
                cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex)
        {
            logger.LogError(ex, "Failed to read dossier {DossierId}", dossierId);
            throw;
        }
    }

    public async Task<IReadOnlyList<Dossier>> ListAsync(CancellationToken ct = default)
    {
        try
        {
            var query = container.GetItemQueryIterator<Dossier>("SELECT * FROM c");
            var results = new List<Dossier>();
            while (query.HasMoreResults)
            {
                var page = await query.ReadNextAsync(ct);
                results.AddRange(page);
            }
            return results;
        }
        catch (CosmosException ex)
        {
            logger.LogError(ex, "Failed to list dossiers");
            throw;
        }
    }

    public async Task UpsertAsync(Dossier dossier, CancellationToken ct = default)
    {
        try
        {
            await container.UpsertItemAsync(
                dossier,
                new PartitionKey(dossier.DossierId),
                cancellationToken: ct);
        }
        catch (CosmosException ex)
        {
            logger.LogError(ex, "Failed to upsert dossier {DossierId}", dossier.DossierId);
            throw;
        }
    }

    public async Task DeleteAsync(string dossierId, CancellationToken ct = default)
    {
        try
        {
            await container.DeleteItemAsync<Dossier>(
                dossierId,
                new PartitionKey(dossierId),
                cancellationToken: ct);
            logger.LogInformation("Deleted dossier {DossierId}", dossierId);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogWarning("Dossier {DossierId} not found for deletion", dossierId);
        }
    }
}
