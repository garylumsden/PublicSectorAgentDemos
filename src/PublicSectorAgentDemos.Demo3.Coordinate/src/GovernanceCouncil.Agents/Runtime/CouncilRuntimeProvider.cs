namespace GovernanceCouncil.Agents.Runtime;

using GovernanceCouncil.Core.Models;

/// <summary>
/// Resolves the active <see cref="ICouncilRuntime"/> from the <see cref="AgentRuntime"/> toggle
/// (Foundry default | local MAF). Both runtimes are kept warm so switching is just a pointer change
/// plus a load of the newly-selected runtime.
/// </summary>
public sealed class CouncilRuntimeProvider
{
    private readonly FoundryCouncilRuntime _foundry;
    private readonly MafCouncilRuntime _maf;

    public CouncilRuntimeProvider(FoundryCouncilRuntime foundry, MafCouncilRuntime maf)
    {
        _foundry = foundry;
        _maf = maf;
    }

    public ICouncilRuntime Active =>
        AgentRuntime.Active == AgentRuntime.Mode.Maf ? _maf : _foundry;
}
