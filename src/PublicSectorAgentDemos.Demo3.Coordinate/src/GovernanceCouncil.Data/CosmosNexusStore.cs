namespace GovernanceCouncil.Data;

using GovernanceCouncil.Core.Interfaces;
using GovernanceCouncil.Core.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

internal sealed class CosmosNexusStore(Container container, ILogger<CosmosNexusStore> logger) : INexusStore
{
    public async Task<Nexus> GetAsync(string nexusId, string sourceAssessmentId, CancellationToken ct = default)
    {
        try
        {
            var response = await container.ReadItemAsync<Nexus>(
                nexusId,
                new PartitionKey(sourceAssessmentId),
                cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex)
        {
            logger.LogError(ex, "Failed to read nexus {NexusId} for source {SourceAssessmentId}", nexusId, sourceAssessmentId);
            throw;
        }
    }

    public async Task<IReadOnlyList<Nexus>> ListBySourceAsync(string sourceAssessmentId, CancellationToken ct = default)
    {
        var queryDef = new QueryDefinition("SELECT * FROM c WHERE c.sourceAssessmentId = @id")
            .WithParameter("@id", sourceAssessmentId);

        return await QueryAsync(queryDef, new QueryRequestOptions { PartitionKey = new PartitionKey(sourceAssessmentId) }, ct);
    }

    public async Task<IReadOnlyList<Nexus>> ListByTargetAsync(string targetAssessmentId, CancellationToken ct = default)
    {
        var queryDef = new QueryDefinition("SELECT * FROM c WHERE c.targetAssessmentId = @id")
            .WithParameter("@id", targetAssessmentId);

        return await QueryAsync(queryDef, null, ct);
    }

    public async Task<IReadOnlyList<Nexus>> ListByTypeAsync(NexusType nexusType, CancellationToken ct = default)
    {
        var queryDef = new QueryDefinition("SELECT * FROM c WHERE c.nexusType = @type")
            .WithParameter("@type", nexusType.ToString());

        return await QueryAsync(queryDef, null, ct);
    }

    public async Task<IReadOnlyList<Nexus>> ListAllAsync(CancellationToken ct = default)
    {
        var queryDef = new QueryDefinition("SELECT * FROM c");
        return await QueryAsync(queryDef, null, ct);
    }

    public async Task UpsertAsync(Nexus nexus, CancellationToken ct = default)
    {
        try
        {
            await container.UpsertItemAsync(
                nexus,
                new PartitionKey(nexus.SourceAssessmentId),
                cancellationToken: ct);
        }
        catch (CosmosException ex)
        {
            logger.LogError(ex, "Failed to upsert nexus {NexusId}", nexus.Id);
            throw;
        }
    }

    public async Task DeleteAsync(string nexusId, string sourceAssessmentId, CancellationToken ct = default)
    {
        try
        {
            await container.DeleteItemAsync<Nexus>(
                nexusId,
                new PartitionKey(sourceAssessmentId),
                cancellationToken: ct);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already deleted — ignore
        }
    }

    public async Task<IReadOnlyList<Nexus>> ListForAssessmentAsync(string assessmentId, CancellationToken ct = default)
    {
        var queryDef = new QueryDefinition(
            "SELECT * FROM c WHERE c.sourceAssessmentId = @id OR c.targetAssessmentId = @id")
            .WithParameter("@id", assessmentId);

        return await QueryAsync(queryDef, null, ct);
    }

    private async Task<IReadOnlyList<Nexus>> QueryAsync(
        QueryDefinition queryDef,
        QueryRequestOptions? options,
        CancellationToken ct)
    {
        try
        {
            using var iterator = container.GetItemQueryIterator<Nexus>(queryDef, requestOptions: options);
            var results = new List<Nexus>();
            while (iterator.HasMoreResults)
            {
                var page = await iterator.ReadNextAsync(ct);
                results.AddRange(page);
            }
            return results;
        }
        catch (CosmosException ex)
        {
            logger.LogError(ex, "Failed to query nexuses");
            throw;
        }
    }
}
