using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using PublicSectorAgentDemos.Demo1.FoundationAndGround;

namespace PublicSectorAgentDemos.Demo1.CloudIntegrationTests;

public sealed class LiveAgentParityTests
{
    [CloudIntegrationFact]
    [Trait("Category", "CloudIntegration")]
    public async Task PreprovisionedAgentsFollowEveryDeclaredScenarioRoute()
    {
        string endpointValue = Environment.GetEnvironmentVariable("AZURE_AI_FOUNDRY_ENDPOINT")
            ?? throw new InvalidOperationException("AZURE_AI_FOUNDRY_ENDPOINT is required.");
        Uri endpoint = new(endpointValue.TrimEnd('/') + "/", UriKind.Absolute);
        AccessToken token = await new AzureCliCredential().GetTokenAsync(
            new TokenRequestContext(["https://ai.azure.com/.default"]),
            CancellationToken.None);
        using HttpClient client = new()
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromMinutes(3)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        LiveAgentContext context = await AssertLiveDefinitionParityAsync(client);

        IReadOnlyList<Scenario> scenarios = LoadScenarios();
        ApprovedCitationSource approvedSource = LoadApprovedSource();

        foreach (Scenario scenario in scenarios)
        {
            AgentResult? foundation = null;
            AgentResult? ground = null;
            try
            {
                foundation = await RunAgentAsync(
                    client, Demo1AgentCatalog.FoundationAgentName, scenario.Prompt, approvedSource, false, false);
                ground = await RunAgentAsync(
                    client, Demo1AgentCatalog.GroundAgentName, scenario.Prompt, approvedSource, scenario.RequiresGrounding, true);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or Xunit.Sdk.XunitException or JsonException)
            {
                string failure = $"{(foundation is null ? "Foundation" : "Ground")}: {exception.GetType().Name}";
                SaveComparison(scenario, context, foundation, ground, failure);
                // Do not expose service hosts or response bodies from transport exceptions.
                throw new InvalidOperationException($"{scenario.Id} request failed ({failure}). No retry was made.");
            }
            SaveComparison(scenario, context, foundation, ground);
            Assert.False(foundation.UsedKnowledgeBase);

            if (scenario.ExpectedResponseKind == "refusal")
            {
                Assert.Equal(GroundedResponseGate.UnsupportedRefusalMessage, foundation.Answer);
                Assert.Equal(GroundedResponseGate.UnsupportedRefusalMessage, ground.Answer);
                Assert.True(ground.Decision.IsExplicitRefusal);
                Assert.DoesNotContain("Assessment", ground.Answer, StringComparison.Ordinal);
                Assert.DoesNotContain("FourStandards", ground.Answer, StringComparison.Ordinal);
                Assert.False(ground.UsedKnowledgeBase);
                Assert.Empty(ground.Evidence.Citations);
                Assert.False(ground.Evidence.HasUnsupportedAnnotations);
                continue;
            }

            Assert.True(
                ground.Answer.Contains(scenario.ExpectedRouteMarker, StringComparison.Ordinal),
                $"{scenario.Id} Ground response failed: citations={ground.Evidence.Citations.Count}, " +
                $"units={ground.Evidence.MaterialUnits.Count}, " +
                $"validCitations={ground.Evidence.AllCitationsAreValid}, " +
                $"coveredUnits={ground.Evidence.AllMaterialUnitsHaveValidCitations}, " +
                $"verifiedQuotes={ground.Evidence.HasVerifiedQuotations}, " +
                $"application={ground.Evidence.HasApplication}, " +
                $"unsupportedAnnotations={ground.Evidence.HasUnsupportedAnnotations}.");

            Assert.True(scenario.RequiresGrounding);
            Assert.True(scenario.RequiresCitations);
            Assert.True(ground.Decision.CanAnswer, $"{scenario.Id} failed the runtime grounding gate.");
            Assert.True(ground.UsedKnowledgeBase, $"{scenario.Id} did not call knowledge_base_retrieve.");
            Assert.NotEmpty(ground.Evidence.Citations);
            Assert.NotEmpty(ground.Evidence.MaterialUnits);
            Assert.False(
                ground.Evidence.HasUnsupportedAnnotations,
                $"{scenario.Id} returned a non-citation output-text annotation.");
            Assert.True(
                ground.Evidence.AllCitationsAreValid,
                $"{scenario.Id} returned a citation that was detached or did not resolve to the pinned source.");
            Assert.True(
                ground.Evidence.AllMaterialUnitsHaveValidCitations,
                $"{scenario.Id} returned a material output unit without its own exact corpus citation.");
            Assert.True(ground.Evidence.HasVerifiedQuotations, $"{scenario.Id} did not return verbatim retrieved blockquotes.");
            Assert.True(ground.Evidence.HasApplication, $"{scenario.Id} omitted the Application explanation.");
        }
    }

