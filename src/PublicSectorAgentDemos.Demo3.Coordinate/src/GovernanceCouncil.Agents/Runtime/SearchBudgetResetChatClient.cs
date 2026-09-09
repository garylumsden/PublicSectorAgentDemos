namespace GovernanceCouncil.Agents.Runtime;

using Microsoft.Extensions.AI;

/// <summary>
/// Resets a member's per-turn grounding <see cref="GroundingTools.SearchBudget"/> at the start of each
/// turn, then delegates. It sits OUTSIDE the function-invocation loop, so the budget is restored once
/// per turn (not once per tool iteration) — hard-capping how many grounding calls a single debate turn
/// can make, regardless of how tool-eager the model is.
/// </summary>
internal sealed class SearchBudgetResetChatClient(IChatClient inner, GroundingTools.SearchBudget budget, int max)
    : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        System.Threading.Volatile.Write(ref budget.Remaining, max);
        return base.GetResponseAsync(messages, options, ct);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        System.Threading.Volatile.Write(ref budget.Remaining, max);
        return base.GetStreamingResponseAsync(messages, options, ct);
    }
}
