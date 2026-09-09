namespace GovernanceCouncil.Agents.Runtime;

using Microsoft.Extensions.AI;

/// <summary>
/// The source of the council's agents for a deliberation, exposed as one <see cref="IChatClient"/>
/// per role (each already applies that role's persona prompt + grounding). Two implementations:
///   <c>FoundryCouncilRuntime</c> — Foundry Prompt Agents (provisioned; the agent IS the chat client);
///   <c>MafCouncilRuntime</c>     — local MAF clients on the Foundry chat models (persona + tool baked
///   in via a function-invoking pipeline).
/// The debate engine only ever needs an IChatClient per role, so this seam keeps it identical for both.
/// </summary>
public interface ICouncilRuntime
{
    /// <summary>Member agent names (e.g. <c>gc-cdio</c>), index-aligned with <see cref="MemberClients"/>.</summary>
    string[] MemberNames { get; }

    /// <summary>One chat client per deliberating member — each runs that persona (+ its grounding tool).</summary>
    IReadOnlyList<IChatClient> MemberClients { get; }

    /// <summary>The Chair chat client (final synthesis).</summary>
    IChatClient ChairClient { get; }

    /// <summary>The Moderator chat client (speaker selection). Null when selection uses <see cref="FastClient"/>.</summary>
    IChatClient? ModeratorClient { get; }

    /// <summary>
    /// When non-null, bids + speaker-selection run on this fast, NON-reasoning client instead of the
    /// member/moderator clients (MAF mode). Null in Foundry mode (keeps the existing per-agent path).
    /// </summary>
    IChatClient? FastClient { get; }

    bool IsLoaded { get; }

    /// <summary>Prepares the clients (Foundry: provision + fetch; MAF: build locally). Idempotent.</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>Forces a rebuild/refetch — used after a profile/grounding/runtime switch.</summary>
    Task ReloadAsync(CancellationToken ct = default);
}
