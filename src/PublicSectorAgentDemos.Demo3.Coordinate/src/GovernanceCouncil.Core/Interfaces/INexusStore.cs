namespace GovernanceCouncil.Core.Interfaces;

using GovernanceCouncil.Core.Models;

public interface INexusStore
{
    Task<Nexus> GetAsync(string nexusId, string sourceAssessmentId, CancellationToken ct = default);
    Task<IReadOnlyList<Nexus>> ListBySourceAsync(string sourceAssessmentId, CancellationToken ct = default);
    Task<IReadOnlyList<Nexus>> ListByTargetAsync(string targetAssessmentId, CancellationToken ct = default);
    Task<IReadOnlyList<Nexus>> ListByTypeAsync(NexusType nexusType, CancellationToken ct = default);
    Task<IReadOnlyList<Nexus>> ListAllAsync(CancellationToken ct = default);
    Task UpsertAsync(Nexus nexus, CancellationToken ct = default);
    Task DeleteAsync(string nexusId, string sourceAssessmentId, CancellationToken ct = default);
    Task<IReadOnlyList<Nexus>> ListForAssessmentAsync(string assessmentId, CancellationToken ct = default);
}
