using System.Text.Json.Serialization;

namespace PublicSectorAgentDemos.Demo1.Web;

public static class QualityCriteria
{
    public const string Correctness = "correctness";
    public const string RelevanceCompleteness = "relevance-completeness";
    public const string EvidenceSupport = "evidence-support";
    public const string UncertaintyHandling = "uncertainty-handling";
    public const string NextActionUsefulness = "next-action-usefulness";

    public static readonly string[] All =
    [
        Correctness,
        RelevanceCompleteness,
        EvidenceSupport,
        UncertaintyHandling,
        NextActionUsefulness
    ];

    public static string Label(string id) => id switch
    {
        Correctness => "Correctness against references",
        RelevanceCompleteness => "Relevance and completeness",
        EvidenceSupport => "Evidence support",
        UncertaintyHandling => "Uncertainty handling",
        NextActionUsefulness => "Next-action usefulness",
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };
}

public sealed record AnswerAssessment(int? Score, string Explanation, string? Quote);

public sealed record CriterionAssessment(
    string Id,
    string Label,
    AnswerAssessment Foundation,
    AnswerAssessment Ground,
    int? Difference);

public sealed record QualityAssessment(
    string Summary,
    IReadOnlyList<CriterionAssessment> Criteria,
    string RubricVersion,
    string Model,
    string ReferenceBasis,
    IReadOnlyList<string> Limitations,
    long ElapsedMs,
    bool Cached = false);

internal sealed record JudgeAnswerAssessment(
    [property: JsonPropertyName("score")] int? Score,
    [property: JsonPropertyName("explanation")] string Explanation,
    [property: JsonPropertyName("quote")] string? Quote);

internal sealed record JudgeCriterionAssessment(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("foundation")] JudgeAnswerAssessment Foundation,
    [property: JsonPropertyName("ground")] JudgeAnswerAssessment Ground);

internal sealed record JudgeReferenceCheck(
    [property: JsonPropertyName("reference_index")] int ReferenceIndex,
    [property: JsonPropertyName("foundation")] string Foundation,
    [property: JsonPropertyName("ground")] string Ground);

internal sealed record JudgeAssessment(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("criteria")] IReadOnlyList<JudgeCriterionAssessment> Criteria,
    [property: JsonPropertyName("reference_checks")] IReadOnlyList<JudgeReferenceCheck> ReferenceChecks);

public static class QualityAssessmentValidator
{
    public const int MaxSummaryCharacters = 800;
    public const int MaxExplanationCharacters = 1_200;
    public const int MaxQuoteCharacters = 500;

    internal static QualityAssessment ValidateAndMap(
        JudgeAssessment result,
        ComparisonSnapshot snapshot,
        AssessmentReference reference,
        string model,
        long elapsedMs)
    {
        if (string.IsNullOrWhiteSpace(result.Summary) || result.Summary.Length > MaxSummaryCharacters ||
            result.Criteria is null || result.Criteria.Count != QualityCriteria.All.Length)
        {
            throw new InvalidDataException("The assessment result has an invalid summary or criterion count.");
        }

        ValidateReferenceChecks(result.ReferenceChecks, reference.ReviewRubric.Count);
        Dictionary<string, JudgeCriterionAssessment> byId = new(StringComparer.Ordinal);
        foreach (JudgeCriterionAssessment criterion in result.Criteria)
        {
            if (criterion is null || criterion.Foundation is null || criterion.Ground is null ||
                !QualityCriteria.All.Contains(criterion.Id, StringComparer.Ordinal) || !byId.TryAdd(criterion.Id, criterion))
            {
                throw new InvalidDataException("The assessment result has an unknown or duplicate criterion.");
            }
        }

        List<CriterionAssessment> mapped = [];
        foreach (string id in QualityCriteria.All)
        {
            JudgeCriterionAssessment criterion = byId[id];
            JudgeAnswerAssessment foundation = criterion.Foundation;
            JudgeAnswerAssessment ground = criterion.Ground;
            if (id == QualityCriteria.Correctness && reference.ScenarioId is null &&
                (foundation.Score is not null || ground.Score is not null))
            {
                throw new InvalidDataException("Correctness cannot be scored without a reviewed fixture outcome.");
            }
            foundation = NormalizeAnswer(foundation, snapshot.Foundation.OriginalText);
            ground = NormalizeAnswer(ground, snapshot.Ground.OriginalText);
            foundation = ApplyReferenceConstraints(id, foundation, result.ReferenceChecks.Select(item => item.Foundation));
            ground = ApplyReferenceConstraints(id, ground, result.ReferenceChecks.Select(item => item.Ground));
            mapped.Add(new(
                id,
                QualityCriteria.Label(id),
                new(foundation.Score, foundation.Explanation, foundation.Quote),
                new(ground.Score, ground.Explanation, ground.Quote),
                foundation.Score.HasValue && ground.Score.HasValue ? ground.Score - foundation.Score : null));
        }

        return new(result.Summary, mapped, QualityAssessmentRubric.Version, model,
            reference.Basis, reference.Limitations, elapsedMs);
    }

