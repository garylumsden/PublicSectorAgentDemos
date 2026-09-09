namespace GovernanceCouncil.Agents.Runtime;

using Azure.AI.OpenAI;
using Azure.Core;
using GovernanceCouncil.Agents.Provisioning;
using GovernanceCouncil.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

/// <summary>
/// Local Microsoft Agent Framework runtime: one function-invoking <see cref="IChatClient"/> per member
/// directly on the Foundry chat models (no Foundry agent provisioning). Each member runs its embedded
/// persona prompt + a grounding tool (Web IQ / Foundry IQ over REST), scoped to the member's
/// authoritative domains. Bids + speaker-selection run on a fast NON-reasoning model
/// (<see cref="CouncilModels.BidModel"/>) via <see cref="FastClient"/>.
/// </summary>
public sealed class MafCouncilRuntime : ICouncilRuntime
{
    private readonly AzureOpenAIClient _openAi;
    private readonly GroundingTools.Config _grounding;
    private readonly ILogger<MafCouncilRuntime> _logger;

    public MafCouncilRuntime(
        Uri azureOpenAIEndpoint,
        TokenCredential credential,
        HttpClient httpClient,
        string? searchEndpoint,
        string knowledgeBaseName,
        string? webIqApiKey,
        ILogger<MafCouncilRuntime> logger)
    {
        _openAi = new AzureOpenAIClient(azureOpenAIEndpoint, credential);
        _grounding = new GroundingTools.Config(httpClient, credential, searchEndpoint, knowledgeBaseName, webIqApiKey, logger);
        _logger = logger;
    }

    public string[] MemberNames { get; private set; } = [];
    public IReadOnlyList<IChatClient> MemberClients { get; private set; } = [];
    public IChatClient ChairClient { get; private set; } = null!;
    public IChatClient? ModeratorClient => null; // selection runs on FastClient
    public IChatClient? FastClient { get; private set; }
    public bool IsLoaded { get; private set; }

    public Task LoadAsync(CancellationToken ct = default)
    {
        if (!IsLoaded) Build();
        return Task.CompletedTask;
    }

    public Task ReloadAsync(CancellationToken ct = default)
    {
        IsLoaded = false;
        Build();
        return Task.CompletedTask;
    }

    private void Build()
    {
        var members = CouncilMembers.DeliberationMembers;
        MemberNames = members.Select(m => $"gc-{m.Id}").ToArray();
        MemberClients = members.Select(m => WithEffort(m.Tier, m.ModelDeployment, BuildPersonaClient(m))).ToList();
        ChairClient = WithEffort(CouncilMembers.Chair.Tier, CouncilMembers.Chair.ModelDeployment, BuildPersonaClient(CouncilMembers.Chair));
        FastClient = WithEffort(CouncilModels.ModelTier.Fast, CouncilModels.BidModel, ModelClient(CouncilModels.BidModel));

        IsLoaded = true;
        _logger.LogInformation(
            "MAF runtime built {Count} member clients + chair (bid {Bid}; effort personas={P} / chair={C} / bid={B})",
            MemberClients.Count, CouncilModels.BidModel,
            CouncilModels.ReasoningEffortFor(CouncilModels.ModelTier.Reasoning) ?? "default",
            CouncilModels.ReasoningEffortFor(CouncilModels.ModelTier.Synthesis) ?? "default",
            CouncilModels.ReasoningEffortFor(CouncilModels.ModelTier.Fast) ?? "default");
    }

    /// <summary>Wraps a reasoning-capable model's client to run at its tier's reasoning effort.</summary>
    private static IChatClient WithEffort(CouncilModels.ModelTier tier, string model, IChatClient client) =>
        CouncilModels.ReasoningEffortFor(tier) is { } effort && CouncilModels.SupportsReasoningEffort(model)
            ? new ReasoningEffortChatClient(client, CouncilModels.NormalizeEffort(model, effort))
            : client;

    /// <summary>Max grounding-tool calls a single debate turn may make (hard cap; tool-eager models are stopped at this).</summary>
    private const int MaxSearchCallsPerTurn = 2;

    private IChatClient BuildPersonaClient(CouncilMember member)
    {
        // Function-invoking pipeline so grounding tool calls execute automatically in the loop.
        // Cap iterations so a tool-eager model (notably Grok) can't loop the grounding tool many times
        // per turn — bounds search calls and the per-turn context growth.
        var pipeline = ModelClient(member.ModelDeployment)
            .AsBuilder()
            .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = 3)
            .Build();

        // Per-member, per-turn grounding budget — a hard stop on the number of search calls a single
        // turn can fire, regardless of how eagerly the model requests the tool.
        var budget = new GroundingTools.SearchBudget();
        var tool = GroundingTools.ForMember(member, _grounding, budget);
        var prompt = CouncilPrompts.Load(member);

        if (tool is null)
            return new PersonaChatClient(pipeline, prompt, null);

        var instructions = prompt + $"""


            ---

            Use the `{tool.Name}` tool to gather current, authoritative evidence before you answer,
            and cite the source URLs it returns. Do not use any other source. Call it **at most
            {MaxSearchCallsPerTurn} times** per turn with focused queries — do not call it repeatedly
            or once per detail; gather what you need, then answer.
            """;

        // Reset the budget once per turn, OUTSIDE the function-invocation loop.
        return new SearchBudgetResetChatClient(
            new PersonaChatClient(pipeline, instructions, [tool]),
            budget, MaxSearchCallsPerTurn);
    }

    private IChatClient ModelClient(string deployment) =>
        _openAi.GetChatClient(deployment).AsIChatClient();
}