    private static async Task<AgentResult> RunAgentAsync(
        HttpClient client,
        string agentName,
        string prompt,
        ApprovedCitationSource approvedSource,
        bool requireTool,
        bool applyGroundingGate)
    {
        string relativeUrl =
            $"agents/{Uri.EscapeDataString(agentName)}/endpoint/protocols/openai/responses?api-version=v1";
        Dictionary<string, object> payload = new()
        {
            ["input"] = new[]
            {
                new { role = "user", content = prompt }
            }
        };
        if (requireTool)
        {
            payload["tool_choice"] = "required";
        }

        string requestJson = JsonSerializer.Serialize(payload);
        using HttpResponseMessage response = await client.PostAsync(
            relativeUrl,
            new StringContent(requestJson, Encoding.UTF8, "application/json"),
            CancellationToken.None);
        string content = await response.Content.ReadAsStringAsync(CancellationToken.None);
        Assert.True(
            response.IsSuccessStatusCode,
            $"{agentName} returned HTTP {(int)response.StatusCode}.");

        FoundryResponseCitationResult evidence =
            FoundryResponseCitationValidator.Inspect(content, approvedSource);
        Assert.False(string.IsNullOrWhiteSpace(evidence.Text), $"{agentName} returned no output text.");
        bool usedKnowledgeBase = HasKnowledgeBaseCall(content);
        GroundedResponseDecision decision = applyGroundingGate
            ? GroundedResponseGate.EvaluateLiveResponse(usedKnowledgeBase, evidence)
            : new(true, evidence.Text);
        return new(
            evidence,
            usedKnowledgeBase,
            decision.Answer,
            decision);
    }

    private static bool HasKnowledgeBaseCall(string content)
    {
        using JsonDocument document = JsonDocument.Parse(content);
        return document.RootElement.TryGetProperty("output", out JsonElement output) &&
            output.ValueKind == JsonValueKind.Array &&
            output.EnumerateArray().Any(item =>
                item.TryGetProperty("type", out JsonElement type) &&
                type.ValueKind == JsonValueKind.String &&
                type.GetString() == "mcp_call" &&
                item.TryGetProperty("name", out JsonElement name) &&
                name.ValueKind == JsonValueKind.String &&
                name.GetString() == "knowledge_base_retrieve");
    }