    private static void ValidateReferenceChecks(
        IReadOnlyList<JudgeReferenceCheck>? checks,
        int expectedCount)
    {
        if (checks is null || checks.Count != expectedCount)
        {
            throw new InvalidDataException("The assessment result has an invalid reference-check count.");
        }

        string[] allowed = ["aligned", "contradicted", "missing"];
        for (int index = 0; index < checks.Count; index++)
        {
            JudgeReferenceCheck check = checks[index];
            if (check is null || check.ReferenceIndex != index ||
                !allowed.Contains(check.Foundation, StringComparer.Ordinal) ||
                !allowed.Contains(check.Ground, StringComparer.Ordinal))
            {
                throw new InvalidDataException("The assessment result contains an invalid reference check.");
            }
        }
    }

    private static JudgeAnswerAssessment ApplyReferenceConstraints(
        string criterionId,
        JudgeAnswerAssessment answer,
        IEnumerable<string> findings)
    {
        if (!answer.Score.HasValue)
        {
            return answer;
        }

        string[] values = findings.ToArray();
        int contradictions = values.Count(value => value == "contradicted");
        int missing = values.Count(value => value == "missing");
        int maximum = 5;
        if (criterionId == QualityCriteria.Correctness)
        {
            if (contradictions >= 2) maximum = 2;
            else if (contradictions == 1) maximum = 3;
            else if (missing > 0) maximum = 4;
        }
        else if (criterionId is QualityCriteria.UncertaintyHandling or QualityCriteria.NextActionUsefulness)
        {
            if (contradictions > 0) maximum = 3;
        }
        else if (criterionId == QualityCriteria.RelevanceCompleteness && missing > 0)
        {
            maximum = 4;
        }

        if (answer.Score.Value <= maximum)
        {
            return answer;
        }

        string reason = contradictions > 0
            ? $" The score is capped because {contradictions} reviewed requirement(s) are contradicted."
            : $" The score is capped because {missing} reviewed requirement(s) are missing.";
        string explanation = answer.Explanation + reason;
        if (explanation.Length > MaxExplanationCharacters)
        {
            explanation = explanation[..MaxExplanationCharacters];
        }
        return answer with { Score = maximum, Explanation = explanation };
    }

    private static JudgeAnswerAssessment NormalizeAnswer(JudgeAnswerAssessment answer, string original)
    {
        if (answer.Score is < 1 or > 5 ||
            string.IsNullOrWhiteSpace(answer.Explanation) || answer.Explanation.Length > MaxExplanationCharacters ||
            answer.Quote?.Length > MaxQuoteCharacters)
        {
            throw new InvalidDataException("The assessment result contains an invalid score or text value.");
        }

        if (answer.Score is null)
        {
            if (!string.IsNullOrEmpty(answer.Quote))
            {
                throw new InvalidDataException("A criterion that was not assessed cannot contain a quotation.");
            }
            return answer;
        }

        if (!string.IsNullOrWhiteSpace(answer.Quote) &&
            original.Contains(answer.Quote, StringComparison.Ordinal))
        {
            return answer;
        }

        string fallbackQuote = original.Trim();
        if (fallbackQuote.Length > 240)
        {
            int boundary = fallbackQuote.LastIndexOf(' ', 240);
            fallbackQuote = fallbackQuote[..(boundary > 0 ? boundary : 240)];
        }
        if (fallbackQuote.Length == 0)
        {
            throw new InvalidDataException("The assessed answer is empty.");
        }

        return answer with { Quote = fallbackQuote };
    }
}
