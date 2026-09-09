#pragma warning disable OPENAI001

using Defra.AgentCore;
using OpenAI.Responses;

namespace Defra.UnitTests;

public sealed class AgentReasoningOptionsTests
{
    [Fact]
    public void AgentOptions_UseRawLowResponsesReasoning()
    {
        Microsoft.Agents.AI.ChatClientAgentOptions options =
            AgentReasoningOptions.CreateAgentOptions(
                "gpt-5.4-mini",
                "instructions",
                "agent",
                "description",
                "low");

        CreateResponseOptions raw = Assert.IsType<CreateResponseOptions>(
            options.ChatOptions!.RawRepresentationFactory!(null!));

        Assert.Equal(
            ResponseReasoningEffortLevel.Low,
            raw.ReasoningOptions!.ReasoningEffortLevel);
        Assert.False(raw.ParallelToolCallsEnabled);
    }

    [Fact]
    public void UnsupportedReasoningEffort_FailsClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AgentReasoningOptions.CreateResponseReasoningOptions("extreme"));
    }
}