    private static IReadOnlyList<Scenario> LoadScenarios()
    {
        string root = FindRepositoryRoot();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "data",
            "ground",
            "v1",
            "fixture-set.json")));
        string? selectedId = Environment.GetEnvironmentVariable("DEMO1_SCENARIO_ID");
        Scenario[] scenarios = document.RootElement.GetProperty("fixtures").EnumerateArray()
            .Select(item => new Scenario(
                item.GetProperty("scenarioId").GetString()!,
                item.GetProperty("prompt").GetString()!,
                item.GetProperty("expectedRoute").GetString()!,
                item.GetProperty("expectedRouteMarker").GetString()!,
                item.GetProperty("expectedResponseKind").GetString()!,
                item.GetProperty("requiresGrounding").GetBoolean(),
                item.GetProperty("requiresCitations").GetBoolean()))
            .Where(scenario => selectedId is null || scenario.Id == selectedId)
            .ToArray();
        Assert.NotEmpty(scenarios);
        return scenarios;
    }

    private static async Task<LiveAgentContext> AssertLiveDefinitionParityAsync(HttpClient client)
    {
        List<JsonElement> definitions = [];
        List<string> versions = [];
        foreach (string name in new[] { Demo1AgentCatalog.FoundationAgentName, Demo1AgentCatalog.GroundAgentName })
        {
            using HttpResponseMessage response = await client.GetAsync($"agents/{name}?api-version=v1");
            Assert.True(response.IsSuccessStatusCode, $"Could not read {name}: HTTP {(int)response.StatusCode}.");
            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement latest = document.RootElement.GetProperty("versions").GetProperty("latest");
            definitions.Add(latest.GetProperty("definition").Clone());
            versions.Add(latest.GetProperty("version").GetString()!);
        }

        JsonElement foundation = definitions[0];
        JsonElement ground = definitions[1];
        Assert.Equal(foundation.GetProperty("model").GetString(), ground.GetProperty("model").GetString());
        Assert.Equal(foundation.GetProperty("reasoning").GetProperty("effort").GetString(),
            ground.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.True(!foundation.TryGetProperty("tools", out JsonElement tools) || tools.GetArrayLength() == 0);
        string baseInstructions = foundation.GetProperty("instructions").GetString()!;
        Assert.StartsWith(baseInstructions, ground.GetProperty("instructions").GetString()!, StringComparison.Ordinal);
        string root = FindRepositoryRoot();
        Demo1AgentCatalogDocument catalog = Demo1AgentCatalog.Load(root);
        string expectedBase = Demo1AgentCatalog.Resolve(root, catalog, "foundation").Instructions.TrimEnd().ReplaceLineEndings("\n");
        Assert.Equal(expectedBase, baseInstructions.ReplaceLineEndings("\n"));
        Assert.Equal(Demo1AgentCatalog.Resolve(root, catalog, "ground").Instructions.TrimEnd().ReplaceLineEndings("\n"),
            ground.GetProperty("instructions").GetString()!.ReplaceLineEndings("\n"));
        Assert.Equal(catalog.Definitions[0].Model, foundation.GetProperty("model").GetString());
        Assert.Equal(catalog.Definitions[0].ReasoningEffort, foundation.GetProperty("reasoning").GetProperty("effort").GetString());
        return new(versions[0], versions[1], foundation.GetProperty("model").GetString()!,
            foundation.GetProperty("reasoning").GetProperty("effort").GetString()!);
    }

    private static void SaveComparison(
        Scenario scenario,
        LiveAgentContext context,
        AgentResult? foundation,
        AgentResult? ground,
        string? requestFailure = null)
    {
        string? directory = Environment.GetEnvironmentVariable("DEMO1_REHEARSAL_RESULTS_PATH");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfff}-{scenario.Id}-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            scenario = scenario.Id,
            prompt = scenario.Prompt,
            context,
            attemptsPerAgent = 1,
            requestFailure,
            foundation = foundation is null ? null : new { answer = foundation.Evidence.Text, foundation.UsedKnowledgeBase },
            ground = ground is null ? null : new
            {
                answer = ground.Evidence.Text,
                ground.UsedKnowledgeBase,
                ground.Decision.CanAnswer,
                ground.Evidence.HasVerifiedQuotations,
                ground.Evidence.HasApplication,
                ground.Evidence.AllCitationsAreValid,
                ground.Evidence.AllMaterialUnitsHaveValidCitations,
                ground.Evidence.Quotations
            },
            limitation = "Structural and quotation checks are not a factual-accuracy score. Review each answer against the scenario rubric."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static ApprovedCitationSource LoadApprovedSource()
    {
        string root = FindRepositoryRoot();
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root,
            "data",
            "ground",
            "v1",
            "source-manifest.json")));
        JsonElement source = document.RootElement.GetProperty("sources")[0];
        string identityPath = GetRequiredEnvironmentVariable("DEMO1_CITATION_IDENTITY_PATH");
        if (!Path.IsPathRooted(identityPath))
        {
            identityPath = Path.Combine(root, identityPath);
        }

        if (!File.Exists(identityPath))
        {
            throw new InvalidOperationException(
                $"DEMO1_CITATION_IDENTITY_PATH does not identify a file: {identityPath}");
        }

        using JsonDocument identity = JsonDocument.Parse(File.ReadAllText(identityPath));
        JsonElement identityRoot = identity.RootElement;
        string expectedFilename = source.GetProperty("expectedLocalFilename").GetString()!;
        string expectedKnowledgeSource = source.GetProperty("knowledgeSourceName").GetString()!;
        string expectedManifestSource = source.GetProperty("sourceId").GetString()!;
        if (identityRoot.GetProperty("schemaVersion").GetInt32() != 1 ||
            !identityRoot.GetProperty("filename").GetString()!.Equals(
                expectedFilename,
                StringComparison.Ordinal) ||
            !identityRoot.GetProperty("knowledgeSourceName").GetString()!.Equals(
                expectedKnowledgeSource,
                StringComparison.Ordinal) ||
            !identityRoot.GetProperty("manifestSourceId").GetString()!.Equals(
                expectedManifestSource,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "DEMO1_CITATION_IDENTITY_PATH does not identify the pinned manifest corpus.");
        }

        HashSet<string> fileIds = identityRoot.GetProperty("fileIds")
            .EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> approvedUris =
        [
            source.GetProperty("officialUrl").GetString()!,
            source.GetProperty("downloadUrl").GetString()!
        ];
        HashSet<string> identityApprovedUris = [];
        if (identityRoot.TryGetProperty("approvedUris", out JsonElement identityUris))
        {
            identityApprovedUris.UnionWith(identityUris
                .EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!));
            approvedUris.UnionWith(identityApprovedUris);
        }

        if (fileIds.Count == 0 && identityApprovedUris.Count == 0)
        {
            throw new InvalidOperationException(
                "DEMO1_CITATION_IDENTITY_PATH contains no service citation identity.");
        }

        return new(
            fileIds,
            expectedFilename,
            approvedUris);
    }

    private static string GetRequiredEnvironmentVariable(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is required.");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PublicSectorAgentDemos.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository root was not found.");
    }

    private sealed record Scenario(
        string Id,
        string Prompt,
        string ExpectedRoute,
        string ExpectedRouteMarker,
        string ExpectedResponseKind,
        bool RequiresGrounding,
        bool RequiresCitations);

    private sealed record AgentResult(
        FoundryResponseCitationResult Evidence,
        bool UsedKnowledgeBase,
        string Answer,
        GroundedResponseDecision Decision);

    private sealed record LiveAgentContext(string FoundationVersion, string GroundVersion, string Model, string ReasoningEffort);
}
