namespace GovernanceCouncil.Data;

using GovernanceCouncil.Core.Interfaces;
using GovernanceCouncil.Core.Models;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

internal sealed class CosmosAssessmentStore(Container container, ILogger<CosmosAssessmentStore> logger) : IAssessmentStore
{
    public async Task<Assessment> GetAsync(string assessmentId, CancellationToken ct = default)
    {
        try
        {
            var response = await container.ReadItemAsync<Assessment>(
                assessmentId,
                new PartitionKey(assessmentId),
                cancellationToken: ct);
            return response.Resource;
        }
        catch (CosmosException ex)
        {
            logger.LogError(ex, "Failed to read assessment {AssessmentId}", assessmentId);
            throw;
        }
    }

    // List views (Home, Assessment list, Nexus Explorer) never need the 1536-float embedding, so the
    // list projection omits it — the heavy vector is only read on the single-item GetAsync path.
    private const string ListProjection =
        "SELECT c.id, c.assessmentId, c.dossierId, c.dossierTitle, c.deliberationId, c.reviewDate, " +
        "c.overallRecommendation, c.chairSummary, c.participants, c.deliberationSummary, c.votes, " +
        "c.conditions, c.dissent, c.risks, c.status, c.embeddingModel FROM c";

    public async Task<IReadOnlyList<Assessment>> ListAsync(CancellationToken ct = default)
    {
        try
        {
            var query = container.GetItemQueryIterator<Assessment>(ListProjection);
            var results = new List<Assessment>();
            while (query.HasMoreResults)
            {
                var page = await query.ReadNextAsync(ct);
                results.AddRange(page);
            }
            return results;
        }
        catch (CosmosException ex)
        {
            logger.LogError(ex, "Failed to list assessments");
            throw;
        }
    }

    public async Task UpsertAsync(Assessment assessment, CancellationToken ct = default)
    {
        try
        {
            await container.UpsertItemAsync(
                assessment,
                new PartitionKey(assessment.AssessmentId),
                cancellationToken: ct);
        }
        catch (CosmosException ex)
        {
            logger.LogError(ex, "Failed to upsert assessment {AssessmentId}", assessment.AssessmentId);
            throw;
        }
    }

    public async Task DeleteAsync(string assessmentId, CancellationToken ct = default)
    {
        try
        {
            await container.DeleteItemAsync<Assessment>(
                assessmentId,
                new PartitionKey(assessmentId),
                cancellationToken: ct);
            logger.LogInformation("Deleted assessment {AssessmentId}", assessmentId);
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            logger.LogWarning("Assessment {AssessmentId} not found for deletion", assessmentId);
        }
    }

    public async Task<IReadOnlyList<Assessment>> FindSimilarAsync(
        float[] queryEmbedding, int k, string excludeAssessmentId, CancellationToken ct = default)
    {
        try
        {
            // Server-side ANN: VectorDistance over the /embedding vector index. Skips the query item and
            // any legacy record without an embedding. ORDER BY VectorDistance ranks nearest-first.
            var queryDef = new QueryDefinition(
                "SELECT TOP @k * FROM c WHERE c.assessmentId != @ex AND IS_DEFINED(c.embedding) " +
                "ORDER BY VectorDistance(c.embedding, @q)")
                .WithParameter("@k", k)
                .WithParameter("@ex", excludeAssessmentId)
                .WithParameter("@q", queryEmbedding);

            using var iterator = container.GetItemQueryIterator<Assessment>(queryDef);
            var results = new List<Assessment>();
            while (iterator.HasMoreResults)
                results.AddRange(await iterator.ReadNextAsync(ct));
            return results;
        }
        catch (CosmosException ex)
        {
            logger.LogWarning(ex, "Vector similarity query failed; caller will fall back");
            return [];
        }
    }
}
