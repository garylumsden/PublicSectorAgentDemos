using System.Text.Json;
using PublicSectorAgentDemos.Contracts.Demo4;
using PublicSectorAgentDemos.Demo4.Application;

namespace PublicSectorAgentDemos.Demo4.Tests;

public sealed class MemoryContinuityReaderTests
{
    private const string BaselineReason =
        "supplier-assurance-evidence-missing-before-payment";

    [Fact]
    public void SkippedSearchReportsTheNotebookWasNotConsulted()
    {
        MemoryReadOutcome outcome = MemoryContinuityReader.Read(
            [Assessment()],
            Current("CG-8202", BaselineReason),
            TestData.Now);

        Assert.False(outcome.Searched);
        Assert.Equal(0, outcome.ReturnedCount);
        Assert.Null(outcome.FreshestUpdatedAt);
        Assert.Empty(outcome.References);
        Assert.Equal(MemoryContinuityReader.NotConsultedStatement, outcome.ContinuityStatement);
        Assert.Contains("did not consult", outcome.ContinuityStatement, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySearchReportsNoPriorReference()
    {
        MemoryReadOutcome outcome = MemoryContinuityReader.Read(
            [Assessment(), Search(Result([]))],
            Current("CG-8202", BaselineReason),
            TestData.Now);

        Assert.True(outcome.Searched);
        Assert.Equal(0, outcome.ReturnedCount);
        Assert.Null(outcome.FreshestUpdatedAt);
        Assert.Equal(MemoryContinuityReader.NoPriorReferenceStatement, outcome.ContinuityStatement);
        Assert.Contains("no prior reference", outcome.ContinuityStatement, StringComparison.Ordinal);
    }

    [Fact]
    public void SameReasonAndDifferentCaseIsAMatchedRepeatControlFailure()
    {
        DateTimeOffset updated = TestData.Now.AddHours(-2);
        MemoryReadOutcome outcome = MemoryContinuityReader.Read(
            [
                Assessment(),
                Search(Result([Reference("memory-a", updated, "CG-8101", BaselineReason)]))
            ],
            Current("CG-8202", BaselineReason),
            TestData.Now);

        MemoryReferenceView reference = Assert.Single(outcome.References);
        Assert.Equal(MemoryMatchVerdict.MatchedRepeatControlFailure, reference.Verdict);
        Assert.Equal(1, outcome.ReturnedCount);
        Assert.Equal(updated, outcome.FreshestUpdatedAt);
        Assert.Contains("CG-8101", outcome.ContinuityStatement, StringComparison.Ordinal);
        Assert.Contains(
            "Memory changed the read of this case",
            outcome.ContinuityStatement,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SameCaseRunIsExcludedFromMemoryReferences()
    {
        MemoryReadOutcome outcome = MemoryContinuityReader.Read(
            [
                Assessment(),
                Search(Result(
                [
                    Reference("memory-a", TestData.Now.AddHours(-3), "CG-8202", BaselineReason)
                ]))
            ],
            Current("CG-8202", BaselineReason),
            TestData.Now);

        Assert.Empty(outcome.References);
        Assert.Equal(0, outcome.ReturnedCount);
        Assert.Null(outcome.FreshestUpdatedAt);
        Assert.Equal(
            MemoryContinuityReader.NoPriorReferenceStatement,
            outcome.ContinuityStatement);
    }

    [Fact]
    public void SameCaseRunIsExcludedWhenAnotherCaseMatches()
    {
        DateTimeOffset sameCaseUpdated = TestData.Now.AddHours(-1);
        DateTimeOffset priorCaseUpdated = TestData.Now.AddHours(-2);
        MemoryReadOutcome outcome = MemoryContinuityReader.Read(
            [
                Assessment(),
                Search(Result(
                [
                    Reference("memory-a", sameCaseUpdated, "CG-8202", BaselineReason),
                    Reference(
                        "memory-b",
                        priorCaseUpdated,
                        "CG-8101",
                        BaselineReason,
                        "investigation-9002")
                ]))
            ],
            Current("CG-8202", BaselineReason),
            TestData.Now);

        MemoryReferenceView reference = Assert.Single(outcome.References);
        Assert.Equal("CG-8101", reference.CaseReference);
        Assert.Equal(priorCaseUpdated, outcome.FreshestUpdatedAt);
        Assert.DoesNotContain(
            "CG-8202",
            outcome.ContinuityStatement,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentReasonIsRuledOutAndDoesNotAlterTheCurrentEvidence()
    {
        MemoryReadOutcome outcome = MemoryContinuityReader.Read(
            [
                Assessment(),
                Search(Result(
                [
                    Reference("memory-a", TestData.Now.AddHours(-4), "CG-8101", BaselineReason, "investigation-9101"),
                    Reference("memory-b", TestData.Now.AddHours(-5), "CG-8202", BaselineReason, "investigation-9102")
                ]))
            ],
            Current("CG-8303", "payment-release-control-not-engaged"),
            TestData.Now);

        Assert.Equal(2, outcome.ReturnedCount);
        Assert.All(
            outcome.References,
            reference => Assert.Equal(
                MemoryMatchVerdict.RuledOutDifferentControl,
                reference.Verdict));
        Assert.Contains(
            "2 ruled-out records used a different reason code and did not alter the current evidence.",
            outcome.ContinuityStatement,
            StringComparison.Ordinal);
        Assert.DoesNotContain("CG-8101", outcome.ContinuityStatement, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchedAndRuledOutRecordsAreBothNarrated()
    {
        MemoryReadOutcome outcome = MemoryContinuityReader.Read(
            [
                Assessment(),
                Search(Result(
                [
                    Reference("memory-a", TestData.Now.AddHours(-4), "CG-8101", BaselineReason, "investigation-9201"),
                    Reference(
                        "memory-b",
                        TestData.Now.AddHours(-5),
                        "CG-8404",
                        "records-retention-schedule-overdue",
                        "investigation-9202")
                ]))
            ],
            Current("CG-8202", BaselineReason),
            TestData.Now);

        Assert.Contains("CG-8101", outcome.ContinuityStatement, StringComparison.Ordinal);
        Assert.Contains(
            "1 ruled-out record used a different reason code and did not alter the current evidence.",
            outcome.ContinuityStatement,
            StringComparison.Ordinal);
        Assert.Equal(
            [MemoryMatchVerdict.MatchedRepeatControlFailure, MemoryMatchVerdict.RuledOutDifferentControl],
            outcome.References.Select(reference => reference.Verdict));
    }

    [Fact]
    public void ReferencesExposeEveryDisplayedRecordField()
    {
        DateTimeOffset updated = TestData.Now.AddHours(-6);
        MemoryReadOutcome outcome = MemoryContinuityReader.Read(
            [
                Assessment(),
                Search(Result([Reference("memory-a", updated, "CG-8101", BaselineReason)]))
            ],
            Current("CG-8202", BaselineReason),
            TestData.Now);

        MemoryReferenceView reference = Assert.Single(outcome.References);
        NotebookRecordEnvelope record = PriorRecord("CG-8101", BaselineReason);
        Assert.Equal("memory-a", reference.MemoryId);
        Assert.Equal(record.RecordId, reference.RecordId);
        Assert.Equal(record.InvestigationReference, reference.InvestigationReference);
        Assert.Equal("CG-8101", reference.CaseReference);
        Assert.Equal(record.Pattern, reference.Pattern);
        Assert.Equal(record.From, reference.From);
        Assert.Equal(record.To, reference.To);
        Assert.Equal(BaselineReason, reference.ReasonCode);
        Assert.Equal(record.Confidence, reference.Confidence);
        Assert.Equal(record.EvidenceIds, reference.EvidenceIds);
        Assert.Equal(record.RecommendedFollowUp, reference.RecommendedFollowUp);
        Assert.Equal(record.CreatedAt, reference.CreatedAt);
        Assert.Equal(updated, reference.UpdatedAt);
    }

    [Fact]
    public void TwoMemorySearchesAreRejected()
    {
        TrustedToolExecution search = Search(Result([]));

        UnsafeMemoryReferenceException exception =
            Assert.Throws<UnsafeMemoryReferenceException>(() =>
                MemoryContinuityReader.Read(
                    [Assessment(), search, search with { CallId = "call-memory-2" }],
                    Current("CG-8202", BaselineReason),
                    TestData.Now));

        Assert.Equal("memory_search.duplicate", exception.Code);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"consulted":true,"records":[],"returnedCount":0,"extra":1}""")]
    [InlineData("""{"consulted":false,"records":[],"returnedCount":0}""")]
    [InlineData("""{"consulted":true,"records":[],"returnedCount":3}""")]
    public void UnsafeSearchResultsAreRejected(string json)
    {
        Assert.Throws<UnsafeMemoryReferenceException>(() =>
            MemoryContinuityReader.Read(
                [Assessment(), Search(json)],
                Current("CG-8202", BaselineReason),
                TestData.Now));
    }

    [Fact]
    public void FailedSearchExecutionIsRejected()
    {
        Assert.Throws<UnsafeMemoryReferenceException>(() =>
            MemoryContinuityReader.Read(
                [
                    Assessment(),
                    new("call-memory-1", Demo4MemoryContract.SearchToolName, false, null)
                ],
                Current("CG-8202", BaselineReason),
                TestData.Now));
    }

    [Fact]
    public void ReportedFreshestTimestampMustMatchTheReturnedRecords()
    {
        Demo4MemorySearchResult result = Result(
            [Reference("memory-a", TestData.Now.AddHours(-2), "CG-8101", BaselineReason)]);
        string json = Serialize(result with
        {
            FreshestUpdatedAt = TestData.Now.AddHours(-1)
        });

        Assert.Throws<UnsafeMemoryReferenceException>(() =>
            MemoryContinuityReader.Read(
                [Assessment(), Search(json)],
                Current("CG-8202", BaselineReason),
                TestData.Now));
    }

    [Fact]
    public void DuplicateMemoryIdentifiersAreRejected()
    {
        Demo4MemoryReference reference = Reference(
            "memory-a",
            TestData.Now.AddHours(-2),
            "CG-8101",
            BaselineReason);

        Assert.Throws<UnsafeMemoryReferenceException>(() =>
            MemoryContinuityReader.Read(
                [Assessment(), Search(Result([reference, reference]))],
                Current("CG-8202", BaselineReason),
                TestData.Now));
    }

    [Fact]
    public void MoreRecordsThanTheReviewedLimitAreRejected()
    {
        Demo4MemoryReference[] references = Enumerable
            .Range(0, Demo4MemoryContract.MaximumSearchRecords + 1)
            .Select(index => Reference(
                $"memory-{index}",
                TestData.Now.AddHours(-index - 1),
                "CG-8101",
                BaselineReason,
                $"investigation-{index:D4}"))
            .ToArray();

        Assert.Throws<UnsafeMemoryReferenceException>(() =>
            MemoryContinuityReader.Read(
                [Assessment(), Search(Result(references))],
                Current("CG-8202", BaselineReason),
                TestData.Now));
    }

    [Fact]
    public void RecordsOutsideTheRetentionWindowAreRejected()
    {
        Demo4MemoryReference expired = Reference(
            "memory-a",
            TestData.Now.AddSeconds(-Demo4MemoryContract.RequiredTtlSeconds - 60),
            "CG-8101",
            BaselineReason);

        Assert.Throws<UnsafeMemoryReferenceException>(() =>
            MemoryContinuityReader.Read(
                [Assessment(), Search(Result([expired]))],
                Current("CG-8202", BaselineReason),
                TestData.Now));
    }

    [Fact]
    public void AnUnsafeMemoryIdentifierIsRejected()
    {
        Demo4MemoryReference unsafeIdentifier = Reference(
            "memory a/../b",
            TestData.Now.AddHours(-2),
            "CG-8101",
            BaselineReason);

        Assert.Throws<UnsafeMemoryReferenceException>(() =>
            MemoryContinuityReader.Read(
                [Assessment(), Search(Result([unsafeIdentifier]))],
                Current("CG-8202", BaselineReason),
                TestData.Now));
    }

    [Fact]
    public void ATamperedRecordEnvelopeIsRejected()
    {
        NotebookRecordEnvelope tampered = PriorRecord("CG-8101", BaselineReason) with
        {
            InvestigationReference = "investigation-tampered"
        };

        Assert.Throws<UnsafeMemoryReferenceException>(() =>
            MemoryContinuityReader.Read(
                [
                    Assessment(),
                    Search(Result(
                    [
                        new("memory-a", TestData.Now.AddHours(-2), tampered)
                    ]))
                ],
                Current("CG-8202", BaselineReason),
                TestData.Now));
    }

    [Fact]
    public void TheStatementIsAppendedToTheRecommendedFollowUp()
    {
        MemoryReadOutcome outcome = MemoryContinuityReader.Read(
            [
                Assessment(),
                Search(Result([Reference("memory-a", TestData.Now.AddHours(-2), "CG-8101", BaselineReason)]))
            ],
            Current("CG-8202", BaselineReason),
            TestData.Now);

        string followUp = outcome.ApplyTo("Ask an authorized reviewer to confirm this control finding.");

        Assert.StartsWith(
            "Ask an authorized reviewer to confirm this control finding.",
            followUp,
            StringComparison.Ordinal);
        Assert.EndsWith(outcome.ContinuityStatement, followUp, StringComparison.Ordinal);
        Assert.True(followUp.Length <= 800);
    }

    [Fact]
    public void TheLongestStatementStaysInsideTheRecordBound()
    {
        Demo4MemoryReference[] references = Enumerable
            .Range(0, Demo4MemoryContract.MaximumSearchRecords)
            .Select(index => Reference(
                $"memory-{index}",
                TestData.Now.AddHours(-index - 1),
                $"CG-{810000000000 + index}",
                BaselineReason,
                $"investigation-{index:D4}"))
            .ToArray();

        MemoryReadOutcome outcome = MemoryContinuityReader.Read(
            [Assessment(), Search(Result(references))],
            Current("CG-8202", BaselineReason),
            TestData.Now);
        string followUp = outcome.ApplyTo(
            "Ask an authorized cross-government reviewer to confirm this control finding before any payment, escalation, or enforcement decision.");

        Assert.Equal(
            Demo4MemoryContract.MaximumSearchRecords,
            outcome.References.Count);
        Assert.True(followUp.Length <= 800, $"The follow-up used {followUp.Length} characters.");
    }

    private static CasePatternAssessment Current(string caseReference, string reasonCode) =>
        TestData.Result(InvestigationOutcome.Completed, []).Assessment with
        {
            CaseReference = caseReference,
            ReasonCode = reasonCode
        };

    private static TrustedToolExecution Assessment() => new(
        "call-assessment-1",
        "assess_case_pattern",
        true,
        "{}");

    private static TrustedToolExecution Search(Demo4MemorySearchResult result) =>
        Search(Serialize(result));

    private static TrustedToolExecution Search(string json) => new(
        "call-memory-1",
        Demo4MemoryContract.SearchToolName,
        true,
        json);

    private static Demo4MemorySearchResult Result(
        IReadOnlyList<Demo4MemoryReference> records) => new(
        true,
        records,
        records.Count,
        records.Count == 0
            ? null
            : records.Max(record => record.UpdatedAt));

    private static Demo4MemoryReference Reference(
        string memoryId,
        DateTimeOffset updatedAt,
        string caseReference,
        string reasonCode,
        string investigationReference = "investigation-9001") => new(
        memoryId,
        updatedAt,
        PriorRecord(caseReference, reasonCode, investigationReference));

    private static NotebookRecordEnvelope PriorRecord(
        string caseReference,
        string reasonCode,
        string investigationReference = "investigation-9001")
    {
        ValidatedInvestigationResult seed = TestData.Result(
            InvestigationOutcome.Completed,
            []);
        return NotebookRecordCodec.Create(
            seed with
            {
                InvestigationReference = investigationReference,
                Assessment = seed.Assessment with
                {
                    CaseReference = caseReference,
                    ReasonCode = reasonCode
                }
            },
            TestData.Now.AddDays(-1));
    }

    private static string Serialize(Demo4MemorySearchResult result) =>
        JsonSerializer.Serialize(
            result,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
