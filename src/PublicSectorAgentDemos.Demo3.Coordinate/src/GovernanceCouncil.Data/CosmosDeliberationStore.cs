namespace GovernanceCouncil.Data;

using GovernanceCouncil.Core.Interfaces;
using GovernanceCouncil.Core.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

internal sealed class CosmosDeliberationStore(Container container, ILogger<CosmosDeliberationStore> logger) : IDeliberationStore
{
    public async Task<Deliberation> GetAsync(string deliberationId, CancellationToken ct = default)
    {
        try
        {
            var response = await container.ReadItemAsync<Deliberation>(
                deliberationId,
                new PartitionKey(deliberationId),
                cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex)
        {
            logger.LogError(ex, "Failed to read deliberation {DeliberationId}", deliberationId);
            throw;
        }
    }

    public async Task<Deliberation?> GetOrDefaultAsync(string deliberationId, CancellationToken ct = default)
    {
        try
        {
            var response = await container.ReadItemAsync<Deliberation>(
                deliberationId,
                new PartitionKey(deliberationId),
                cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<Deliberation>> ListAsync(CancellationToken ct = default)
    {
        try
        {
            var query = container.GetItemQueryIterator<Deliberation>("SELECT * FROM c");
            var results = new List<Deliberation>();
            while (query.HasMoreResults)
            {
                var page = await query.ReadNextAsync(ct);
                results.AddRange(page);
            }
            return results;
        }
        catch (CosmosException ex)
        {
            logger.LogError(ex, "Failed to list deliberations");
            throw;
        }
    }

    public async Task UpsertAsync(Deliberation deliberation, CancellationToken ct = default)
    {
        try
        {
            await container.UpsertItemAsync(
                deliberation,
                new PartitionKey(deliberation.DeliberationId),
                cancellationToken: ct);
        }
        catch (CosmosException ex)
        {
            logger.LogError(ex, "Failed to upsert deliberation {DeliberationId}", deliberation.DeliberationId);
            throw;
        }
    }
}
