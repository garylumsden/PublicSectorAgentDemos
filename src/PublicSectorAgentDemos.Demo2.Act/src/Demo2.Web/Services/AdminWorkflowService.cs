using Defra.AgentCore;
using Defra.Contracts.FloodSupport;
using Defra.Contracts.V1;
using Defra.Policy;
using Demo2.Web.Configuration;
using Demo2.Web.Domain;
using Demo2.Web.Observability;
using Demo2.Web.Persistence;
using Microsoft.Extensions.Options;

namespace Demo2.Web.Services;

public sealed record VetDispatchReceipt(
    string DispatchReference,
    string ToolboxName,
    string ToolName,
    DateTimeOffset DispatchedAt);

public interface IWelfareToolboxClient
{
    Task<ReservationInventoryState> GetInventoryStateAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This toolbox does not expose reservation inventory.");

    Task<ReservationInventoryState> ResetReservationsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This toolbox does not support reservation reset.");

    Task<VetDispatchReceipt> DispatchVetAsync(
        WelfareCaseRecord caseRecord,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryWelfareToolboxClient(
    IOptions<Demo2Options> options,
    TimeProvider timeProvider,
    FloodSupportReservationSimulator? simulator = null)
    : IWelfareToolboxClient
{
    private readonly Demo2Options _options = options.Value;
    private readonly FloodSupportReservationSimulator _simulator = simulator ?? new();

    public SupportReservationReceipt? FindReservation(string actionRequestId) =>
        _simulator.FindReservation(actionRequestId);

    public Task<ReservationInventoryState> GetInventoryStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_simulator.GetInventoryState());
    }

    public Task<ReservationInventoryState> ResetReservationsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_simulator.ResetReservations());
    }

    public Task<VetDispatchReceipt> DispatchVetAsync(
        WelfareCaseRecord caseRecord,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (caseRecord.Status != WelfareCaseStatus.DispatchApproved)
        {
            throw new InvalidOperationException("Local simulation requires approved case state.");
        }
        SupportPackageRequest request = FloodSupportApprovalGuard.ValidateCasePackage(caseRecord);
        SupportReservationReceipt receipt = _simulator.Reserve(request, timeProvider.GetUtcNow());
        return Task.FromResult(new VetDispatchReceipt(receipt.ReservationReference,
            _options.Toolbox.Name, _options.Toolbox.DispatchToolName, receipt.RecordedAt));
    }
}

