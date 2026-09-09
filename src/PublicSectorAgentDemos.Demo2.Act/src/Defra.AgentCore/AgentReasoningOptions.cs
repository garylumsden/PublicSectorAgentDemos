#pragma warning disable OPENAI001

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace Defra.AgentCore;

public static class AgentReasoningOptions
{
    public static ChatClientAgentOptions CreateAgentOptions(
        string model,
        string instructions,
        string name,
        string description,
        string effort) =>
        new()
        {
            Name = name,
            Description = description,
            ChatOptions = CreateChatOptions(model, instructions, effort)
        };

    public static ChatClientAgentRunOptions CreateModelRunOptions(
        string effort,
        ChatResponseFormat? responseFormat = null) =>
        new(CreateChatOptions(model: null, instructions: null, effort))
        {
            ResponseFormat = responseFormat
        };

    public static ResponseReasoningOptions CreateResponseReasoningOptions(
        string effort) =>
        new()
        {
            ReasoningEffortLevel = NormalizeEffort(effort) switch
            {
                "low" => ResponseReasoningEffortLevel.Low,
                "medium" => ResponseReasoningEffortLevel.Medium,
                "high" => ResponseReasoningEffortLevel.High,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(effort),
                    effort,
                    "Reasoning effort must be low, medium, or high.")
            }
        };

    public static string NormalizeEffort(string effort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effort);
        string normalized = effort.Trim().ToLowerInvariant();
        return normalized is "low" or "medium" or "high"
            ? normalized
            : throw new ArgumentOutOfRangeException(
                nameof(effort),
                effort,
                "Reasoning effort must be low, medium, or high.");
    }

    private static ChatOptions CreateChatOptions(
        string? model,
        string? instructions,
        string effort) =>
        new()
        {
            ModelId = model,
            Instructions = instructions,
            RawRepresentationFactory = _ => new CreateResponseOptions
            {
                ReasoningOptions = CreateResponseReasoningOptions(effort),
                ParallelToolCallsEnabled = false
            }
        };
}
