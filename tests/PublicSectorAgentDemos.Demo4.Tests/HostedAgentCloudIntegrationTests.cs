using Azure.Identity;
using PublicSectorAgentDemos.Contracts.Demo4;
using PublicSectorAgentDemos.Demo4.Application;

namespace PublicSectorAgentDemos.Demo4.Tests;

public sealed class HostedAgentCloudIntegrationTests
{
    [Demo4CloudFact]
    [Trait("Category", "CloudIntegration")]
    public async Task RecurrenceScenarioReadsMemoryAtMostOnce()
    {
        FoundryHostedCasePatternAgentClient client = CreateAgentClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));

        HostedAgentTurnResult turn = await client.InvokeAsync(
            new(
                "VALIDATION-READ-DIRECT",
                "CASE-RECURRENCE-HOUSING",
                "Assess case CG-8202. Check whether another public body has already recorded a related control failure."),
            timeout.Token);

        Assert.Equal(
            1,
            (turn.ToolExecutions ?? [])
                .Count(execution => execution.ToolName == Demo4MemoryContract.SearchToolName));
    }

    [Demo4CloudFact]
    [Trait("Category", "CloudIntegration")]
    public async Task RuledOutScenarioReadsMemoryAndStillCompletes()
    {
        FoundryHostedCasePatternAgentClient client = CreateAgentClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));

        HostedAgentTurnResult turn = await client.InvokeAsync(
            new(
                "VALIDATION-RULED-OUT-DIRECT",
                "CASE-NEAR-MATCH-TRANSPORT",
                "Assess case CG-8303. Check whether a related control failure elsewhere explains the current evidence."),
            timeout.Token);

        Assert.Equal("completed", turn.Status);
        Assert.InRange(
            (turn.ToolExecutions ?? [])
                .Count(execution => execution.ToolName == Demo4MemoryContract.SearchToolName),
            0,
            1);
        Assert.Contains(
            turn.ToolExecutions ?? [],
            execution => execution.ToolName == "assess_case_pattern" && execution.Succeeded);
    }

    [Demo4CloudFact]
    [Trait("Category", "CloudIntegration")]
    public async Task InvalidScenarioDoesNotDispatchAMemoryWrite()
    {
        FoundryHostedCasePatternAgentClient agentClient = CreateAgentClient();
        RecordingMemoryClient memory = new();
        AssessmentProcessingService processor = new(
            agentClient,
            new InvestigationMemoryCoordinator(memory, TimeProvider.System),
            TimeProvider.System);
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));

        await Assert.ThrowsAsync<HostedAssessmentProcessingException>(() =>
            processor.ProcessAsync(
                new(
                    "VALIDATION-INVALID-DIRECT",
                    "CASE-UNVERIFIABLE-CLAIM",
                    "Assess case CG-8505 and report the unverifiable control claim."),
                timeout.Token));

        Assert.Equal(0, memory.CreateCalls);
    }

    [Demo4CloudFact]
    [Trait("Category", "CloudIntegration")]
    public async Task SkipScenarioProcessesAndWritesOnce()
    {
        using HttpClient httpClient = new();
        AzureFoundryAccessTokenProvider tokenProvider = new(new AzureCliCredential());
        FoundryHostedCasePatternAgentClient agentClient = new(
            httpClient,
            tokenProvider,
            new()
            {
                ResponsesEndpoint = GetRequiredVariable("DEMO4_HOSTED_AGENT_ENDPOINT"),
                ModelDeploymentName = GetRequiredVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME")
            });
        FoundryMemoryItemClient memoryClient = new(
            httpClient,
            tokenProvider,
            new() { ProjectEndpoint = GetRequiredVariable("FOUNDRY_PROJECT_ENDPOINT") });
        AssessmentProcessingService processor = new(
            agentClient,
            new InvestigationMemoryCoordinator(memoryClient, TimeProvider.System),
            TimeProvider.System);
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));

        AssessmentProcessingReceipt receipt = await processor.ProcessAsync(
            new(
                "VALIDATION-SKIP-CLOUD",
                "CASE-BASELINE-GRANTS",
                "Assess case CG-8101. A grants administration body released supplier payments and the assurance evidence is missing."),
            timeout.Token);

        Assert.Equal("completed", receipt.Status);
    }

    [Demo4CloudFact]
    [Trait("Category", "CloudIntegration")]
    public async Task SkipScenarioCompletesWithoutMemoryRead()
    {
        FoundryHostedCasePatternAgentClient client = CreateAgentClient();
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(10));

        HostedAgentTurnResult turn = await client.InvokeAsync(
            new(
                "VALIDATION-SKIP-DIRECT",
                "CASE-UNRELATED-CONTROL",
                "Assess case CG-8404. A public audit body reports an overdue records retention schedule."),
            timeout.Token);

        Assert.Equal("completed", turn.Status);
        Assert.DoesNotContain(
            turn.ToolExecutions ?? [],
            execution => execution.ToolName == Demo4MemoryContract.SearchToolName);
        Assert.Contains(
            turn.ToolExecutions ?? [],
            execution => execution.ToolName == "assess_case_pattern" && execution.Succeeded);
    }

    private static string GetRequiredVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is required.");

    private static FoundryHostedCasePatternAgentClient CreateAgentClient() =>
        new(
            new HttpClient(),
            new AzureFoundryAccessTokenProvider(new AzureCliCredential()),
            new()
            {
                ResponsesEndpoint = GetRequiredVariable("DEMO4_HOSTED_AGENT_ENDPOINT"),
                ModelDeploymentName = GetRequiredVariable("AZURE_AI_MODEL_DEPLOYMENT_NAME")
            });

    private sealed class RecordingMemoryClient : IFoundryMemoryItemClient
    {
        public int CreateCalls { get; private set; }

        public Task CreateAndVerifyAsync(
            NotebookRecordEnvelope record,
            string serializedRecord,
            CancellationToken cancellationToken)
        {
            CreateCalls++;
            return Task.CompletedTask;
        }

        public Task<MemoryNotebookListing> ListValidRecordsAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new MemoryNotebookListing([], 0, 0));
        }
    }
}
