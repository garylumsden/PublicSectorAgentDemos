using System.Text.Json;
using PublicSectorAgentDemos.Demo1.FoundationAndGround;

namespace PublicSectorAgentDemos.Demo1.Web;

public sealed record AnswerEvidence(
    int CitationCount,
    int ApprovedCitationCount,
    IReadOnlyList<string> VerifiedQuotations,
    string Limitation);

public sealed record AssessmentReference(
    string Basis,
    string? ScenarioId,
    string? ExpectedRoute,
    string? ExpectedResponseKind,
    IReadOnlyList<string> ExpectedSignals,
    IReadOnlyList<string> ReviewRubric,
    AnswerEvidence FoundationEvidence,
    AnswerEvidence GroundEvidence,
    IReadOnlyList<string> Limitations);

public sealed class AssessmentReferenceBuilder
{
    private readonly IReadOnlyList<FixtureReference> fixtures;
    private readonly ApprovedCitationSource? approvedSource;
    private readonly string sourceLimitation;

    public AssessmentReferenceBuilder(IConfiguration configuration)
    {
        using Stream fixtureStream = typeof(Program).Assembly.GetManifestResourceStream("Demo1.Fixtures.json")
            ?? throw new InvalidOperationException("The embedded Demo 1 fixture is missing.");
        using JsonDocument fixtureDocument = JsonDocument.Parse(fixtureStream);
        fixtures = fixtureDocument.RootElement.GetProperty("fixtures").EnumerateArray().Select(item => new FixtureReference(
            item.GetProperty("scenarioId").GetString()!,
            item.GetProperty("prompt").GetString()!,
            item.GetProperty("expectedRoute").GetString()!,
            item.GetProperty("expectedResponseKind").GetString()!,
            ReadStrings(item, "expectedSignals"),
            ReadStrings(item, "reviewRubric"))).ToArray();

        (approvedSource, sourceLimitation) = TryLoadApprovedSource(configuration["DEMO1_CITATION_IDENTITY_PATH"]);
    }

    public AssessmentReference Build(string prompt, AgentAnswer foundation, AgentAnswer ground)
    {
        FixtureReference? fixture = fixtures.SingleOrDefault(item => item.Prompt.Equals(prompt, StringComparison.Ordinal));
        List<string> limitations = [];
        string basis;
        if (fixture is null)
        {
            basis = "Custom prompt. No reviewed expected outcome is available.";
            limitations.Add("Correctness against current policy can be Not assessed because this prompt has no reviewed fixture outcome.");
        }
        else
        {
            basis = $"Repository fixture {fixture.ScenarioId}, expected route {fixture.ExpectedRoute}, with application-owned review guidance.";
        }

        if (!string.IsNullOrEmpty(sourceLimitation)) limitations.Add(sourceLimitation);
        AnswerEvidence foundationEvidence = Inspect(foundation);
        AnswerEvidence groundEvidence = Inspect(ground);
        if (!string.IsNullOrEmpty(foundationEvidence.Limitation)) limitations.Add($"Foundation: {foundationEvidence.Limitation}");
        if (!string.IsNullOrEmpty(groundEvidence.Limitation)) limitations.Add($"Ground: {groundEvidence.Limitation}");

        return new(basis, fixture?.ScenarioId, fixture?.ExpectedRoute, fixture?.ExpectedResponseKind,
            fixture?.ExpectedSignals ?? [], fixture?.ReviewRubric ?? [], foundationEvidence, groundEvidence,
            limitations.Distinct(StringComparer.Ordinal).ToArray());
    }

    private AnswerEvidence Inspect(AgentAnswer answer)
    {
        if (approvedSource is null)
        {
            return new(answer.Citations.Count, 0, [], "Source identity is unavailable, so citation source and quotations were not verified.");
        }

        try
        {
            FoundryResponseCitationResult result = FoundryResponseCitationValidator.Inspect(answer.OriginalResponse, approvedSource);
            string[] quotations = result.Quotations
                .Where(item => item.IsVerbatimRetrievedExcerpt && item.HasSourceBoundary)
                .Select(item => item.Text)
                .Take(4)
                .ToArray();
            int approved = result.Citations.Count(item => item.ResolvesToApprovedSource && item.IsAttachedToOutputText);
            string limitation = result.AllCitationsAreValid
                ? ""
                : "One or more citation annotations did not resolve to the approved source.";
            return new(result.Citations.Count, approved, quotations, limitation);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
        {
            return new(answer.Citations.Count, 0, [], "The service response could not be checked against the approved citation identity.");
        }
    }

    private static (ApprovedCitationSource? Source, string Limitation) TryLoadApprovedSource(string? identityPath)
    {
        if (string.IsNullOrWhiteSpace(identityPath) || !File.Exists(identityPath))
        {
            return (null, "The approved citation identity file is missing or unavailable.");
        }

        try
        {
            using Stream manifestStream = typeof(Program).Assembly.GetManifestResourceStream("Demo1.SourceManifest.json")
                ?? throw new InvalidDataException("The embedded source manifest is missing.");
            using JsonDocument manifest = JsonDocument.Parse(manifestStream);
            JsonElement source = manifest.RootElement.GetProperty("sources")[0];
            using JsonDocument identity = JsonDocument.Parse(File.ReadAllText(identityPath));
            JsonElement root = identity.RootElement;
            string filename = source.GetProperty("expectedLocalFilename").GetString()!;
            string knowledgeSource = source.GetProperty("knowledgeSourceName").GetString()!;
            string manifestSourceId = source.GetProperty("sourceId").GetString()!;
            if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("filename").GetString() != filename ||
                root.GetProperty("knowledgeSourceName").GetString() != knowledgeSource ||
                root.GetProperty("manifestSourceId").GetString() != manifestSourceId)
            {
                return (null, "The citation identity does not match the pinned source manifest.");
            }

            HashSet<string> fileIds = ReadStrings(root, "fileIds").ToHashSet(StringComparer.Ordinal);
            HashSet<string> identityApprovedUris = ReadStrings(root, "approvedUris").ToHashSet(StringComparer.Ordinal);
            HashSet<string> approvedUris =
            [
                source.GetProperty("officialUrl").GetString()!,
                source.GetProperty("downloadUrl").GetString()!
            ];
            approvedUris.UnionWith(identityApprovedUris);
            if (fileIds.Count == 0 && identityApprovedUris.Count == 0)
            {
                return (null, "The citation identity contains no service citation identity.");
            }
            return (new(fileIds, filename, approvedUris), "");
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or KeyNotFoundException)
        {
            return (null, "The approved citation identity file is invalid or unavailable.");
        }
    }

    private static string[] ReadStrings(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item!).ToArray()
            : [];

    private sealed record FixtureReference(
        string ScenarioId,
        string Prompt,
        string ExpectedRoute,
        string ExpectedResponseKind,
        IReadOnlyList<string> ExpectedSignals,
        IReadOnlyList<string> ReviewRubric);
}
