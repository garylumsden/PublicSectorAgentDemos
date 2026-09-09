using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PublicSectorAgentDemos.Contracts.Demo4;

public enum InvestigationOutcome
{
    Completed,
    Refused,
    Unsupported,
    InsufficientEvidence
}

public sealed record CaseEvidence(
    string EvidenceId,
    string SourceId,
    string Summary);

public sealed record InvestigationBoundaries(
    string EvidenceBoundary,
    string ContextBoundary,
    string ReferenceBoundary,
    string GuidanceBoundary);

public sealed record CasePatternAssessment(
    string ScenarioId,
    string CaseReference,
    DateOnly From,
    DateOnly To,
    InvestigationOutcome Outcome,
    string Pattern,
    decimal Confidence,
    string ReasonCode,
    IReadOnlyList<CaseEvidence> Evidence,
    InvestigationBoundaries Boundaries);

public sealed record TrustedToolExecution(
    string CallId,
    string ToolName,
    bool Succeeded,
    string? StructuredResultJson);

public sealed record ValidatedInvestigationResult(
    string InvestigationReference,
    CasePatternAssessment Assessment,
    IReadOnlyList<TrustedToolExecution> ToolExecutions,
    string RecommendedFollowUp);

public static class Demo4MemoryContract
{
    public const string StoreName = "demo4-cross-government-control-memory";
    public const string Scope = "demo4-cross-government-control-notebook";
    public const string SearchToolName = "search_investigation_memory";
    public const int RequiredTtlSeconds = 604_800;
    public const int MaximumSearchRecords = 8;
}

public sealed record NotebookRecordEnvelope(
    string Kind,
    string Version,
    string RecordId,
    DateTimeOffset CreatedAt,
    string InvestigationReference,
    string ScenarioId,
    string CaseReference,
    DateOnly From,
    DateOnly To,
    string Pattern,
    decimal Confidence,
    string ReasonCode,
    IReadOnlyList<string> EvidenceIds,
    InvestigationBoundaries Boundaries,
    string RecommendedFollowUp);

public sealed record Demo4MemoryReference(
    string MemoryId,
    DateTimeOffset UpdatedAt,
    NotebookRecordEnvelope Record);

public sealed record Demo4MemorySearchResult(
    bool Consulted,
    IReadOnlyList<Demo4MemoryReference> Records,
    int ReturnedCount,
    DateTimeOffset? FreshestUpdatedAt);

public static partial class NotebookRecordCodec
{
    public const string Kind = "public-sector.demo4.cross-government-control-reference";
    public const string Version = "2.0.0-preview";
    public const int MaximumSerializedCharacters = 4_096;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static NotebookRecordEnvelope Create(
        ValidatedInvestigationResult result,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(result);
        CasePatternAssessment assessment = result.Assessment;
        if (assessment.Outcome != InvestigationOutcome.Completed)
        {
            throw InvalidRecord("Only a completed assessment can create a Memory record.");
        }

        string recordId = CreateRecordId(
            result.InvestigationReference,
            assessment.ScenarioId,
            createdAt);
        NotebookRecordEnvelope record = new(
            Kind,
            Version,
            recordId,
            createdAt,
            result.InvestigationReference,
            assessment.ScenarioId,
            assessment.CaseReference,
            assessment.From,
            assessment.To,
            assessment.Pattern,
            assessment.Confidence,
            assessment.ReasonCode,
            Array.AsReadOnly(assessment.Evidence.Select(item => item.EvidenceId).ToArray()),
            assessment.Boundaries,
            result.RecommendedFollowUp);
        Validate(record, createdAt, Demo4MemoryContract.RequiredTtlSeconds);
        return record;
    }

    public static string Serialize(NotebookRecordEnvelope record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string json = JsonSerializer.Serialize(record, SerializerOptions);
        return json.Length <= MaximumSerializedCharacters
            ? json
            : throw InvalidRecord("The Memory record exceeds its bounded envelope size.");
    }

