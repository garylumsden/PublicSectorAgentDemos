namespace PublicSectorAgentDemos.Demo1.FoundationAndGround;

public static class GroundedResponseGate
{
    public const string FailureMessage =
        "Grounding unavailable. No evidence-based assessment can be provided.";
    public const string UnsupportedRefusalMessage =
        "Refusal: This request is outside the Managing Public Money assessment domain.";

    public static GroundedResponseDecision Evaluate(GroundingAttempt attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        if (attempt.IsExplicitRefusal)
        {
            bool validRefusal = string.Equals(
                    attempt.Answer,
                    UnsupportedRefusalMessage,
                    StringComparison.Ordinal) &&
                attempt.Citations.Count == 0;
            return validRefusal
                ? new(false, UnsupportedRefusalMessage, true)
                : new(false, FailureMessage);
        }

        bool citationsAreValid = attempt.Citations.Count > 0 &&
            attempt.Citations.All(citation =>
                string.Equals(citation.SourceId, Demo1AgentCatalog.ApprovedSourceId, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(citation.Marker));
        if (!attempt.RetrievalSucceeded ||
            !attempt.HasRelevantPassage ||
            string.IsNullOrWhiteSpace(attempt.Answer) ||
            !citationsAreValid)
        {
            return new(false, FailureMessage);
        }

        return new(true, attempt.Answer);
    }

    public static GroundedResponseDecision EvaluateLiveResponse(
        bool retrievalSucceeded,
        FoundryResponseCitationResult response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (response.IsExactExemptMessage &&
            response.Text.Equals(UnsupportedRefusalMessage, StringComparison.Ordinal))
        {
            return new(false, UnsupportedRefusalMessage, true);
        }

        if (response.IsExactExemptMessage &&
            response.Text.Equals(FailureMessage, StringComparison.Ordinal))
        {
            return new(false, FailureMessage);
        }

        if (!retrievalSucceeded ||
            response.Citations.Count == 0 ||
            response.MaterialUnits.Count == 0 ||
            !response.AllCitationsAreValid ||
            !response.AllMaterialUnitsHaveValidCitations ||
            !response.HasVerifiedQuotations ||
            !response.HasApplication)
        {
            return new(false, FailureMessage);
        }

        return new(true, response.Text);
    }
}

public sealed record GroundingAttempt(
    bool RetrievalSucceeded,
    bool HasRelevantPassage,
    string? Answer,
    IReadOnlyList<GroundingCitation> Citations,
    bool IsExplicitRefusal = false);

public sealed record GroundingCitation(string SourceId, string Marker);

public sealed record GroundedResponseDecision(
    bool CanAnswer,
    string Answer,
    bool IsExplicitRefusal = false);
