using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Defra.Contracts.FloodSupport;
using Defra.Contracts.V1;
using Defra.Observability;
using Demo2.Web.Agent;
using Demo2.Web.Configuration;
using Demo2.Web.Domain;
using Demo2.Web.Observability;
using Demo2.Web.Persistence;
using Microsoft.Extensions.Options;

namespace Demo2.Web.Services;

public sealed class WelfareAssessmentRejectedException(string decisionCode)
    : Exception($"Assessment rejected by policy: {decisionCode}")
{
    public string DecisionCode { get; } = decisionCode;
}

public sealed class WelfareAssessmentService(
    ComplaintInputGuard inputGuard,
    WelfareDeterministicPolicy deterministicPolicy,
    IWelfareAgentClient agentClient,
    IWelfareCaseStore caseStore,
    IConversationStore conversationStore,
    IApprovalStore approvalStore,
    IDemo2ToolPolicyProvider toolPolicyProvider,
    Demo2AuditTrail auditTrail,
    IOptions<Demo2Options> options,
    TimeProvider timeProvider)
{
    private readonly Demo2Options _options = options.Value;

    public AssessmentExecutionProvider ExecutionProvider =>
        string.Equals(_options.Agent.Mode, Demo2AgentModes.Azure, StringComparison.Ordinal)
            ? AssessmentExecutionProvider.AzureFoundry
            : AssessmentExecutionProvider.LocalDeterministic;

    public async IAsyncEnumerable<WelfareAssessmentEvent> AssessAsync(
        WelfareAssessmentCommand command,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        AgentMcpApprovalHandler? approvalHandler = null)
    {
        ExecutionProgress progress = new();
        try
        {
            await foreach (WelfareAssessmentEvent update in AssessCoreAsync(
                               command, progress, cancellationToken, approvalHandler))
            {
                yield return update;
            }
        }
        finally
        {
            if (!progress.FinalStatePersisted && progress.ExecutingCase is not null)
            {
                await RecoverInterruptedExecutionAsync(progress);
            }
        }
    }

    private async IAsyncEnumerable<WelfareAssessmentEvent> AssessCoreAsync(
        WelfareAssessmentCommand command,
        ExecutionProgress progress,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        AgentMcpApprovalHandler? approvalHandler)
    {
        ValidateCommand(command);
        WelfareCaseRecord? previousCase = await caseStore.GetAsync(
            command.CaseId,
            cancellationToken);
        if (previousCase is not null && previousCase.OwnerObjectId != command.OwnerObjectId)
        {
            throw new UnauthorizedAccessException("Case reference belongs to a different user.");
        }
        if (previousCase?.SupportExecutionInProgress == true ||
            previousCase?.SupportExecutionRequiresReconciliation == true ||
            previousCase?.Status == WelfareCaseStatus.VetDispatched)
        {
            throw new InvalidOperationException("A completed or unresolved reservation must be reviewed before reassessing this case.");
        }

        int nextVersion = (previousCase?.Version ?? 0) + 1;
        InputGuardResult input = inputGuard.Screen(command.ComplaintText);
        CorrelationId correlationId = CorrelationId.Create();
        DateTimeOffset now = timeProvider.GetUtcNow();
        using Activity? activity = DefraActivities.StartAgentTurn(correlationId, "demo2.flood-support.assess");
        DefraActivities.SetPolicy(
            activity,
            deterministicPolicy.Rules.RuleSetId,
            deterministicPolicy.Rules.ContractVersion);
        DefraActivities.SetDecision(activity, input.Code);
        await auditTrail.WriteAsync(
            AuditEventKind.InputScreened,
            correlationId,
            "input",
            "none",
            input.Code,
            input.IsAllowed ? "allowed" : "denied",
            input.InputHash,
            cancellationToken: cancellationToken);

        if (!input.IsAllowed)
        {
            throw new WelfareAssessmentRejectedException(input.Code);
        }

        yield return new(
            AssessmentEventKind.Accepted,
            "Situation screened. Prompt and personal data will not be logged.",
            now,
            ExecutionProvider: ExecutionProvider);

        EmergencyPolicyResult emergency = deterministicPolicy.EvaluateEmergency(
            input.SanitizedText);

        AgentAssessment agentAssessment;
        WelfareUrgency urgency;
        WelfareRoute route;
        List<string> policyReasons = [];
        List<string> evidence = [];
        bool repeatEscalated = false;
        bool continuationFloorApplied = false;
        if (emergency.IsEmergency)
        {
            policyReasons.Add("pre-model-emergency-high");
            evidence.AddRange(emergency.MatchedSignals.Select(signal => $"deterministic:{signal}"));
            DefraActivities.SetDecision(activity, "pre-model-emergency-high");
            yield return new(
                AssessmentEventKind.EmergencyFloorApplied,
                "Emergency signal matched. High urgency floor set before provider assessment.",
                timeProvider.GetUtcNow(),
                ExecutionProvider: ExecutionProvider);
        }

        ConversationRecord? conversation = previousCase is null
            ? null
            : await conversationStore.GetAsync(command.CaseId, cancellationToken);

        string issuedActionRequestId = $"approval-{Guid.NewGuid():N}";
        string incidentReference = CreateIncidentReference(command.CaseId);
        string? expectedAreaReference = NormalizeOptionalIdentifier(command.FarmReference);
        WelfareApprovalRecord? agentApproval = null;
        WelfareCaseRecord? pendingCase = null;
        AgentMcpApprovalHandler agentMcpApprovalHandler = async (request, token) =>
                {
                    if (!string.Equals(
                            request.ToolName,
                            _options.Toolbox.DispatchToolName,
                            StringComparison.Ordinal) ||
                        !RequiresApproval(request.ToolName))
                    {
                        throw new InvalidOperationException(
                            "Agent requested approval for a tool not permitted by dispatch policy.");
                    }

                    if (agentApproval is not null)
                    {
                        throw new InvalidOperationException(
                            "Only one support reservation approval is permitted per assessment.");
                    }

                    if (!FloodSupportCatalogue.TryParseApprovalArguments(request.Arguments, out SupportPackageRequest? supportRequest) ||
                        !FloodSupportCatalogue.ValidateRequest(supportRequest!, expectedAreaReference!, out SupportPackageDefinition? package) ||
                        supportRequest!.ActionRequestId != issuedActionRequestId ||
                        supportRequest.IncidentReference != incidentReference ||
                        supportRequest.ReservationGeneration != command.ReservationGeneration ||
                        string.IsNullOrWhiteSpace(request.RequestId))
                    {
                        throw new InvalidOperationException("Approval arguments do not match the current issued flood-support assessment request.");
                    }

                    DateTimeOffset requestedAt = timeProvider.GetUtcNow();
                    ApprovalRequest approvalRequest = new(
                        request.RequestId,
                        correlationId,
                        request.ToolName,
                        FloodSupportCatalogue.ToArguments(supportRequest).Keys.Order(StringComparer.Ordinal).ToArray(),
                        "mcp-approval-required",
                        requestedAt,
                        request.ExpiresAt is { } suppliedExpiry && suppliedExpiry < requestedAt.AddMinutes(_options.ApprovalLifetimeMinutes)
                            ? suppliedExpiry : requestedAt.AddMinutes(_options.ApprovalLifetimeMinutes));
                    agentApproval = new(
                        command.CaseId,
                        nextVersion,
                        approvalRequest,
                        Decision: null,
                        DispatchReference: null,
                        UpdatedAt: requestedAt)
                    {
                        SupportRequest = supportRequest,
                        PackageSnapshot = package
                    };
                    pendingCase = new(command.CaseId, command.OwnerObjectId, expectedAreaReference,
                        command.ReceivedAt, input.InputHash,
                        emergency.IsEmergency ? WelfareUrgency.High : previousCase?.Urgency ?? WelfareUrgency.Unclassified,
                        emergency.IsEmergency ? WelfareRoute.ImmediateHumanEscalation : previousCase?.Route ?? WelfareRoute.RequestClarification,
                        WelfareCaseStatus.PendingApproval, "Flood-support package awaits an explicit operator decision.",
                        policyReasons.ToArray(), false, null, request.RequestId, nextVersion, requestedAt)
                    {
                        IssuedActionRequestId = issuedActionRequestId,
                        IncidentReference = incidentReference,
                        SupportRequest = supportRequest,
                        PackageSnapshot = package
                    };
                    _ = FloodSupportApprovalGuard.Validate(pendingCase, agentApproval, requestedAt, requireDecision: false);
                    using (await Demo2CaseTransitions.EnterAsync(command.CaseId, token))
                    {
                        WelfareCaseRecord? current = await caseStore.GetAsync(command.CaseId, token);
                        if (!Demo2CaseTransitions.MatchesSnapshot(current, previousCase))
                        {
                            throw new InvalidOperationException("A newer case transition invalidated this assessment.");
                        }
                        await approvalStore.UpsertAsync(agentApproval, token);
                        await caseStore.UpsertAsync(pendingCase, token);
                    }
                    await auditTrail.WriteAsync(
                        AuditEventKind.ApprovalRequested,
                        correlationId,
                        "agent-mcp-approval",
                        request.ToolName,
                        "mcp-approval-required",
                        "pending",
                        input.InputHash,
                        approvalRequestId: request.RequestId,
                        cancellationToken: token);

                    async Task PersistDecisionAsync(
                        AgentMcpApprovalDecision decision,
                        CancellationToken persistenceToken)
                    {
                        ValidateDecisionCode(decision.ReasonCode);
                        using IDisposable transition = await Demo2CaseTransitions.EnterAsync(command.CaseId, persistenceToken);
                        DateTimeOffset decidedAt = timeProvider.GetUtcNow();
                        WelfareCaseRecord currentCase = await caseStore.GetAsync(command.CaseId, persistenceToken)
                            ?? throw new InvalidOperationException("The pending case is missing.");
                        WelfareApprovalRecord currentApproval = await approvalStore.GetAsync(command.CaseId, request.RequestId, persistenceToken)
                            ?? throw new InvalidOperationException("The pending approval is missing.");
                        if (!Demo2CaseTransitions.MatchesSnapshot(currentCase, pendingCase) ||
                            currentCase.OwnerObjectId != command.OwnerObjectId || !currentApproval.IsPending ||
                            currentCase.Status != WelfareCaseStatus.PendingApproval || currentApproval.SupportRequest != supportRequest ||
                            currentApproval.CaseVersion != nextVersion)
                        {
                            throw new InvalidOperationException("The approval was changed, decided or invalidated while awaiting a decision.");
                        }
                        if (decision.Approved)
                        {
                            _ = FloodSupportApprovalGuard.Validate(currentCase, currentApproval, decidedAt, requireDecision: false);
                        }
                        ApprovalDecision persistedDecision = new(
                            request.RequestId,
                            correlationId,
                            request.ToolName,
                            decision.Approved ? ApprovalOutcome.Approved : ApprovalOutcome.Denied,
                            command.OwnerObjectId,
                            decision.ReasonCode,
                            decidedAt);
                        agentApproval = currentApproval with
                        {
                            Decision = persistedDecision,
                            UpdatedAt = decidedAt
                        };
                        await approvalStore.UpsertAsync(agentApproval, persistenceToken);
                        pendingCase = currentCase with
                        {
                            Status = decision.Approved ? WelfareCaseStatus.DispatchApproved : WelfareCaseStatus.DispatchDenied,
                            SupportExecutionInProgress = decision.Approved,
                            UpdatedAt = decidedAt
                        };
                        if (decision.Approved)
                        {
                            progress.ExecutingCase = pendingCase;
                        }
                        await caseStore.UpsertAsync(pendingCase, persistenceToken);
                        await auditTrail.WriteAsync(
                            AuditEventKind.ApprovalDecided,
                            correlationId,
                            "agent-mcp-approval",
                            request.ToolName,
                            decision.ReasonCode,
                            decision.Approved ? "approved" : "denied",
                            input.InputHash,
                            approvalRequestId: request.RequestId,
                            cancellationToken: persistenceToken);
                    }

                    AgentMcpApprovalDecision decision;
                    try
                    {
                        AgentMcpApprovalRequest informedRequest = request with
                        {
                            ExpiresAt = approvalRequest.ExpiresAt,
                            RequestedAt = requestedAt,
                            Arguments = request.Arguments.Count == 1
                                ? new Dictionary<string, object?>(StringComparer.Ordinal)
                                {
                                    ["request"] = FloodSupportCatalogue.ToArguments(supportRequest)
                                }
                                : FloodSupportCatalogue.ToArguments(supportRequest),
                            ArgumentNames = approvalRequest.ArgumentNames
                        };
                        decision = approvalHandler is null
                            ? new(false, "approval-handler-unavailable")
                            : await approvalHandler(informedRequest, token);
                    }
                    catch (OperationCanceledException)
                    {
                        await PersistDecisionAsync(
                            new(false, "operator-cancelled"),
                            CancellationToken.None);
                        throw;
                    }

                    await PersistDecisionAsync(decision, token);
                    progress.DecisionReleased = decision.Approved;
                    return decision;
                };

        WelfareUrgency minimumUrgency = emergency.IsEmergency
            ? WelfareUrgency.High
            : previousCase?.Urgency ?? WelfareUrgency.Unclassified;
        WelfareRoute? requiredRoute = emergency.IsEmergency
            ? WelfareRoute.ImmediateHumanEscalation
            : previousCase?.Urgency == WelfareUrgency.High
                ? previousCase.Route
                : null;
        AgentAssessmentRequest agentRequest = new(
            command.CaseId,
            NormalizeOptionalIdentifier(command.FarmReference),
            input.SanitizedText,
            command.ReceivedAt,
            deterministicPolicy.Rules.RepeatComplaintWindowDays,
            minimumUrgency,
            requiredRoute,
            issuedActionRequestId,
            incidentReference,
            conversation?.ConversationId,
            conversation?.PreviousResponseId)
        {
            ReservationGeneration = command.ReservationGeneration
        };

        Stopwatch stopwatch = Stopwatch.StartNew();
        AgentAssessment? streamedAssessment = null;
        bool approvalDecisionProjected = false;
        await using IAsyncEnumerator<AgentAssessmentUpdate> updates = agentClient
            .AssessStreamingAsync(
                agentRequest,
                agentMcpApprovalHandler,
                cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            AgentAssessmentUpdate update;
            try
            {
                if (!await updates.MoveNextAsync())
                {
                    break;
                }

                update = updates.Current;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                if (exception is SupportReservationNotExecutedException rejected &&
                    rejected.ActionRequestId == issuedActionRequestId)
                {
                    progress.NonExecutionConfirmed = true;
                }
                activity?.SetStatus(
                    ActivityStatusCode.Error,
                    exception.GetBaseException().GetType().Name);
                throw;
            }

            if (update.Kind == AgentAssessmentUpdateKind.ResponseValidated)
            {
                streamedAssessment = update.Assessment ??
                    throw new AgentResponseFormatException(
                        "Validated agent update contained no assessment.");
                progress.Receipt = streamedAssessment.Dispatch?.Receipt;
            }

            WelfareAssessmentEvent? projected = ProjectAgentUpdate(update, ExecutionProvider);
            if (projected is not null)
            {
                approvalDecisionProjected |=
                    projected.Kind == AssessmentEventKind.McpApprovalDecided;
                yield return projected;
            }
        }

        agentAssessment = streamedAssessment ??
            throw new AgentResponseFormatException(
                "Agent stream completed without a validated assessment.");
        stopwatch.Stop();

        await conversationStore.UpsertAsync(
            new ConversationRecord(
                command.CaseId,
                agentAssessment.ConversationId,
                agentAssessment.ResponseId,
                timeProvider.GetUtcNow()),
            cancellationToken);
        await auditTrail.WriteAsync(
            AuditEventKind.AnswerProduced,
            correlationId,
            "agent",
            "none",
            "agent-response",
            "completed",
            input.InputHash,
            stopwatch.ElapsedMilliseconds,
            cancellationToken: cancellationToken);

        PostModelPolicyResult postModel = deterministicPolicy.ApplyPostModel(
            agentAssessment,
            previousCase);
        urgency = emergency.IsEmergency ? WelfareUrgency.High : postModel.Urgency;
        route = emergency.IsEmergency ? WelfareRoute.ImmediateHumanEscalation : postModel.Route;
        policyReasons.AddRange(postModel.Reasons);
        evidence.AddRange(agentAssessment.Evidence);
        repeatEscalated = postModel.RepeatEscalated;
        continuationFloorApplied = postModel.ContinuationFloorApplied;

        if (repeatEscalated)
        {
            yield return new(
                AssessmentEventKind.RepeatOffenderEscalated,
                "Configured repeated-unmet-needs threshold met. Post-provider urgency raised to High.",
                timeProvider.GetUtcNow(),
                ExecutionProvider: ExecutionProvider);
        }

        if (continuationFloorApplied)
        {
            yield return new(
                AssessmentEventKind.ContinuationFloorApplied,
                "Continuation attempted a downgrade. Previous urgency floor retained.",
                timeProvider.GetUtcNow(),
                ExecutionProvider: ExecutionProvider);
        }

        WelfareApprovalRecord? approval = agentApproval;
        if (approval?.PackageSnapshot is { } proposedPackage)
        {
            foreach (string fact in proposedPackage.Evidence.Concat(proposedPackage.Risks))
            {
                if (!evidence.Contains(fact, StringComparer.Ordinal)) evidence.Add(fact);
            }
        }

        if (agentAssessment.Dispatch is not null)
        {
            if (approval?.Decision?.Outcome != ApprovalOutcome.Approved || pendingCase is null ||
                agentAssessment.Dispatch.Receipt is not { } receipt)
            {
                throw new InvalidOperationException(
                    "Support reservation completed without an approved MCP tool decision.");
            }

            using (await Demo2CaseTransitions.EnterAsync(command.CaseId, cancellationToken))
            {
                WelfareCaseRecord currentCase = await caseStore.GetAsync(command.CaseId, cancellationToken)
                    ?? throw new InvalidOperationException("The approved reservation case is missing.");
                WelfareApprovalRecord currentApproval = await approvalStore.GetAsync(
                    command.CaseId,
                    approval.Request.RequestId,
                    cancellationToken)
                    ?? throw new InvalidOperationException("The approved reservation decision is missing.");
                if (!Demo2CaseTransitions.MatchesSnapshot(currentCase, pendingCase))
                {
                    throw new InvalidOperationException("The approved reservation binding changed before receipt validation.");
                }

                DateTimeOffset validatedAt = timeProvider.GetUtcNow();
                _ = FloodSupportApprovalGuard.Validate(
                    currentCase,
                    currentApproval,
                    validatedAt,
                    requireDecision: true);
                FloodSupportApprovalGuard.ValidateReceipt(receipt, currentApproval.SupportRequest!);
                pendingCase = currentCase;
                approval = currentApproval with
                {
                    DispatchReference = agentAssessment.Dispatch.DispatchReference,
                    UpdatedAt = validatedAt
                };
                await approvalStore.UpsertAsync(approval, cancellationToken);
            }
        }

        if (approval?.Decision is not null &&
            !approvalDecisionProjected)
        {
            yield return new(
                AssessmentEventKind.McpApprovalDecided,
                approval.Decision.Outcome == ApprovalOutcome.Approved
                    ? "MCP support reservation approved during agent execution."
                    : "MCP support reservation rejected during agent execution.",
                timeProvider.GetUtcNow(),
                ExecutionProvider: ExecutionProvider,
                ApprovalGranted: approval.Decision.Outcome == ApprovalOutcome.Approved);
        }

        if (agentAssessment.Dispatch is not null)
        {
            yield return new(
                AssessmentEventKind.DispatchCompleted,
                $"Support package reserved (simulation) with reference {agentAssessment.Dispatch.DispatchReference}.",
                timeProvider.GetUtcNow(),
                ExecutionProvider: ExecutionProvider);
        }

        WelfareCaseStatus status = agentAssessment.Dispatch is not null
            ? WelfareCaseStatus.VetDispatched
            : approval?.Decision?.Outcome == ApprovalOutcome.Approved
                ? WelfareCaseStatus.DispatchApproved
                : approval?.Decision?.Outcome == ApprovalOutcome.Denied
                    ? WelfareCaseStatus.DispatchDenied
                    : approval is not null
                        ? WelfareCaseStatus.PendingApproval
                        : WelfareCaseStatus.Assessed;

        WelfareCaseRecord record = new(
            command.CaseId,
            command.OwnerObjectId,
            NormalizeOptionalIdentifier(command.FarmReference),
            command.ReceivedAt,
            input.InputHash,
            urgency,
            route,
            status,
            agentAssessment.Summary,
            policyReasons,
            AgentBypassed: false,
            agentAssessment.ResponseId,
            approval?.Request.RequestId,
            nextVersion,
            timeProvider.GetUtcNow())
        {
            IssuedActionRequestId = issuedActionRequestId,
            IncidentReference = incidentReference,
            SupportRequest = approval?.SupportRequest,
            PackageSnapshot = approval?.PackageSnapshot,
            SupportExecutionRequiresReconciliation =
                approval?.Decision?.Outcome == ApprovalOutcome.Approved && agentAssessment.Dispatch is null
        };
        if (record.SupportExecutionRequiresReconciliation)
        {
            record = record with
            {
                Summary = "Approval was received, but there is no confirmed reservation receipt. Reconcile the original action."
            };
        }
        yield return new(
            AssessmentEventKind.PersistenceStarted,
            "Saving final case and policy state.",
            timeProvider.GetUtcNow(),
            ExecutionProvider: ExecutionProvider);
        using (await Demo2CaseTransitions.EnterAsync(command.CaseId, cancellationToken))
        {
            WelfareCaseRecord? current = await caseStore.GetAsync(command.CaseId, cancellationToken);
            if (!Demo2CaseTransitions.MatchesSnapshot(current, pendingCase ?? previousCase))
            {
                throw new InvalidOperationException("The assessment was invalidated by a newer case transition.");
            }
            await caseStore.UpsertAsync(record, cancellationToken);
            progress.FinalStatePersisted = true;
        }
        await auditTrail.WriteAsync(
            AuditEventKind.AnswerProduced,
            correlationId,
            "post-policy",
            "none",
            repeatEscalated
                ? "repeated-unmet-needs-high"
                : continuationFloorApplied
                    ? "continuation-no-downgrade"
                    : emergency.IsEmergency
                        ? "emergency-floor"
                        : "agent-assessment",
            urgency.ToString().ToLowerInvariant(),
            input.InputHash,
            approvalRequestId: approval?.Request.RequestId,
            cancellationToken: cancellationToken);

        WelfareAssessmentOutcome outcome = new(
            record,
            approval,
            evidence,
            agentAssessment.ToolTrace);
        yield return new(
            AssessmentEventKind.Persisted,
            "Final case and workflow state persisted.",
            timeProvider.GetUtcNow(),
            ExecutionProvider: ExecutionProvider);
        yield return new(
            AssessmentEventKind.Completed,
            "Assessment complete.",
            timeProvider.GetUtcNow(),
            outcome,
            ExecutionProvider: ExecutionProvider);
    }

    private async Task RecoverInterruptedExecutionAsync(ExecutionProgress progress)
    {
        WelfareCaseRecord expected = progress.ExecutingCase!;
        using IDisposable transition = await Demo2CaseTransitions.EnterAsync(expected.CaseId, CancellationToken.None);
        WelfareCaseRecord current = await caseStore.GetAsync(expected.CaseId, CancellationToken.None)
            ?? throw new InvalidOperationException("The interrupted reservation case is missing.");
        if (current.OwnerObjectId != expected.OwnerObjectId || current.Version != expected.Version ||
            current.ApprovalRequestId != expected.ApprovalRequestId ||
            current.IssuedActionRequestId != expected.IssuedActionRequestId ||
            current.SupportRequest != expected.SupportRequest)
        {
            throw new InvalidOperationException("The interrupted reservation binding changed; automatic recovery was refused.");
        }
        _ = FloodSupportApprovalGuard.ValidateCasePackage(current);
        if (current.Status == WelfareCaseStatus.VetDispatched)
        {
            return;
        }
        WelfareApprovalRecord approval = await approvalStore.GetAsync(
            current.CaseId, current.ApprovalRequestId!, CancellationToken.None)
            ?? throw new InvalidOperationException("The interrupted reservation approval is missing.");
        bool outcomeKnown = !progress.DecisionReleased || progress.NonExecutionConfirmed;
        SupportReservationReceipt? receipt = progress.Receipt;
        if (agentClient is SyntheticContractWelfareAgentClient local)
        {
            receipt = local.FindReservation(current.IssuedActionRequestId!);
            outcomeKnown = true;
        }
        (WelfareCaseRecord recoveredCase, WelfareApprovalRecord recoveredApproval) =
            SupportExecutionRecovery.Resolve(current, approval, outcomeKnown, receipt, timeProvider.GetUtcNow());
        await approvalStore.UpsertAsync(recoveredApproval, CancellationToken.None);
        await caseStore.UpsertAsync(recoveredCase, CancellationToken.None);
        await auditTrail.WriteAsync(
            AuditEventKind.AnswerProduced, approval.Request.CorrelationId, "reservation-recovery",
            "reserveSupportPackage", "interrupted-execution",
            recoveredCase.SupportExecutionRequiresReconciliation ? "reconciliation-required" :
                recoveredCase.Status == WelfareCaseStatus.VetDispatched ? "completed" : "not-executed",
            current.ComplaintHash, approvalRequestId: approval.Request.RequestId,
            cancellationToken: CancellationToken.None);
    }

    private sealed class ExecutionProgress
    {
        public WelfareCaseRecord? ExecutingCase { get; set; }
        public SupportReservationReceipt? Receipt { get; set; }
        public bool DecisionReleased { get; set; }
        public bool NonExecutionConfirmed { get; set; }
        public bool FinalStatePersisted { get; set; }
    }

    private static WelfareAssessmentEvent? ProjectAgentUpdate(
        AgentAssessmentUpdate update,
        AssessmentExecutionProvider executionProvider)
    {
        AssessmentEventKind? kind = update.Kind switch
        {
            AgentAssessmentUpdateKind.InvocationStarted => AssessmentEventKind.AgentStarted,
            AgentAssessmentUpdateKind.ToolCallStarted =>
                AssessmentEventKind.AgentToolCallStarted,
            AgentAssessmentUpdateKind.ApprovalRequested =>
                AssessmentEventKind.McpApprovalRequested,
            AgentAssessmentUpdateKind.ApprovalDecided =>
                AssessmentEventKind.McpApprovalDecided,
            AgentAssessmentUpdateKind.ToolResultReceived =>
                AssessmentEventKind.AgentToolResultReceived,
            AgentAssessmentUpdateKind.ResponseCompleted =>
                AssessmentEventKind.AgentResponseCompleted,
            AgentAssessmentUpdateKind.ResponseValidated =>
                AssessmentEventKind.AgentCompleted,
            _ => null
        };
        string message = (executionProvider, update.Kind) switch
        {
            (AssessmentExecutionProvider.LocalDeterministic, AgentAssessmentUpdateKind.InvocationStarted) =>
                "Local assessment started.",
            (AssessmentExecutionProvider.LocalDeterministic, AgentAssessmentUpdateKind.ResponseCompleted) =>
                "Local result produced; validating the typed assessment contract.",
            (AssessmentExecutionProvider.LocalDeterministic, AgentAssessmentUpdateKind.ResponseValidated) =>
                "Local result satisfies the typed assessment contract.",
            (AssessmentExecutionProvider.AzureFoundry, AgentAssessmentUpdateKind.InvocationStarted) =>
                "Azure Foundry agent invocation started.",
            _ => update.Message
        };

        return kind is null
            ? null
            : new WelfareAssessmentEvent(
                kind.Value,
                message,
                update.Timestamp,
                ExecutionProvider: executionProvider,
                ApprovalGranted: update.Approved);
    }

    private bool RequiresApproval(string toolName) =>
        toolPolicyProvider.Policy.ToolApprovals.SingleOrDefault(rule =>
            string.Equals(rule.ToolName, toolName, StringComparison.Ordinal))
        ?.RequiresApproval == true;

    private static string CreateIncidentReference(string caseId)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(caseId));
        uint numeric = BinaryPrimitives.ReadUInt32BigEndian(hash) % 100_000_000;
        return $"INC-{numeric:00000000}";
    }

    private static void ValidateDecisionCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.'))
        {
            throw new ArgumentException(
                "Decision code contains unsupported characters.",
                nameof(value));
        }
    }

    private static string? NormalizeOptionalIdentifier(string? value)
    {
        string? normalized = value?.Trim().ToUpperInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static void ValidateCommand(WelfareAssessmentCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.CaseId) ||
            command.CaseId.Length > 64 ||
            command.CaseId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_'))
        {
            throw new ArgumentException("Case ID contains unsupported characters.", nameof(command));
        }

        if (command.ReceivedAt == default)
        {
            throw new ArgumentException("ReceivedAt is required.", nameof(command));
        }

        if (command.OwnerObjectId == Guid.Empty)
        {
            throw new UnauthorizedAccessException("An authenticated case owner is required.");
        }

        if (!string.IsNullOrWhiteSpace(command.FarmReference) &&
            (command.FarmReference.Length > 64 ||
             command.FarmReference.Any(character =>
                 !char.IsAsciiLetterOrDigit(character) &&
                 character is not '-' and not '_')))
        {
            throw new ArgumentException("Area reference contains unsupported characters.", nameof(command));
        }
    }
}
