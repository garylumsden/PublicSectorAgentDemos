namespace GovernanceCouncil.Agents.Runtime;

using Azure.AI.Projects;
using GovernanceCouncil.Agents.Provisioning;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// The default runtime: Foundry Prompt Agents. Provisions each member's agent version
/// (<see cref="AgentProvisioner"/>) and fetches them as MAF <see cref="AIAgent"/>s
/// (<see cref="AgentCache"/>); the agent's own <see cref="IChatClient"/> runs the persona + tools
/// server-side. Bids/selection keep the existing per-agent path (FastClient = null).
/// </summary>
public sealed class FoundryCouncilRuntime : ICouncilRuntime
{
    private readonly AIProjectClient _projectClient;
    private readonly AgentProvisioner _provisioner;
    private readonly AgentCache _cache;
    private readonly ILogger _logger;

    public FoundryCouncilRuntime(
        AIProjectClient projectClient,
        HttpClient httpClient,
        Azure.Core.TokenCredential credential,
        string projectEndpoint,
        AgentCache cache,
        ILogger<FoundryCouncilRuntime> logger)
    {
        _projectClient = projectClient;
        _provisioner = new AgentProvisioner(httpClient, credential, projectEndpoint, logger);
        _cache = cache;
        _logger = logger;
    }

    public string[] MemberNames => _cache.MemberNames;
    public IReadOnlyList<IChatClient> MemberClients { get; private set; } = [];
    public IChatClient ChairClient { get; private set; } = null!;
    public IChatClient? ModeratorClient { get; private set; }
    public IChatClient? FastClient => null;
    public bool IsLoaded => _cache.IsLoaded;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _provisioner.ProvisionAllAsync(ct);
        await _cache.LoadAsync(_projectClient, _logger, ct);
        CaptureClients();
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _provisioner.ProvisionAllAsync(ct);
        await _cache.RefreshAsync(_projectClient, _logger, ct);
        CaptureClients();
    }

    private void CaptureClients()
    {
        MemberClients = _cache.MemberAgents.Select(ClientOf).ToList();
        ChairClient = ClientOf(_cache.Chair);
        ModeratorClient = ClientOf(_cache.Moderator);
    }

    private static IChatClient ClientOf(AIAgent agent) =>
        agent.GetService<IChatClient>()
            ?? throw new InvalidOperationException("Foundry agent does not provide an IChatClient.");
}
