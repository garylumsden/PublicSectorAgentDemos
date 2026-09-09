namespace GovernanceCouncil.Agents.Runtime;

using Microsoft.Extensions.AI;
using ChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;
using ChatReasoningEffortLevel = OpenAI.Chat.ChatReasoningEffortLevel;

/// <summary>
/// Forces a fixed <c>reasoning_effort</c> on every call for a local MAF client (Chat Completions). Used
/// to run each council role at its own effort — <c>minimal</c> for bids / routing, <c>low</c> for the
/// debating personas, <c>medium</c> for the Chair's synthesis. Merges with any existing raw-options
/// factory so persona tools / structured outputs are preserved.
/// </summary>
internal sealed class ReasoningEffortChatClient(IChatClient inner, string effort) : DelegatingChatClient(inner)
{
    private readonly ChatReasoningEffortLevel _effort = Map(effort);

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        => base.GetResponseAsync(messages, WithEffort(options), ct);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        => base.GetStreamingResponseAsync(messages, WithEffort(options), ct);

    private ChatOptions WithEffort(ChatOptions? options)
    {
        options = options?.Clone() ?? new ChatOptions();
        var prior = options.RawRepresentationFactory;
        options.RawRepresentationFactory = client =>
        {
            var raw = prior?.Invoke(client);
            var cco = raw as ChatCompletionOptions ?? new ChatCompletionOptions();
            cco.ReasoningEffortLevel = _effort;
            return cco;
        };
        return options;
    }

    private static ChatReasoningEffortLevel Map(string effort) => effort.Trim().ToLowerInvariant() switch
    {
        "none" => new ChatReasoningEffortLevel("none"),
        "minimal" => ChatReasoningEffortLevel.Minimal,
        "low" => ChatReasoningEffortLevel.Low,
        "medium" => ChatReasoningEffortLevel.Medium,
        "high" => ChatReasoningEffortLevel.High,
        _ => ChatReasoningEffortLevel.Minimal
    };
}
