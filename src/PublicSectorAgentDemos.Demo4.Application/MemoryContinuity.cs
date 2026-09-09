using System.Text.Json;
using System.Text.Json.Serialization;
using PublicSectorAgentDemos.Contracts.Demo4;

namespace PublicSectorAgentDemos.Demo4.Application;

public enum MemoryMatchVerdict
{
    MatchedRepeatControlFailure,
    EarlierRunOfThisCase,
    RuledOutDifferentControl
}

public sealed record MemoryReferenceView(
    string MemoryId,
    string RecordId,
    string InvestigationReference,
    string CaseReference,
    string Pattern,
    DateOnly From,
    DateOnly To,
    string ReasonCode,
    decimal Confidence,
    IReadOnlyList<string> EvidenceIds,
    string RecommendedFollowUp,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    MemoryMatchVerdict Verdict);

public sealed record MemoryReadOutcome(
    bool Searched,
    int ReturnedCount,
    DateTimeOffset? FreshestUpdatedAt,
    IReadOnlyList<MemoryReferenceView> References,
    string ContinuityStatement)
{
    public string ApplyTo(string recommendedFollowUp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recommendedFollowUp);
        return $"{recommendedFollowUp.TrimEnd()} {ContinuityStatement}";
    }
}

public static class MemoryMatchVerdictText
{
    public static string Describe(MemoryMatchVerdict verdict) => verdict switch
    {
        MemoryMatchVerdict.MatchedRepeatControlFailure =>
            "Matched: repeat control failure in another case",
        MemoryMatchVerdict.EarlierRunOfThisCase =>
            "Matched: earlier run of this case",
        MemoryMatchVerdict.RuledOutDifferentControl =>
            "Ruled out: different control",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict))
    };
}

public static class MemoryContinuityReader
{
    public const string NotConsultedStatement =
        "Memory changed nothing. The agent did not consult the shared notebook for this case.";

    public const string NoPriorReferenceStatement =
        "Memory changed nothing. The notebook search returned no prior reference for this case.";

