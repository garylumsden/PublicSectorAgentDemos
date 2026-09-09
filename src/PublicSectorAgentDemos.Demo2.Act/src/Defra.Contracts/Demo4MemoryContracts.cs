using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Defra.Contracts.V1;

public static class Demo4MemoryContract
{
    public const string StoreName = "demo4-investigation-memory";
    public const string Scope = "demo4-shared-investigation-notebook";
    public const string SearchToolName = "search_investigation_memory";
    public const int RequiredTtlSeconds = 604_800;
    public const int MaximumSearchRecords = 8;
}

public sealed record NotebookScenarioReference(
    string CatchmentId,
    string Parameter,
    DateOnly From,
    DateOnly To);

public sealed record NotebookAssessmentReference(
    string ComplianceStatus,
    int MeasurementCount,
    decimal Minimum,
    decimal Maximum,
    decimal Mean,
    string Trend,
    string? StatisticName,
    decimal? StatisticValue,
    string? StatisticUnit,
    string? ThresholdRuleId,
    decimal? ThresholdValue,
    string? ThresholdUnit,
    string? ComparisonOperator,
    string Rationale,
    string InterpretationBoundary,
    string DecisionBoundary);

public sealed record NotebookProvenanceReference(
    string MeasurementSourceId,
    string ContextSourceId,
    string ThresholdCatalogueId,
    string ThresholdCatalogueVersion,
    IReadOnlyList<string> EvidenceIds);

public sealed record NotebookRecordDraft(
    NotebookScenarioReference ScenarioReference,
    NotebookAssessmentReference AssessmentReference,
    NotebookProvenanceReference ProvenanceReference,
    string RecommendedFollowUp);

public sealed record NotebookRecordEnvelope(
    string Kind,
    string Version,
    string RecordId,
    DateTimeOffset CreatedAt,
    string InvestigationReference,
    NotebookScenarioReference ScenarioReference,
    NotebookAssessmentReference AssessmentReference,
    NotebookProvenanceReference ProvenanceReference,
    string RecommendedFollowUp,
    string ReferenceSummary);

public sealed record Demo4MemoryReference(
    string MemoryId,
    DateTimeOffset UpdatedAt,
    string RecordId,
    DateTimeOffset CreatedAt,
    string InvestigationReference,
    NotebookScenarioReference ScenarioReference,
    NotebookAssessmentReference AssessmentReference,
    NotebookProvenanceReference ProvenanceReference,
    string RecommendedFollowUp,
    string ReferenceSummary);

public sealed record Demo4MemorySearchResult(
    bool Consulted,
    IReadOnlyList<Demo4MemoryReference> Records,
    int ReturnedCount,
    DateTimeOffset? FreshestUpdatedAt);

