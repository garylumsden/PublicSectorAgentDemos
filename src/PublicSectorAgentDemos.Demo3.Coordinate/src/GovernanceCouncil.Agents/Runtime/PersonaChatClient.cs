namespace GovernanceCouncil.Agents.Runtime;

using Microsoft.Extensions.AI;

/// <summary>
/// Wraps a (function-invoking) chat client so every call runs as a given persona: it prepends the
/// persona system prompt (unless the caller already supplied one) and binds the persona's grounding
/// tools into <see cref="ChatOptions.Tools"/>. This lets a local MAF member be consumed as a plain
/// <see cref="IChatClient"/> — identical to how the debate engine invokes a Foundry agent's client.
/// </summary>
internal sealed class PersonaChatClient(IChatClient inner, string systemPrompt, IList<AITool>? tools)
    : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        => base.GetResponseAsync(WithPersona(messages), WithTools(options), ct);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        => base.GetStreamingResponseAsync(WithPersona(messages), WithTools(options), ct);

    private IEnumerable<ChatMessage> WithPersona(IEnumerable<ChatMessage> messages)
        => messages.Any(m => m.Role == ChatRole.System)
            ? messages
            : messages.Prepend(new ChatMessage(ChatRole.System, systemPrompt));

    private ChatOptions? WithTools(ChatOptions? options)
    {
        if (tools is null || tools.Count == 0) return options;
        options = options?.Clone() ?? new ChatOptions();
        options.Tools = tools;
        return options;
    }
}
