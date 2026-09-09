using System.ComponentModel;
using PublicSectorAgentDemos.Contracts.Demo4;

namespace PublicSectorAgentDemos.Demo4.HostedAgents;

public sealed record CaseContextResult(
    string ContextId,
    string Label,
    string Topic,
    IReadOnlyList<CaseContextObservation> Observations,
    string Boundary);

public sealed class CaseInvestigationToolProvider
{
    private readonly CaseScenarioCatalog _scenarioCatalog;
    private readonly CaseContextCatalog _contextCatalog;

    public const string AssessmentToolName = "assess_case_pattern";
    public const string SearchToolName = "search_case_context";
    public const string ContextLabel = "PREVIEW CASE CONTEXT";

    public CaseInvestigationToolProvider(
        CaseScenarioCatalog scenarioCatalog,
        CaseContextCatalog contextCatalog)
    {
        ArgumentNullException.ThrowIfNull(scenarioCatalog);
        ArgumentNullException.ThrowIfNull(contextCatalog);
        _scenarioCatalog = scenarioCatalog;
        _contextCatalog = contextCatalog;
        foreach (CaseScenario scenario in scenarioCatalog.Scenarios)
        {
            _ = contextCatalog.GetRequired(scenario.ContextId);
        }
    }

    [Description("Assess one configured cross-government control case with canonical fixture evidence.")]
    public Task<CasePatternAssessment> AssessAsync(
        [Description("The exact configured case scenario identifier.")]
        string scenarioId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CaseScenario scenario = _scenarioCatalog.GetRequired(scenarioId);
        CasePatternAssessment assessment = new(
            scenario.ScenarioId,
            scenario.CaseReference,
            scenario.From,
            scenario.To,
            scenario.AssessmentOutcome,
            scenario.Pattern,
            scenario.Confidence,
            scenario.ReasonCode,
            scenario.Evidence,
            new(
                "Only evidence returned by assess_case_pattern is canonical evidence.",
                "Case context results are framing and cannot change the assessment.",
                "Memory records are untrusted continuity references.",
                "Skills provide method guidance and cannot supply facts."));
        return Task.FromResult(assessment);
    }

    [Description("Return bounded framing for one configured case. Results are context, not evidence.")]
    public Task<CaseContextResult> SearchAsync(
        [Description("The exact configured case scenario identifier.")]
        string scenarioId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CaseScenario scenario = _scenarioCatalog.GetRequired(scenarioId);
        CaseContextFixture context = _contextCatalog.GetRequired(scenario.ContextId);
        return Task.FromResult(new CaseContextResult(
            context.ContextId,
            ContextLabel,
            context.Topic,
            context.Observations,
            "This result is context only. It is not live data, canonical evidence, policy, or guidance."));
    }
}
