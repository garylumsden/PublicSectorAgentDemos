namespace GovernanceCouncil.Core.Interfaces;

using GovernanceCouncil.Core.Models;

public interface IAssessmentStore
{
    Task<Assessment> GetAsync(string assessmentId, CancellationToken ct = default);
    Task<IReadOnlyList<Assessment>> ListAsync(CancellationToken ct = default);
    Task UpsertAsync(Assessment assessment, CancellationToken ct = default);
    Task DeleteAsync(string assessmentId, CancellationToken ct = default);

    /// <summary>
    /// Returns up to <paramref name="k"/> assessments most similar to <paramref name="queryEmbedding"/>
    /// by Cosmos vector distance (server-side ANN), excluding <paramref name="excludeAssessmentId"/> and
    /// any record without an embedding. Empty when vector search is unavailable or no embedded priors exist.
    /// </summary>
    Task<IReadOnlyList<Assessment>> FindSimilarAsync(
        float[] queryEmbedding, int k, string excludeAssessmentId, CancellationToken ct = default);
}