    public static NotebookRecordEnvelope DeserializeAndValidate(
        string json,
        DateTimeOffset now,
        int ttlSeconds)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumSerializedCharacters)
        {
            throw InvalidRecord();
        }

        NotebookRecordEnvelope record;
        try
        {
            record = JsonSerializer.Deserialize<NotebookRecordEnvelope>(json, SerializerOptions)
                ?? throw InvalidRecord();
        }
        catch (JsonException exception)
        {
            throw new UnsafeMemoryReferenceException(
                "memory_record.invalid_envelope",
                "A Memory item was not an application-owned record.",
                exception);
        }

        Validate(record, now, ttlSeconds);
        if (!Encoding.UTF8.GetBytes(Serialize(record)).SequenceEqual(Encoding.UTF8.GetBytes(json)))
        {
            throw InvalidRecord();
        }

        return record;
    }

    public static void Validate(
        NotebookRecordEnvelope record,
        DateTimeOffset now,
        int ttlSeconds)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!string.Equals(record.Kind, Kind, StringComparison.Ordinal) ||
            !string.Equals(record.Version, Version, StringComparison.Ordinal) ||
            !string.Equals(
                record.RecordId,
                CreateRecordId(
                    record.InvestigationReference,
                    record.ScenarioId,
                    record.CreatedAt),
                StringComparison.Ordinal) ||
            record.CreatedAt < now.AddSeconds(-ttlSeconds) ||
            record.CreatedAt > now.AddMinutes(1) ||
            !SafeIdPattern().IsMatch(record.InvestigationReference) ||
            !ScenarioIdPattern().IsMatch(record.ScenarioId) ||
            !CaseReferencePattern().IsMatch(record.CaseReference) ||
            record.To < record.From ||
            record.To.DayNumber - record.From.DayNumber + 1 is < 1 or > 366 ||
            !IsBoundedText(record.Pattern, 256) ||
            record.Confidence is < 0 or > 1 ||
            !SafeIdPattern().IsMatch(record.ReasonCode) ||
            record.EvidenceIds is null or { Count: 0 or > 16 } ||
            record.EvidenceIds.Distinct(StringComparer.Ordinal).Count() != record.EvidenceIds.Count ||
            record.EvidenceIds.Any(id => !SafeIdPattern().IsMatch(id)) ||
            record.Boundaries is null ||
            !IsBoundedText(record.Boundaries.EvidenceBoundary, 512) ||
            !IsBoundedText(record.Boundaries.ContextBoundary, 512) ||
            !IsBoundedText(record.Boundaries.ReferenceBoundary, 512) ||
            !IsBoundedText(record.Boundaries.GuidanceBoundary, 512) ||
            !IsBoundedText(record.RecommendedFollowUp, 800))
        {
            throw InvalidRecord();
        }
    }

    private static string CreateRecordId(
        string investigationReference,
        string scenarioId,
        DateTimeOffset createdAt)
    {
        string identity = string.Join(
            '\n',
            Kind,
            Version,
            createdAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            investigationReference,
            scenarioId);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"d4r-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private static bool IsBoundedText(string? value, int maximumCharacters) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumCharacters &&
        !value.Any(character =>
            char.IsControl(character) &&
            character is not '\r' and not '\n' and not '\t');

    private static UnsafeMemoryReferenceException InvalidRecord(
        string detail = "A Memory item was not an application-owned record.") =>
        new("memory_record.invalid_envelope", detail);

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex SafeIdPattern();

    [GeneratedRegex(@"\ACASE-[A-Z0-9-]{3,80}\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex ScenarioIdPattern();

    [GeneratedRegex(@"\ACG-[0-9]{4,12}\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex CaseReferencePattern();
}

public sealed class UnsafeMemoryReferenceException : InvalidOperationException
{
    public UnsafeMemoryReferenceException(string code, string safeDetail)
        : base(safeDetail)
    {
        Code = code;
        SafeDetail = safeDetail;
    }

    public UnsafeMemoryReferenceException(
        string code,
        string safeDetail,
        Exception innerException)
        : base(safeDetail, innerException)
    {
        Code = code;
        SafeDetail = safeDetail;
    }

    public string Code { get; }

    public string SafeDetail { get; }
}