public sealed class Demo2AdminWorkflowService(
    IWelfareCaseStore caseStore,
    IApprovalStore approvalStore,
    IInspectorDecisionStore inspectorDecisionStore,
    IWelfareToolboxClient toolboxClient,
    IDemo2ToolPolicyProvider toolPolicyProvider,
    IOptions<Demo2Options> options,
    Demo2AuditTrail auditTrail,
    TimeProvider timeProvider)
{
    private readonly Demo2Options _options = options.Value;

    public Task<ReservationInventoryState> GetInventoryStateAsync(CancellationToken cancellationToken = default) =>
        toolboxClient.GetInventoryStateAsync(cancellationToken);

    public Task<ReservationInventoryState> ResetReservationsAsync(CancellationToken cancellationToken = default) =>
        toolboxClient.ResetReservationsAsync(cancellationToken);

    public async Task<IReadOnlyList<WelfareCaseRecord>> ListCasesAsync(
        CancellationToken cancellationToken = default)
    {
        return await caseStore.ListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WelfareApprovalRecord>> ListPendingApprovalsAsync(
        CancellationToken cancellationToken = default)
    {
        return await approvalStore.ListPendingAsync(cancellationToken);
    }

    public async Task<WelfareApprovalRecord> DecideApprovalAsync(
        string caseId,
        string requestId,
        ApprovalOutcome outcome,
        string decisionCode,
        CancellationToken cancellationToken = default)
    {
        using IDisposable transition = await Demo2CaseTransitions.EnterAsync(caseId, cancellationToken);
        return await DecideApprovalCoreAsync(caseId, requestId, outcome, decisionCode, cancellationToken);
    }

    private async Task<WelfareApprovalRecord> DecideApprovalCoreAsync(
        string caseId,
        string requestId,
        ApprovalOutcome outcome,
        string decisionCode,
        CancellationToken cancellationToken)
    {
        ValidateDecisionCode(decisionCode);
        WelfareApprovalRecord approval = await approvalStore.GetAsync(
                caseId,
                requestId,
                cancellationToken)
            ?? throw new KeyNotFoundException("Approval request was not found.");
        WelfareCaseRecord caseRecord = await caseStore.GetAsync(caseId, cancellationToken)
            ?? throw new KeyNotFoundException("Case was not found.");
        if (approval.CaseVersion != caseRecord.Version ||
            !string.Equals(
                caseRecord.ApprovalRequestId,
                requestId,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Approval request is stale for the current case version.");
        }

        if (!approval.IsPending)
        {
            throw new InvalidOperationException("Approval request was already decided.");
        }

        if (caseRecord.OwnerObjectId != Demo2SyntheticOperator.ObjectId)
        {
            throw new UnauthorizedAccessException("The local operator does not own this case.");
        }
        _ = FloodSupportApprovalGuard.Validate(caseRecord, approval, timeProvider.GetUtcNow(), requireDecision: false);

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (approval.Request.ExpiresAt < now)
        {
            throw new InvalidOperationException("Approval request has expired.");
        }

        ApprovalDecision decision = new(
            approval.Request.RequestId,
            approval.Request.CorrelationId,
            approval.Request.ToolName,
            outcome,
            Demo2SyntheticOperator.ObjectId,
            decisionCode,
            now);
        WelfareApprovalRecord updatedApproval = approval with
        {
            Decision = decision,
            UpdatedAt = now
        };
        await approvalStore.UpsertAsync(updatedApproval, cancellationToken);

        await caseStore.UpsertAsync(
            caseRecord with
            {
                Status = outcome == ApprovalOutcome.Approved
                    ? WelfareCaseStatus.DispatchApproved
                    : WelfareCaseStatus.DispatchDenied,
                UpdatedAt = now
            },
            cancellationToken);
        await auditTrail.WriteAsync(
            AuditEventKind.ApprovalDecided,
            approval.Request.CorrelationId,
            "approval",
            approval.Request.ToolName,
            decisionCode,
            outcome.ToString().ToLowerInvariant(),
            Hash(caseId),
            approvalRequestId: requestId,
            cancellationToken: cancellationToken);
        return updatedApproval;
    }

    public async Task<InspectorDecisionRecord> OverrideUrgencyAsync(
        string caseId,
        WelfareUrgency overrideUrgency,
        string reasonCode,
        CancellationToken cancellationToken = default)
    {
        using IDisposable transition = await Demo2CaseTransitions.EnterAsync(caseId, cancellationToken);
        return await OverrideUrgencyCoreAsync(caseId, overrideUrgency, reasonCode, cancellationToken);
    }

    private async Task<InspectorDecisionRecord> OverrideUrgencyCoreAsync(
        string caseId,
        WelfareUrgency overrideUrgency,
        string reasonCode,
        CancellationToken cancellationToken)
    {
        ValidateDecisionCode(reasonCode);
        WelfareCaseRecord current = await caseStore.GetAsync(caseId, cancellationToken)
            ?? throw new KeyNotFoundException("Case was not found.");
        if (current.Status == WelfareCaseStatus.VetDispatched || current.SupportExecutionInProgress ||
            current.SupportExecutionRequiresReconciliation)
        {
            throw new InvalidOperationException(
                "Urgency cannot be changed after support reservation has completed.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        int nextVersion = checked(current.Version + 1);
        InspectorDecisionRecord decision = new(
            $"decision-{Guid.NewGuid():N}",
            caseId,
            current.Urgency,
            overrideUrgency,
            Demo2SyntheticOperator.ObjectId,
            reasonCode,
            now);
        await inspectorDecisionStore.AddAsync(decision, cancellationToken);

        WelfareRoute route = WelfareDeterministicPolicy.NormalizeRoute(
            overrideUrgency,
            overrideUrgency == WelfareUrgency.High
                ? WelfareRoute.PriorityInspectorReview
                : current.Route);
        WelfareCaseRecord overridden = current with
        {
            Urgency = overrideUrgency,
            Route = route,
            Status = WelfareCaseStatus.Assessed,
            PolicyReasons = [.. current.PolicyReasons, $"admin-override:{reasonCode}"],
            ApprovalRequestId = null,
            Version = nextVersion,
            UpdatedAt = now
        };
        await caseStore.UpsertAsync(overridden, cancellationToken);
        await InvalidatePendingApprovalAsync(current, now, cancellationToken);
        await auditTrail.WriteAsync(
            AuditEventKind.AnswerProduced,
            CorrelationId.Create(),
            "admin-override",
            "none",
            reasonCode,
            overrideUrgency.ToString().ToLowerInvariant(),
            Hash(caseId),
            cancellationToken: cancellationToken);
        return decision;
    }

    private async Task InvalidatePendingApprovalAsync(
        WelfareCaseRecord current,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(current.ApprovalRequestId))
        {
            return;
        }

        WelfareApprovalRecord? approval = await approvalStore.GetAsync(
            current.CaseId,
            current.ApprovalRequestId,
            cancellationToken);
        if (approval?.IsPending != true)
        {
            return;
        }

        await approvalStore.UpsertAsync(
            approval with
            {
                Decision = new ApprovalDecision(
                    approval.Request.RequestId,
                    approval.Request.CorrelationId,
                    approval.Request.ToolName,
                    ApprovalOutcome.Denied,
                    Demo2SyntheticOperator.ObjectId,
                    "urgency-override-invalidated",
                    now),
                UpdatedAt = now
            },
            cancellationToken);
    }

    private bool RequiresApproval() =>
        toolPolicyProvider.Policy.ToolApprovals.SingleOrDefault(rule =>
            string.Equals(
                rule.ToolName,
                _options.Toolbox.DispatchToolName,
                StringComparison.Ordinal))
        ?.RequiresApproval == true;

    public async Task<VetDispatchReceipt> DispatchVetAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        using IDisposable transition = await Demo2CaseTransitions.EnterAsync(caseId, cancellationToken);
        return await DispatchVetAuthorizedAsync(caseId, cancellationToken);
    }

    private async Task<VetDispatchReceipt> DispatchVetAuthorizedAsync(
        string caseId,
        CancellationToken cancellationToken)
    {
        WelfareCaseRecord caseRecord = await caseStore.GetAsync(caseId, cancellationToken)
            ?? throw new KeyNotFoundException("Case was not found.");
        if (string.IsNullOrWhiteSpace(caseRecord.ApprovalRequestId))
        {
            throw new InvalidOperationException("Dispatch is blocked until approval exists.");
        }
        if (caseRecord.SupportExecutionInProgress)
        {
            throw new InvalidOperationException("A support reservation continuation is already in progress.");
        }

        WelfareApprovalRecord approval = await approvalStore.GetAsync(
                caseId,
                caseRecord.ApprovalRequestId,
                cancellationToken)
            ?? throw new InvalidOperationException("Dispatch approval state was not found.");
        if (approval.CaseVersion != caseRecord.Version)
        {
            throw new InvalidOperationException("Approval request is stale for the current case version.");
        }

        if (approval.DispatchReference is not null ||
            caseRecord.Status == WelfareCaseStatus.VetDispatched)
        {
            throw new InvalidOperationException("Support reservation has already been completed.");
        }

        TurnPolicyEnforcer enforcer = new(toolPolicyProvider.Policy);
        SupportPackageRequest supportRequest = FloodSupportApprovalGuard.Validate(
            caseRecord, approval, timeProvider.GetUtcNow(), requireDecision: true);
        Dictionary<string, string> arguments = FloodSupportCatalogue.ToArguments(supportRequest)
            .ToDictionary(pair => pair.Key, pair => Convert.ToString(pair.Value, System.Globalization.CultureInfo.InvariantCulture)!, StringComparer.Ordinal);
        PolicyDecision authorization = enforcer.AuthorizeToolCall(
            approval.Request,
            arguments,
            approval.Decision,
            timeProvider.GetUtcNow());
        await auditTrail.WriteAsync(
            authorization.IsAllowed ? AuditEventKind.ToolRequested : AuditEventKind.ToolDenied,
            approval.Request.CorrelationId,
            "dispatch",
            _options.Toolbox.DispatchToolName,
            authorization.Code,
            authorization.IsAllowed ? "authorized" : "denied",
            Hash(caseId),
            approvalRequestId: approval.Request.RequestId,
            cancellationToken: cancellationToken);
        if (!authorization.IsAllowed)
        {
            throw new InvalidOperationException($"Dispatch denied by policy: {authorization.Code}");
        }

        if (caseRecord.Status != WelfareCaseStatus.DispatchApproved)
        {
            throw new InvalidOperationException("Case is not in the approved dispatch state.");
        }

        if (string.IsNullOrWhiteSpace(caseRecord.FarmReference))
        {
            throw new InvalidOperationException(
                "Support reservation requires a verified area reference.");
        }

        bool completed = false;
        bool executionStarted = false;
        SupportReservationReceipt? receipt = null;
        try
        {
            await caseStore.UpsertAsync(
                caseRecord with { SupportExecutionInProgress = true, UpdatedAt = timeProvider.GetUtcNow() },
                cancellationToken);
            executionStarted = true;
            VetDispatchReceipt dispatch = await toolboxClient.DispatchVetAsync(
                caseRecord,
                cancellationToken);
            receipt = new(dispatch.DispatchReference, supportRequest, caseRecord.PackageSnapshot!,
                dispatch.DispatchedAt, "simulated", "Approved package reservation.");
            FloodSupportApprovalGuard.ValidateReceipt(receipt, supportRequest);
            DateTimeOffset now = timeProvider.GetUtcNow();
            await approvalStore.UpsertAsync(
                approval with
                {
                    DispatchReference = dispatch.DispatchReference,
                    UpdatedAt = now
                },
                cancellationToken);
            await caseStore.UpsertAsync(
                caseRecord with
                {
                    Status = WelfareCaseStatus.VetDispatched,
                    SupportExecutionInProgress = false,
                    SupportExecutionRequiresReconciliation = false,
                    UpdatedAt = now
                },
                cancellationToken);
            completed = true;
            await auditTrail.WriteAsync(
                AuditEventKind.ToolCompleted,
                approval.Request.CorrelationId,
                "dispatch",
                _options.Toolbox.DispatchToolName,
                "support-reservation",
                "completed",
                Hash(caseId),
                approvalRequestId: approval.Request.RequestId,
                cancellationToken: cancellationToken);
            return dispatch;
        }
        finally
        {
            if (!completed)
            {
                // A failed retry cannot establish the original reservation's outcome.
                bool outcomeKnown = !executionStarted && !caseRecord.SupportExecutionRequiresReconciliation;
                if (toolboxClient is InMemoryWelfareToolboxClient local)
                {
                    receipt = local.FindReservation(supportRequest.ActionRequestId);
                    outcomeKnown = !caseRecord.SupportExecutionRequiresReconciliation || receipt is not null;
                }
                (WelfareCaseRecord recoveredCase, WelfareApprovalRecord recoveredApproval) =
                    SupportExecutionRecovery.Resolve(caseRecord, approval, outcomeKnown, receipt, timeProvider.GetUtcNow());
                await approvalStore.UpsertAsync(recoveredApproval, CancellationToken.None);
                await caseStore.UpsertAsync(recoveredCase, CancellationToken.None);
            }
        }
    }

    private static void ValidateDecisionCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException("Decision code contains unsupported characters.", nameof(value));
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(value)));
}
