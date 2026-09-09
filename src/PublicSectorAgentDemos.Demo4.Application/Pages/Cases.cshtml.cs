using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PublicSectorAgentDemos.Demo4.Application.Pages;

[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class CasesModel : PageModel
{
    public IReadOnlyList<CaseFixtureView> Cases { get; private set; } = [];

    public void OnGet()
    {
        using Stream caseStream = typeof(Program).Assembly.GetManifestResourceStream("Demo4.CaseFixtures.json")
            ?? throw new InvalidOperationException("The embedded case fixture is missing.");
        using Stream contextStream = typeof(Program).Assembly.GetManifestResourceStream("Demo4.ContextFixtures.json")
            ?? throw new InvalidOperationException("The embedded case context fixture is missing.");
        using JsonDocument caseDocument = JsonDocument.Parse(caseStream);
        using JsonDocument contextDocument = JsonDocument.Parse(contextStream);
        Dictionary<string, CaseContextView> contexts = contextDocument.RootElement.GetProperty("contexts")
            .EnumerateArray()
            .Select(context => new CaseContextView(
                RequiredString(context, "contextId"),
                RequiredString(context, "topic"),
                context.GetProperty("observations").EnumerateArray()
                    .Select(observation => new CaseContextObservationView(
                        RequiredString(observation, "category"),
                        RequiredString(observation, "note")))
                    .ToArray()))
            .ToDictionary(context => context.ContextId, StringComparer.Ordinal);

        Cases = caseDocument.RootElement.GetProperty("scenarios").EnumerateArray()
            .Select(scenario =>
            {
                string contextId = RequiredString(scenario, "contextId");
                return new CaseFixtureView(
                    RequiredString(scenario, "scenarioId"),
                    RequiredString(scenario, "caseType"),
                    RequiredString(scenario, "prompt"),
                    contextId,
                    contexts[contextId],
                    RequiredString(scenario, "expectedMemoryAction"),
                    scenario.GetProperty("expectedMemoryRead").GetBoolean(),
                    scenario.GetProperty("expectedMemoryWrite").GetBoolean(),
                    RequiredString(scenario, "expectedOutcome"),
                    RequiredString(scenario, "caseReference"),
                    DateOnly.ParseExact(RequiredString(scenario, "from"), "yyyy-MM-dd"),
                    DateOnly.ParseExact(RequiredString(scenario, "to"), "yyyy-MM-dd"),
                    RequiredString(scenario, "reasonCode"),
                    RequiredString(scenario, "pattern"),
                    scenario.GetProperty("confidence").GetDecimal(),
                    scenario.GetProperty("evidence").EnumerateArray()
                        .Select(evidence => new CaseEvidenceView(
                            RequiredString(evidence, "evidenceId"),
                            RequiredString(evidence, "sourceId"),
                            RequiredString(evidence, "summary")))
                        .ToArray());
            })
            .ToArray();
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        element.GetProperty(propertyName).GetString()
        ?? throw new InvalidDataException($"The embedded case fixture has no {propertyName}.");
}

public sealed record CaseFixtureView(
    string ScenarioId,
    string CaseType,
    string Prompt,
    string ContextId,
    CaseContextView Context,
    string ExpectedMemoryAction,
    bool ExpectedMemoryRead,
    bool ExpectedMemoryWrite,
    string ExpectedOutcome,
    string CaseReference,
    DateOnly From,
    DateOnly To,
    string ReasonCode,
    string Pattern,
    decimal Confidence,
    IReadOnlyList<CaseEvidenceView> Evidence);

public sealed record CaseEvidenceView(string EvidenceId, string SourceId, string Summary);
public sealed record CaseContextView(string ContextId, string Topic, IReadOnlyList<CaseContextObservationView> Observations);
public sealed record CaseContextObservationView(string Category, string Note);
