namespace GovernanceCouncil.Core.Interfaces;

using GovernanceCouncil.Core.Models;

public interface IDeliberationStore
{
    Task<Deliberation> GetAsync(string deliberationId, CancellationToken ct = default);
    Task<Deliberation?> GetOrDefaultAsync(string deliberationId, CancellationToken ct = default);
    Task<IReadOnlyList<Deliberation>> ListAsync(CancellationToken ct = default);
    Task UpsertAsync(Deliberation deliberation, CancellationToken ct = default);
}
