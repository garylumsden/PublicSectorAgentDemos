using Defra.Contracts.V1;
using Demo2.Web.Domain;
using Demo2.Web.Persistence;

namespace Defra.IntegrationTests;

public sealed class Demo2InMemoryPersistenceIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InMemoryRepository_RoundTripsCaseConversationApprovalAndInspectorDecision()
    {
        InMemoryDemo2StateStore repository = new();
        IWelfareCaseStore cases = repository;
        IConversationStore conversations = repository;
        IApprovalStore approvals = repository;
        IInspectorDecisionStore decisions = repository;
        WelfareCaseRecord caseRecord = new(
            "D2-PERSIST",
            Guid.Parse("93ed346b-74a2-4627-aa33-26b7715fd453"),
            "AREA-1001",
            Now,
            "complaint-hash",
            WelfareUrgency.High,
            WelfareRoute.PriorityInspectorReview,
            WelfareCaseStatus.PendingApproval,
            "Synthetic persistent case.",
            ["repeated-unmet-needs-high"],
            false,
            "response-1",
            "approval-1",
            3,
            Now);
        ConversationRecord conversation = new(
            caseRecord.CaseId,
            "conversation-1",
            "response-1",
            Now);
        ApprovalRequest request = new(
            "approval-1",
            new CorrelationId("correlation-d2-persist"),
            "reserveSupportPackage",
            ["caseId", "areaReference", "urgency"],
            "urgent-flood-support",
            Now,
            Now.AddMinutes(30));
        WelfareApprovalRecord approval = new(
            caseRecord.CaseId,
            caseRecord.Version,
            request,
            null,
            null,
            Now);
        InspectorDecisionRecord decision = new(
            "decision-1",
            caseRecord.CaseId,
            WelfareUrgency.Medium,
            WelfareUrgency.High,
            Guid.Parse("97e2d245-5ef5-4468-b1ae-4a892ea7ab55"),
            "support-needs-reviewed",
            Now);

        await cases.UpsertAsync(caseRecord);
        await conversations.UpsertAsync(conversation);
        await approvals.UpsertAsync(approval);
        await decisions.AddAsync(decision);

        Assert.Equal(caseRecord, await cases.GetAsync(caseRecord.CaseId));
        Assert.Equal(conversation, await conversations.GetAsync(caseRecord.CaseId));
        Assert.Equal(approval, await approvals.GetAsync(caseRecord.CaseId, request.RequestId));
        Assert.Equal(caseRecord, Assert.Single(await cases.ListAsync()));
        Assert.Equal(approval, Assert.Single(await approvals.ListForCaseAsync(caseRecord.CaseId)));
        Assert.Equal(approval, Assert.Single(await approvals.ListPendingAsync()));
        Assert.Equal(decision, Assert.Single(await decisions.ListDecisionsForCaseAsync(caseRecord.CaseId)));
    }
}
