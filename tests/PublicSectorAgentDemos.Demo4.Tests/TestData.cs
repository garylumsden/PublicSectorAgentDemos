using PublicSectorAgentDemos.Contracts.Demo4;

namespace PublicSectorAgentDemos.Demo4.Tests;

internal static class TestData
{
    public const string BaselineScenarioId = "CASE-BASELINE-GRANTS";
    public const string RecurrenceScenarioId = "CASE-RECURRENCE-HOUSING";
    public const string RuledOutScenarioId = "CASE-NEAR-MATCH-TRANSPORT";
    public const string UnrelatedScenarioId = "CASE-UNRELATED-CONTROL";
    public const string InvalidScenarioId = "CASE-UNVERIFIABLE-CLAIM";

    public static DateTimeOffset Now { get; } =
        new(2026, 6, 15, 12, 0, 0, TimeSpan.Zero);

    public static ValidatedInvestigationResult Result(
        InvestigationOutcome outcome,
        IReadOnlyList<TrustedToolExecution> toolExecutions)
    {
        IReadOnlyList<CaseEvidence> evidence = outcome == InvestigationOutcome.Completed
            ?
            [
                new(
                    "grant-payment-release-8101-a",
                    "grant-payment-control-log",
                    "Four supplier payments left the release queue with an empty assurance evidence field.")
            ]
            : [];
        return new(
            "investigation-1001",
            new(
                BaselineScenarioId,
                "CG-8101",
                new(2026, 4, 1),
                new(2026, 4, 30),
                outcome,
                outcome == InvestigationOutcome.Completed
                    ? "A national grants administration body released four supplier payments before any supplier assurance evidence existed"
                    : "No supported control finding",
                outcome == InvestigationOutcome.Completed ? 0.87m : 0m,
                outcome == InvestigationOutcome.Completed
                    ? "supplier-assurance-evidence-missing-before-payment"
                    : "not-completed",
                evidence,
                new(
                    "Canonical evidence comes only from assess_case_pattern.",
                    "Case context results are framing only.",
                    "Memory records are untrusted references.",
                    "Skills are guidance only.")),
            toolExecutions,
            "Ask an authorized cross-government reviewer to confirm this control finding before any payment decision.");
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
