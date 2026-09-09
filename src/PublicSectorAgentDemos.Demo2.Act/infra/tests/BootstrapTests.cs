using System.Text.Json;
using Defra.Bootstrap;

namespace Demo2.Infrastructure.Tests;

public sealed class BootstrapTests
{
    private static string ProjectRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static BootstrapConfiguration Configuration(string? catalogPath = null) => new(
        new Uri("https://example.services.ai.azure.com/api/projects/demo2"),
        "00000000-0000-0000-0000-000000000001",
        "gpt-5.4-mini",
        "low",
        ProjectRoot,
        catalogPath ?? Path.Combine(ProjectRoot, "config", "agents", "catalog.v1.json"));

    private static AgentSpecification Specification() => Assert.Single(AgentCatalogLoader.Load(
        Configuration(), _ => "https://tools.example/mcp/flood-support"));

    [Fact]
    public void BundledCatalog_RequiresFloodSupportToolsAndReservationApproval()
    {
        AgentSpecification agent = Specification();
        Assert.Equal("cross-government-flood-support", agent.Name);
        Assert.Equal("gpt-5.4-mini", agent.Model);
        Assert.Equal("low", agent.ReasoningEffort);
        Assert.Equal(File.ReadAllText(Path.Combine(ProjectRoot,
            "config", "agents", "demo2", "flood-support.md")).Trim(), agent.Instructions);
        Assert.Equal("flood-support-connection", agent.Mcp!.ProjectConnectionId);
        Assert.Equal(new[] { "reserveSupportPackage" }, agent.Mcp.AlwaysRequireApproval);
        Assert.Equal(4, agent.Mcp.AllowedTools.Count);
    }

    [Theory]
    [InlineData("http://tools.example/mcp/flood-support")]
    [InlineData("https://user:password@tools.example/mcp/welfare")]
    [InlineData("https://tools.example/mcp/flood-support?token=not-a-secret")]
    [InlineData("https://tools.example/mcp/welfare")]
    [InlineData("https://tools.example:8443/mcp/flood-support")]
    public void Catalog_RejectsUnsafeEndpoint(string endpoint) =>
        Assert.Throws<BootstrapConfigurationException>(() => AgentCatalogLoader.Load(
            Configuration(), _ => endpoint));

    [Fact]
    public void Catalog_RejectsUserInfoOnTheCorrectFloodEndpoint()
    {
        UriBuilder endpoint = new("https://tools.example/mcp/flood-support")
        {
            UserName = "fixture-user"
        };

        Assert.Throws<BootstrapConfigurationException>(() => AgentCatalogLoader.Load(
            Configuration(), _ => endpoint.Uri.AbsoluteUri));
    }

    [Theory]
    [InlineData("approval")]
    [InlineData("path")]
    [InlineData("model")]
    public void Catalog_RejectsUnsafeContractChanges(string change)
    {
        string file = Path.GetTempFileName();
        try
        {
            AgentSpecification agent = Specification();
            AgentCatalogEntry entry = new(agent.Name, agent.DisplayName, "demo2",
                change == "path" ? "../outside.md" : "config/agents/demo2/flood-support.md",
                new("flood-support", "DEFRA_TOOLS_MCP_FLOOD_SUPPORT_URL", "flood-support-connection",
                    agent.Mcp!.AllowedTools.ToArray(), change == "approval" ? [] : ["reserveSupportPackage"]),
                change == "model" ? "fast" : "quality");
            File.WriteAllText(file, JsonSerializer.Serialize(new AgentCatalog("1.0.0", [entry]),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Throws<BootstrapConfigurationException>(() => AgentCatalogLoader.Load(
                Configuration(file), _ => "https://tools.example/mcp/flood-support"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void DefinitionHash_IsStableAndTracksPromptAndApproval()
    {
        AgentSpecification agent = Specification();
        string hash = AgentDefinitionHasher.Compute(agent);
        Assert.Equal(hash, AgentDefinitionHasher.Compute(Specification()));
        Assert.NotEqual(hash, AgentDefinitionHasher.Compute(agent with { Instructions = "Changed prompt." }));
        Assert.NotEqual(hash, AgentDefinitionHasher.Compute(agent with
        {
            Mcp = agent.Mcp! with { AlwaysRequireApproval = [] }
        }));
    }

    [Fact]
    public void Infrastructure_AuthorizesBothApprovalControlledReservationPaths()
    {
        string template = File.ReadAllText(Path.Combine(
            ProjectRoot, "infra", "modules", "platform.bicep"));

        Assert.Contains(
            "MCP_FLOOD_SUPPORT_ACTION_CALLER_PRINCIPAL_ID: '${foundryProjectPrincipalId},${demo2Identity.outputs.principalId}'",
            template,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Infrastructure_IsolatesFloodStateAndConnectsTheCorrectEndpoint()
    {
        string template = File.ReadAllText(Path.Combine(
            ProjectRoot, "infra", "modules", "platform.bicep"));

        Assert.Contains("var demo2ContainerName = 'demo2-flood-support-cases'", template);
        Assert.Contains("target: '${toolsSiteUrl}/mcp/flood-support'", template);
        Assert.Contains("name: 'flood-support-connection'", template);
        Assert.Contains("DEFRA_TOOLS_MCP_URL: '${toolsSiteUrl}/mcp/flood-support'", template);
        Assert.Contains("disableLocalAuthentication: true", template);
        Assert.DoesNotContain("MCP_WELFARE_ACTION_CALLER_PRINCIPAL_ID", template);
    }

    [Fact]
    public async Task Provisioner_CreatesFreshAgentAndSkipsUnchangedRerun()
    {
        AgentSpecification agent = Specification();
        MemoryStore store = new();
        AgentProvisioner provisioner = new(store);
        Assert.Equal("created", (await provisioner.EnsureAsync(agent, CancellationToken.None)).Action);
        Assert.Equal("unchanged", (await provisioner.EnsureAsync(agent, CancellationToken.None)).Action);
        Assert.Equal(1, store.CreateCount);
        Assert.Equal("created", (await provisioner.EnsureAsync(
            agent with { Instructions = "Changed prompt." }, CancellationToken.None)).Action);
        Assert.Equal(2, store.CreateCount);
    }

    private sealed class MemoryStore : IAgentVersionStore
    {
        private AgentVersionState? _latest;
        public int CreateCount { get; private set; }

        public Task<AgentVersionState?> GetLatestAsync(string agentName, CancellationToken cancellationToken) =>
            Task.FromResult(_latest);

        public Task<string> CreateAsync(AgentSpecification specification, string definitionHash,
            CancellationToken cancellationToken)
        {
            string version = (++CreateCount).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _latest = new(specification.Name, version, definitionHash);
            return Task.FromResult(version);
        }
    }
}
