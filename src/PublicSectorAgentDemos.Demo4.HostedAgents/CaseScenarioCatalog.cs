using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PublicSectorAgentDemos.Contracts.Demo4;

namespace PublicSectorAgentDemos.Demo4.HostedAgents;

public enum ExpectedMemoryAction
{
    Read,
    Skip
}

public enum ExpectedScenarioOutcome
{
    Completed,
    Invalid
}

public sealed record CaseScenario(
    string ScenarioId,
    string CaseType,
    string Prompt,
    string ContextId,
    ExpectedMemoryAction ExpectedMemoryAction,
    bool ExpectedMemoryRead,
    bool ExpectedMemoryWrite,
    ExpectedScenarioOutcome ExpectedOutcome,
    InvestigationOutcome AssessmentOutcome,
    string ReasonCode,
    string CaseReference,
    DateOnly From,
    DateOnly To,
    string Pattern,
    decimal Confidence,
    IReadOnlyList<CaseEvidence> Evidence);

internal sealed record CaseScenarioDocument(
    string ContractVersion,
    string DataVersion,
    bool Preview,
    IReadOnlyList<CaseScenario> Scenarios);

public sealed partial class CaseScenarioCatalog
{
    private const int MaximumCharacters = 128 * 1024;
    private static readonly HashSet<string> ApprovedScenarioIds = new(StringComparer.Ordinal)
    {
        "CASE-BASELINE-GRANTS",
        "CASE-RECURRENCE-HOUSING",
        "CASE-NEAR-MATCH-TRANSPORT",
        "CASE-UNRELATED-CONTROL",
        "CASE-UNVERIFIABLE-CLAIM"
    };
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly IReadOnlyDictionary<string, CaseScenario> _scenarios;

    public CaseScenarioCatalog(string path)
        : this(LoadFile(path))
    {
    }

    private CaseScenarioCatalog(IReadOnlyList<CaseScenario> scenarios)
    {
        Scenarios = scenarios;
        _scenarios = scenarios.ToDictionary(item => item.ScenarioId, StringComparer.Ordinal);
    }

    public IReadOnlyList<CaseScenario> Scenarios { get; }

    public CaseScenario GetRequired(string scenarioId) =>
        _scenarios.TryGetValue(scenarioId, out CaseScenario? scenario)
            ? scenario
            : throw new KeyNotFoundException("The case scenario was not found.");

    public static CaseScenarioCatalog FromJson(string json) =>
        new(LoadJson(json));

    private static IReadOnlyList<CaseScenario> LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileInfo file = new(Path.GetFullPath(path));
        if (!file.Exists || file.Length is <= 0 or > MaximumCharacters)
        {
            throw new InvalidOperationException("The case scenario fixture is missing or invalid.");
        }

        return LoadJson(File.ReadAllText(file.FullName));
    }

    private static IReadOnlyList<CaseScenario> LoadJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (json.Length > MaximumCharacters)
        {
            throw new InvalidOperationException("The case scenario fixture exceeds its size limit.");
        }

        CaseScenarioDocument document;
        try
        {
            document = JsonSerializer.Deserialize<CaseScenarioDocument>(json, SerializerOptions)
                ?? throw InvalidFixture();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("The case scenario fixture is invalid.", exception);
        }

        if (document.ContractVersion != "2.0.0" ||
            document.DataVersion != "v1" ||
            !document.Preview ||
            document.Scenarios is not { Count: 5 })
        {
            throw InvalidFixture();
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        HashSet<string> contextIds = new(StringComparer.Ordinal);
        HashSet<string> caseReferences = new(StringComparer.Ordinal);
        HashSet<string> caseTypes = new(StringComparer.Ordinal);
        foreach (CaseScenario scenario in document.Scenarios)
        {
            bool continuityRead = scenario.CaseType
                is "crossDepartmentRecurrence" or "priorContextRuledOut";
            bool continuitySkipped = scenario.CaseType
                is "baselineWrite" or "unrelatedControl";
            bool invalid = scenario.CaseType == "invalidAssessment";
            if (!ScenarioIdPattern().IsMatch(scenario.ScenarioId) ||
                !ids.Add(scenario.ScenarioId) ||
                !ContextIdPattern().IsMatch(scenario.ContextId) ||
                !contextIds.Add(scenario.ContextId) ||
                !CaseReferencePattern().IsMatch(scenario.CaseReference) ||
                !caseReferences.Add(scenario.CaseReference) ||
                !caseTypes.Add(scenario.CaseType) ||
                !(continuityRead || continuitySkipped || invalid) ||
                scenario.AssessmentOutcome != InvestigationOutcome.Completed ||
                scenario.To < scenario.From ||
                scenario.Confidence is < 0 or > 1 ||
                string.IsNullOrWhiteSpace(scenario.Prompt) ||
                string.IsNullOrWhiteSpace(scenario.ReasonCode) ||
                string.IsNullOrWhiteSpace(scenario.Pattern) ||
                scenario.Evidence is null or { Count: > 16 } ||
                ((continuityRead || continuitySkipped) &&
                    (scenario.ExpectedOutcome != ExpectedScenarioOutcome.Completed ||
                     scenario.Evidence.Count is < 2 or > 4 ||
                     !scenario.ExpectedMemoryWrite)) ||
                (continuityRead &&
                    (scenario.ExpectedMemoryAction != ExpectedMemoryAction.Read ||
                     !scenario.ExpectedMemoryRead)) ||
                (continuitySkipped &&
                    (scenario.ExpectedMemoryAction != ExpectedMemoryAction.Skip ||
                     scenario.ExpectedMemoryRead)) ||
                (invalid &&
                    (scenario.ExpectedOutcome != ExpectedScenarioOutcome.Invalid ||
                     scenario.ExpectedMemoryAction != ExpectedMemoryAction.Skip ||
                     scenario.ExpectedMemoryRead ||
                     scenario.ExpectedMemoryWrite ||
                     scenario.Evidence.Count != 0)))
            {
                throw InvalidFixture();
            }
        }

        if (!ids.SetEquals(ApprovedScenarioIds) || caseTypes.Count != 5)
        {
            throw InvalidFixture();
        }

        return Array.AsReadOnly(document.Scenarios.ToArray());
    }

    private static InvalidOperationException InvalidFixture() =>
        new("The case scenario fixture contract is invalid.");

    [GeneratedRegex(@"\ACASE-[A-Z0-9-]{3,80}\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex ScenarioIdPattern();

    [GeneratedRegex(@"\ACG-[0-9]{4,12}\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex CaseReferencePattern();

    [GeneratedRegex(@"\ACONTEXT-[A-Z0-9-]{3,80}\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex ContextIdPattern();
}