public static partial class NotebookRecordCodec
{
    public const string Kind = "defra.demo4.investigation-reference";
    public const string Version = "2.0.0";
    public const int MaximumSerializedCharacters = 4_096;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false
    };

    public static NotebookRecordEnvelope Create(
        string investigationReference,
        NotebookRecordDraft draft,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(investigationReference);
        ArgumentNullException.ThrowIfNull(draft);
        NotebookRecordEnvelope record = new(
            Kind,
            Version,
            CreateRecordId(investigationReference, draft.ScenarioReference, createdAt),
            createdAt,
            investigationReference,
            draft.ScenarioReference,
            draft.AssessmentReference,
            draft.ProvenanceReference,
            draft.RecommendedFollowUp,
            CreateSummary(
                investigationReference,
                draft.ScenarioReference,
                draft.AssessmentReference));
        Validate(record, createdAt, Demo4MemoryContract.RequiredTtlSeconds);
        return record;
    }

    public static string Serialize(NotebookRecordEnvelope record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string json = JsonSerializer.Serialize(record, SerializerOptions);
        if (json.Length > MaximumSerializedCharacters)
        {
            throw new InvalidOperationException("Notebook record exceeds its bounded envelope size.");
        }

        return json;
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
                "A shared-memory item was not an application-owned notebook record.",
                exception);
        }

        Validate(record, now, ttlSeconds);
        string canonicalJson = Serialize(record);
        if (!Encoding.UTF8.GetBytes(canonicalJson)
            .SequenceEqual(Encoding.UTF8.GetBytes(json)))
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
        NotebookScenarioReference scenario = record.ScenarioReference;
        NotebookAssessmentReference assessment = record.AssessmentReference;
        NotebookProvenanceReference provenance = record.ProvenanceReference;
        if (!string.Equals(record.Kind, Kind, StringComparison.Ordinal) ||
            !string.Equals(record.Version, Version, StringComparison.Ordinal) ||
            scenario is null ||
            assessment is null ||
            provenance is null ||
            !string.Equals(
                record.RecordId,
                CreateRecordId(record.InvestigationReference, scenario, record.CreatedAt),
                StringComparison.Ordinal) ||
            record.CreatedAt < now.AddSeconds(-ttlSeconds) ||
            record.CreatedAt > now.AddMinutes(1) ||
            !InvestigationReferencePattern().IsMatch(record.InvestigationReference) ||
            !CatchmentPattern().IsMatch(scenario.CatchmentId) ||
            scenario.Parameter is not ("nitrate" or "phosphorus" or "pH" or "dissolvedOxygen") ||
            scenario.To < scenario.From ||
            scenario.To.DayNumber - scenario.From.DayNumber + 1 is < 1 or > 366 ||
            assessment.ComplianceStatus is not ("compliant" or "nonCompliant" or "unableToDetermine" or "noValidThreshold") ||
            assessment.MeasurementCount is < 1 or > 366 ||
            assessment.Minimum > assessment.Maximum ||
            assessment.Mean < assessment.Minimum ||
            assessment.Mean > assessment.Maximum ||
            !IsBoundedText(assessment.Trend, 64) ||
            !IsOptionalBoundedText(assessment.StatisticName, 128) ||
            !IsOptionalBoundedText(assessment.StatisticUnit, 64) ||
            !IsOptionalBoundedText(assessment.ThresholdRuleId, 128) ||
            !IsOptionalBoundedText(assessment.ThresholdUnit, 64) ||
            !IsOptionalBoundedText(assessment.ComparisonOperator, 32) ||
            !IsBoundedText(assessment.Rationale, 768) ||
            !IsBoundedText(assessment.InterpretationBoundary, 768) ||
            !IsBoundedText(assessment.DecisionBoundary, 768) ||
            !string.Equals(provenance.MeasurementSourceId, "wqa-measurements", StringComparison.Ordinal) ||
            !string.Equals(provenance.ContextSourceId, "waterbody-context", StringComparison.Ordinal) ||
            !string.Equals(provenance.ThresholdCatalogueId, "demo4-water-threshold-catalogue", StringComparison.Ordinal) ||
            !string.Equals(provenance.ThresholdCatalogueVersion, "0.1.1", StringComparison.Ordinal) ||
            provenance.EvidenceIds is null or { Count: 0 or > 8 } ||
            provenance.EvidenceIds.Distinct(StringComparer.Ordinal).Count() != provenance.EvidenceIds.Count ||
            provenance.EvidenceIds.Any(id => !EvidenceIdPattern().IsMatch(id)) ||
            !IsBoundedText(record.RecommendedFollowUp, 800) ||
            !string.Equals(
                record.ReferenceSummary,
                CreateSummary(record.InvestigationReference, scenario, assessment),
                StringComparison.Ordinal))
        {
            throw InvalidRecord();
        }
    }

    private static string CreateRecordId(
        string investigationReference,
        NotebookScenarioReference scenario,
        DateTimeOffset createdAt)
    {
        string identity = string.Join(
            '\n',
            Kind,
            Version,
            createdAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            investigationReference,
            scenario.CatchmentId,
            scenario.Parameter,
            scenario.From.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            scenario.To.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return $"d4r-{Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant()}";
    }

    private static string CreateSummary(
        string investigationReference,
        NotebookScenarioReference scenario,
        NotebookAssessmentReference assessment) =>
        $"Prior completed investigation {investigationReference}: {scenario.CatchmentId} {scenario.Parameter} from {scenario.From:yyyy-MM-dd} to {scenario.To:yyyy-MM-dd}; status {assessment.ComplianceStatus}; {assessment.MeasurementCount} samples; mean {assessment.Mean}; trend {assessment.Trend}; {assessment.Rationale}";

    private static bool IsBoundedText(string? value, int maximumCharacters) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumCharacters &&
        !value.Any(character =>
            char.IsControl(character) &&
            character is not '\r' and not '\n' and not '\t');

    private static bool IsOptionalBoundedText(string? value, int maximumCharacters) =>
        value is null || IsBoundedText(value, maximumCharacters);

    private static UnsafeMemoryReferenceException InvalidRecord() =>
        new(
            "memory_record.invalid_envelope",
            "A shared-memory item was not an application-owned notebook record.");

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{1,128}\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex InvestigationReferencePattern();

    [GeneratedRegex(@"\AWQA-[A-Z0-9]{3,12}\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex CatchmentPattern();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex EvidenceIdPattern();
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