    internal const int MaximumSearchResultCharacters = 16_384;

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false
        };

    public static MemoryReadOutcome Read(
        IReadOnlyList<TrustedToolExecution> toolExecutions,
        CasePatternAssessment assessment,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(toolExecutions);
        ArgumentNullException.ThrowIfNull(assessment);
        TrustedToolExecution[] searches = toolExecutions
            .Where(execution => execution.ToolName == Demo4MemoryContract.SearchToolName)
            .ToArray();
        if (searches.Length > 1)
        {
            throw new UnsafeMemoryReferenceException(
                "memory_search.duplicate",
                "The hosted agent called the read-only Memory function more than once.");
        }

        if (searches.Length == 0)
        {
            return new(false, 0, null, [], NotConsultedStatement);
        }

        Demo4MemorySearchResult search = Parse(searches[0]);
        MemoryReferenceView[] references = Project(search, assessment, now);
        return new(
            true,
            references.Length,
            references.Length == 0
                ? null
                : references.Max(reference => reference.UpdatedAt),
            references,
            BuildStatement(references));
    }

    private static Demo4MemorySearchResult Parse(TrustedToolExecution search)
    {
        if (!search.Succeeded ||
            string.IsNullOrWhiteSpace(search.StructuredResultJson) ||
            search.StructuredResultJson.Length > MaximumSearchResultCharacters)
        {
            throw Invalid("The Memory search did not return one bounded trusted result.");
        }

        try
        {
            return JsonSerializer.Deserialize<Demo4MemorySearchResult>(
                search.StructuredResultJson,
                SerializerOptions) ?? throw Invalid();
        }
        catch (JsonException exception)
        {
            throw new UnsafeMemoryReferenceException(
                "memory_search.invalid_result",
                "The Memory search result was not an application-owned envelope.",
                exception);
        }
    }

    private static MemoryReferenceView[] Project(
        Demo4MemorySearchResult search,
        CasePatternAssessment assessment,
        DateTimeOffset now)
    {
        if (!search.Consulted ||
            search.Records is null ||
            search.Records.Count > Demo4MemoryContract.MaximumSearchRecords ||
            search.ReturnedCount != search.Records.Count)
        {
            throw Invalid("The Memory search result reported an unsupported record count.");
        }

        HashSet<string> memoryIds = new(StringComparer.Ordinal);
        HashSet<string> recordIds = new(StringComparer.Ordinal);
        List<DateTimeOffset> validatedUpdates = [];
        List<MemoryReferenceView> references = [];
        foreach (Demo4MemoryReference reference in search.Records)
        {
            if (reference is null ||
                !IsSafeIdentifier(reference.MemoryId) ||
                reference.UpdatedAt < now.AddSeconds(-Demo4MemoryContract.RequiredTtlSeconds) ||
                reference.UpdatedAt > now.AddMinutes(1) ||
                reference.Record is null)
            {
                throw Invalid("A Memory search record failed its identifier or lifetime bound.");
            }

            NotebookRecordEnvelope record = NotebookRecordCodec.DeserializeAndValidate(
                NotebookRecordCodec.Serialize(reference.Record),
                now,
                Demo4MemoryContract.RequiredTtlSeconds);
            if (!memoryIds.Add(reference.MemoryId) || !recordIds.Add(record.RecordId))
            {
                throw Invalid("The Memory search result repeated one record.");
            }

            validatedUpdates.Add(reference.UpdatedAt);
            if (string.Equals(
                record.CaseReference,
                assessment.CaseReference,
                StringComparison.Ordinal))
            {
                continue;
            }

            references.Add(new(
                reference.MemoryId,
                record.RecordId,
                record.InvestigationReference,
                record.CaseReference,
                record.Pattern,
                record.From,
                record.To,
                record.ReasonCode,
                record.Confidence,
                record.EvidenceIds,
                record.RecommendedFollowUp,
                record.CreatedAt,
                reference.UpdatedAt,
                Judge(record, assessment)));
        }

        DateTimeOffset? freshest = validatedUpdates.Count == 0
            ? null
            : validatedUpdates.Max();
        if (search.FreshestUpdatedAt != freshest)
        {
            throw Invalid("The Memory search result reported an unsupported freshest timestamp.");
        }

        return references
            .OrderByDescending(reference => reference.UpdatedAt)
            .ThenBy(reference => reference.RecordId, StringComparer.Ordinal)
            .ToArray();
    }

    private static MemoryMatchVerdict Judge(
        NotebookRecordEnvelope record,
        CasePatternAssessment assessment)
    {
        if (!string.Equals(record.ReasonCode, assessment.ReasonCode, StringComparison.Ordinal))
        {
            return MemoryMatchVerdict.RuledOutDifferentControl;
        }

        return string.Equals(
            record.CaseReference,
            assessment.CaseReference,
            StringComparison.Ordinal)
                ? MemoryMatchVerdict.EarlierRunOfThisCase
                : MemoryMatchVerdict.MatchedRepeatControlFailure;
    }

    private static string BuildStatement(IReadOnlyList<MemoryReferenceView> references)
    {
        if (references.Count == 0)
        {
            return NoPriorReferenceStatement;
        }

        string[] matched = references
            .Where(reference =>
                reference.Verdict == MemoryMatchVerdict.MatchedRepeatControlFailure)
            .Select(reference => reference.CaseReference)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(caseReference => caseReference, StringComparer.Ordinal)
            .ToArray();
        int earlierRuns = references.Count(reference =>
            reference.Verdict == MemoryMatchVerdict.EarlierRunOfThisCase);
        int ruledOut = references.Count(reference =>
            reference.Verdict == MemoryMatchVerdict.RuledOutDifferentControl);
        List<string> sentences = [];
        if (matched.Length > 0)
        {
            sentences.Add(
                "Memory changed the read of this case. The notebook matched the same reason code in " +
                $"prior case {(matched.Length == 1 ? "reference" : "references")} {string.Join(", ", matched)}.");
        }
        else
        {
            sentences.Add("Memory changed nothing in the current evidence.");
        }

        if (earlierRuns > 0)
        {
            sentences.Add(
                $"The notebook also returned {earlierRuns} earlier {Plural(earlierRuns, "run")} of this case.");
        }

        if (ruledOut > 0)
        {
            sentences.Add(
                $"{ruledOut} ruled-out {Plural(ruledOut, "record")} used a different reason code " +
                "and did not alter the current evidence.");
        }

        return string.Join(' ', sentences);
    }

    private static string Plural(int count, string singular) =>
        count == 1 ? singular : $"{singular}s";

    private static bool IsSafeIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) ||
            character is '-' or '_' or '.' or ':');

    private static UnsafeMemoryReferenceException Invalid(
        string detail = "The Memory search result was not an application-owned envelope.") =>
        new("memory_search.invalid_result", detail);
}
