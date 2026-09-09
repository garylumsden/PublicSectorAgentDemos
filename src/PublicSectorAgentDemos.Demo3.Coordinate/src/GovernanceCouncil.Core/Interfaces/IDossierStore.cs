namespace GovernanceCouncil.Core.Interfaces;

using GovernanceCouncil.Core.Models;

public interface IDossierStore
{
    Task<Dossier> GetAsync(string dossierId, CancellationToken ct = default);
    Task<IReadOnlyList<Dossier>> ListAsync(CancellationToken ct = default);
    Task UpsertAsync(Dossier dossier, CancellationToken ct = default);
    Task DeleteAsync(string dossierId, CancellationToken ct = default);
}
