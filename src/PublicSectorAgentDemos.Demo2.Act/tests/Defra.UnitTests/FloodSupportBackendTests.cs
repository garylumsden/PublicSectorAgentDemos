using System.Text.Json;
using Defra.Audit;
using Defra.Contracts.FloodSupport;
using Defra.Contracts.V1;
using Defra.Policy;
using Defra.Tools.Mcp;
using Demo2.Web.Agent;
using Demo2.Web.Components.UI;
using Demo2.Web.Configuration;
using Demo2.Web.Domain;
using Demo2.Web.Observability;
using Demo2.Web.Persistence;
using Demo2.Web.Services;
using Microsoft.Extensions.Options;

namespace Defra.UnitTests;

public sealed class FloodSupportBackendTests
{
    private static SupportPackageRequest Request(string packageId = "PKG-RIV-060", string? actionId = null) => new(
        actionId ?? "approval-33333333333333333333333333333333", "AREA-1001", "INC-2001", packageId, 1,
        "Temporary accommodation and transport are needed before existing accommodation reaches capacity.");

    [Theory]
    [InlineData("PKG-RIV-060", 60, 12, 2, 120, 4, 8100, 120)]
    [InlineData("PKG-RIV-030", 30, 6, 1, 60, 2, 4050, 150)]
    public void Catalogue_BindsExactResources(string id, int rooms, int accessibleRooms, int vehicles,
        int seats, int accessiblePlaces, int price, int unmet)
    {
        SupportPackageDefinition package = FloodSupportCatalogue.GetPackage(id);
        Assert.Equal(rooms, package.Rooms);
        Assert.Equal(accessibleRooms, package.AccessibleRooms);
        Assert.Equal(vehicles, package.Vehicles);
        Assert.Equal(seats, package.TransportSeats);
        Assert.Equal(accessiblePlaces, package.AccessibleTransportPlaces);
        Assert.Equal(price, package.EstimatedCostGbp);
        Assert.Equal(rooms * 95m + vehicles * 1200m, package.EstimatedCostGbp);
        Assert.Equal(24, package.DurationHours);
        Assert.Equal(unmet, package.UnmetHouseholds);
        Assert.Contains(package.Risks, risk => risk.Contains("source verification", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parser_AcceptsExactFlatAndNestedTypedArguments(bool nested)
    {
        IReadOnlyDictionary<string, object?> arguments = FloodSupportCatalogue.ToArguments(Request());
        if (nested) arguments = new Dictionary<string, object?> { ["request"] = arguments };
        Assert.True(FloodSupportCatalogue.TryParseApprovalArguments(arguments, out SupportPackageRequest? parsed));
        Assert.Equal(Request(), parsed);
    }

    [Fact]
    public void Parser_AcceptsNestedJsonObjectAndRejectsDuplicateKeys()
    {
        JsonElement json = JsonSerializer.SerializeToElement(FloodSupportCatalogue.ToArguments(Request()));
        Assert.True(FloodSupportCatalogue.TryParseApprovalArguments(
            new Dictionary<string, object?> { ["request"] = json }, out _));
        using JsonDocument duplicate = JsonDocument.Parse(json.GetRawText().Replace("\"packageVersion\":1", "\"packageVersion\":1,\"packageVersion\":1", StringComparison.Ordinal));
        Assert.False(FloodSupportCatalogue.TryParseApprovalArguments(
            new Dictionary<string, object?> { ["request"] = duplicate.RootElement }, out _));
    }

    [Theory]
    [InlineData("actionRequestId")]
    [InlineData("areaReference")]
    [InlineData("incidentReference")]
    [InlineData("packageId")]
    [InlineData("packageVersion")]
    [InlineData("justification")]
    [InlineData("reservationGeneration")]
    public void Parser_RejectsEveryMissingField(string field)
    {
        Dictionary<string, object?> arguments = new(FloodSupportCatalogue.ToArguments(Request()));
        Assert.True(arguments.Remove(field));
        Assert.False(FloodSupportCatalogue.TryParseApprovalArguments(arguments, out SupportPackageRequest? parsed));
        Assert.Null(parsed);
    }

    [Theory]
    [InlineData("rooms", 90)]
    [InlineData("approved", true)]
    [InlineData("packageVersion", "1")]
    [InlineData("packageVersion", 1.5)]
    [InlineData("packageVersion", 0)]
    [InlineData("packageId", "UNKNOWN")]
    [InlineData("areaReference", "AREA-1002")]
    [InlineData("AreaReference", "AREA-1001")]
    [InlineData("incidentReference", "INC-evil")]
    [InlineData("actionRequestId", "approval-not-issued")]
    [InlineData("justification", " ")]
    [InlineData("justification", null)]
    public void Parser_RejectsMalformedOrExtraFields(string key, object? value)
    {
        Dictionary<string, object?> arguments = new(FloodSupportCatalogue.ToArguments(Request())) { [key] = value };
        Assert.False(FloodSupportCatalogue.TryParseApprovalArguments(arguments, out _));
    }

    [Fact]
    public void Parser_RejectsOversizedJustificationAndScalarEnvelope()
    {
        Assert.False(FloodSupportCatalogue.TryParseApprovalArguments(
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Request"] = FloodSupportCatalogue.ToArguments(Request()) }, out _));
        Assert.False(FloodSupportCatalogue.ValidateRequest(Request() with { Justification = new string('x', 2001) }, "AREA-1001", out _));
        Assert.False(FloodSupportCatalogue.ValidateRequest(Request(), "AREA-1002", out _));
        Assert.False(FloodSupportCatalogue.TryParseApprovalArguments(new Dictionary<string, object?> { ["request"] = "invalid" }, out _));
    }

    [Fact]
    public void Simulator_RejectsOvercapacityWithoutPartialCommitAndReplaysExactReceipt()
    {
        FloodSupportReservationSimulator simulator = new();
        SupportPackageRequest small = Request("PKG-RIV-030");
        SupportReservationReceipt first = simulator.Reserve(small, DateTimeOffset.UtcNow);
        Assert.Same(first, simulator.Reserve(small, DateTimeOffset.UtcNow));
        SupportReservationException capacity = Assert.Throws<SupportReservationException>(() =>
            simulator.Reserve(Request(actionId: "approval-44444444444444444444444444444444"), DateTimeOffset.UtcNow));
        Assert.Equal("capacity.support_unavailable", capacity.Code);
        Assert.Single(simulator.Reservations);
        _ = simulator.Reserve(small with { ActionRequestId = "approval-55555555555555555555555555555555" }, DateTimeOffset.UtcNow);
        Assert.Equal(60, simulator.Reservations.Sum(receipt => receipt.Package.Rooms));
        Assert.Equal(2, simulator.Reservations.Sum(receipt => receipt.Package.Vehicles));
        SupportReservationException conflict = Assert.Throws<SupportReservationException>(() =>
            simulator.Reserve(small with { Justification = "Changed explanation" }, DateTimeOffset.UtcNow));
        Assert.Equal("toolbox.idempotency_conflict", conflict.Code);
    }

    [Fact]
    public void ResetReservations_RestoresCapacityAndInvalidatesEarlierRequests()
    {
        FloodSupportReservationSimulator simulator = new();
        SupportPackageRequest original = Request();
        _ = simulator.Reserve(original, DateTimeOffset.UtcNow);
        ReservationInventoryState reset = simulator.ResetReservations();
        Assert.Equal(0, reset.ReservationCount);
        Assert.Empty(simulator.Reservations);
        Assert.Equal("reservation.generation_changed", Assert.Throws<SupportReservationException>(
            () => simulator.Reserve(original, DateTimeOffset.UtcNow)).Code);
        SupportPackageRequest pendingBeforeReset = original with { ActionRequestId = "approval-44444444444444444444444444444444" };
        Assert.Throws<SupportReservationException>(() => simulator.Reserve(pendingBeforeReset, DateTimeOffset.UtcNow));
        SupportReservationReceipt fresh = simulator.Reserve(pendingBeforeReset with
        {
            ReservationGeneration = reset.Generation
        }, DateTimeOffset.UtcNow);
        Assert.Equal(60, fresh.Package.Rooms);
        Assert.Single(simulator.Reservations);
    }

    [Fact]
    public async Task ResetReservations_PreservesCaseHistory()
    {
        Harness harness = new();
        WelfareAssessmentOutcome completed = await harness.AssessAsync(
            (_, _) => Task.FromResult(new AgentMcpApprovalDecision(true, "operator-approved")));
        _ = await harness.Admin.ResetReservationsAsync();
        Assert.Empty(harness.Simulator.Reservations);
        Assert.Equal(completed.Case, await harness.Store.GetAsync(harness.CaseId));
        Assert.Single(await harness.Store.ListForCaseAsync(harness.CaseId));
    }

    [Fact]
    public async Task ExplicitRemoteCapacityRejection_RetiresTheActiveApproval()
    {
        Harness harness = new(new RejectedContinuationAgent(), azure: true);
        await Assert.ThrowsAsync<SupportReservationNotExecutedException>(() => harness.AssessAsync(
            (_, _) => Task.FromResult(new AgentMcpApprovalDecision(true, "operator-approved"))));
        WelfareCaseRecord current = (await harness.Store.GetAsync(harness.CaseId))!;
        Assert.False(current.SupportExecutionInProgress);
        Assert.False(current.SupportExecutionRequiresReconciliation);
        Assert.Null(current.ApprovalRequestId);
        Assert.Empty(harness.Simulator.Reservations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LocalSimulation_UsesSameApprovalHandlerAndPersistsExactRequest(bool approve)
    {
        Harness harness = new();
        AgentMcpApprovalRequest? presented = null;
        WelfareAssessmentOutcome outcome = await harness.AssessAsync((request, _) =>
        {
            presented = request;
            Assert.NotNull(request.ExpiresAt);
            Assert.True(FloodSupportCatalogue.TryParseApprovalArguments(request.Arguments, out SupportPackageRequest? parsed));
            Assert.NotNull(parsed);
            return Task.FromResult(new AgentMcpApprovalDecision(approve, "operator-reviewed"));
        });
        Assert.NotNull(presented);
        Assert.Equal(approve ? WelfareCaseStatus.VetDispatched : WelfareCaseStatus.DispatchDenied, outcome.Case.Status);
        WelfareApprovalRecord approval = Assert.IsType<WelfareApprovalRecord>(outcome.Approval);
        Assert.Equal(outcome.Case.SupportRequest, approval.SupportRequest);
        Assert.Equal(outcome.Case.IssuedActionRequestId, approval.SupportRequest!.ActionRequestId);
        Assert.Equal(outcome.Case.IncidentReference, approval.SupportRequest.IncidentReference);
        Assert.True(FloodSupportCatalogue.MatchesSnapshot(approval.PackageSnapshot!, FloodSupportCatalogue.GetPackage("PKG-RIV-060")));
        Assert.Equal(approve ? 1 : 0, harness.Simulator.Reservations.Count);
        Assert.Contains("UNMET: 120", outcome.Case.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("fictional", outcome.Case.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(outcome.Evidence, evidence => evidence.Contains("90-room", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("action")]
    [InlineData("incident")]
    [InlineData("area")]
    [InlineData("version")]
    [InlineData("extra")]
    public async Task Approval_RejectsMismatchedIssuedArgumentsBeforeShowingModal(string mismatch)
    {
        Harness harness = new(new RequestAgent(request =>
        {
            Dictionary<string, object?> args = new(FloodSupportCatalogue.ToArguments(Request() with
            {
                ActionRequestId = request.DispatchActionRequestId,
                IncidentReference = request.IncidentReference
            }));
            switch (mismatch)
            {
                case "action": args["actionRequestId"] = Request().ActionRequestId; break;
                case "incident": args["incidentReference"] = "INC-9999"; break;
                case "area": args["areaReference"] = "AREA-1002"; break;
                case "version": args["packageVersion"] = 2; break;
                case "extra": args["rooms"] = 90; break;
            }
            return args;
        }));
        bool modalShown = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.AssessAsync((_, _) =>
        {
            modalShown = true;
            return Task.FromResult(new AgentMcpApprovalDecision(true, "operator-reviewed"));
        }));
        Assert.False(modalShown);
        Assert.Empty(await harness.Store.ListPendingAsync());
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("snapshot")]
    [InlineData("request")]
    [InlineData("owner")]
    [InlineData("override")]
    [InlineData("missing")]
    public async Task Approval_RejectsChangedPendingStateBeforeExecuting(string change)
    {
        Harness harness = new();
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.AssessAsync(async (request, _) =>
        {
            WelfareCaseRecord current = (await harness.Store.GetAsync(harness.CaseId))!;
            WelfareApprovalRecord approval = (await harness.Store.GetAsync(harness.CaseId, request.RequestId))!;
            if (change == "expiry") harness.Time.Now = request.ExpiresAt!.Value;
            if (change == "snapshot") await harness.Store.UpsertAsync(approval with { PackageSnapshot = approval.PackageSnapshot! with { Rooms = 90 } });
            if (change == "request") await harness.Store.UpsertAsync(approval with { SupportRequest = approval.SupportRequest! with { PackageId = "PKG-RIV-030" } });
            if (change == "owner") await harness.Store.UpsertAsync(current with { OwnerObjectId = Guid.NewGuid() });
            if (change == "override") await harness.Admin.OverrideUrgencyAsync(harness.CaseId, WelfareUrgency.Medium, "source-reviewed");
            if (change == "missing") await harness.Store.UpsertAsync(approval with { SupportRequest = null });
            return new(true, "operator-reviewed");
        }));
        Assert.Empty(harness.Simulator.Reservations);
    }

    [Fact]
    public async Task Cancellation_RecordsDenialAndNeverReserves()
    {
        Harness harness = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => harness.AssessAsync((_, _) =>
            Task.FromCanceled<AgentMcpApprovalDecision>(new CancellationToken(true))));
        WelfareApprovalRecord approval = Assert.Single(await harness.Store.ListForCaseAsync(harness.CaseId));
        Assert.Equal(ApprovalOutcome.Denied, approval.Decision!.Outcome);
        Assert.Empty(harness.Simulator.Reservations);
    }

    [Theory]
    [InlineData("snapshot")]
    [InlineData("request")]
    [InlineData("expiry")]
    [InlineData("decision")]
    [InlineData("names")]
    public void ExecutionGuard_RejectsTamperedApprovedState(string change)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        SupportPackageRequest request = Request();
        SupportPackageDefinition package = FloodSupportCatalogue.GetPackage(request.PackageId);
        WelfareCaseRecord record = new("FLOOD-EXEC", Demo2SyntheticOperator.ObjectId, request.AreaReference, now, "hash",
            WelfareUrgency.High, WelfareRoute.PriorityInspectorReview, WelfareCaseStatus.DispatchApproved,
            "Approved flood support", [], false, "response", "mcp-approval", 1, now)
        {
            IssuedActionRequestId = request.ActionRequestId,
            IncidentReference = request.IncidentReference,
            SupportRequest = request,
            PackageSnapshot = package
        };
        ApprovalRequest approvalRequest = new("mcp-approval", new("flood-test"), "reserveSupportPackage",
            FloodSupportCatalogue.ToArguments(request).Keys.ToArray(), "human-review", now, now.AddMinutes(30));
        WelfareApprovalRecord approval = new(record.CaseId, record.Version, approvalRequest,
            new(approvalRequest.RequestId, approvalRequest.CorrelationId, approvalRequest.ToolName, ApprovalOutcome.Approved,
                record.OwnerObjectId, "operator-reviewed", now), null, now)
        { SupportRequest = request, PackageSnapshot = package };
        Assert.Equal(request, FloodSupportApprovalGuard.Validate(record, approval, now, true));
        approval = change switch
        {
            "snapshot" => approval with { PackageSnapshot = package with { Rooms = 90 } },
            "request" => approval with { SupportRequest = request with { PackageId = "PKG-RIV-030" } },
            "expiry" => approval with { Request = approvalRequest with { ExpiresAt = now } },
            "decision" => approval with { Decision = approval.Decision! with { Outcome = ApprovalOutcome.Denied } },
            "names" => approval with { Request = approvalRequest with { ArgumentNames = ["rooms"] } },
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        Assert.Throws<InvalidOperationException>(() => FloodSupportApprovalGuard.Validate(record, approval, now, true));
    }

    [Fact]
    public void PersistedPackageSnapshot_RoundTripsWithoutLosingApprovalBinding()
    {
        SupportPackageDefinition canonical = FloodSupportCatalogue.GetPackage("PKG-RIV-060");
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        SupportPackageDefinition restored = JsonSerializer.Deserialize<SupportPackageDefinition>(
            JsonSerializer.Serialize(canonical, options), options)!;
        SupportPackageRequest restoredRequest = JsonSerializer.Deserialize<SupportPackageRequest>(
            JsonSerializer.Serialize(Request(), options), options)!;
        Assert.True(FloodSupportCatalogue.MatchesSnapshot(restored, canonical));
        Assert.Equal(Request(), restoredRequest);
        Assert.False(FloodSupportCatalogue.MatchesSnapshot(restored with { EstimatedCostGbp = 1m }, canonical));
        Assert.False(FloodSupportCatalogue.MatchesSnapshot(restored with { Risks = [] }, canonical));
    }

    [Fact]
    public async Task StandardReviewExample_RemainsMediumWithoutReservationApproval()
    {
        WelfareScenario scenario = WelfareScenarioCatalogue.GetRequired("standard");
        Harness harness = new();
        bool approvalRequested = false;
        WelfareAssessmentOutcome outcome = await harness.AssessAsync((_, _) =>
        {
            approvalRequested = true;
            return Task.FromResult(new AgentMcpApprovalDecision(false, "unexpected-approval"));
        }, scenario.FarmReference, scenario.ComplaintText);

        Assert.Equal(WelfareUrgency.Medium, outcome.Case.Urgency);
        Assert.Equal(WelfareRoute.StandardInspectorReview, outcome.Case.Route);
        Assert.Equal(WelfareCaseStatus.Assessed, outcome.Case.Status);
        Assert.False(approvalRequested);
        Assert.Null(outcome.Approval);
        Assert.Null(outcome.Case.SupportRequest);
        Assert.Empty(harness.Simulator.Reservations);
        Assert.Contains("standard coordination review", outcome.Case.Summary, StringComparison.Ordinal);
        Assert.Contains("No reservation was requested or made", outcome.Case.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("UNMET: 0", outcome.Case.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StandardReviewExample_PreservesAnExistingHighUrgencyFloor()
    {
        WelfareScenario scenario = WelfareScenarioCatalogue.GetRequired("standard");
        Harness harness = new();
        int approvalRequests = 0;
        Task<AgentMcpApprovalDecision> RejectApproval(AgentMcpApprovalRequest request, CancellationToken token)
        {
            approvalRequests++;
            return Task.FromResult(new AgentMcpApprovalDecision(false, "operator-rejected"));
        }

        WelfareAssessmentOutcome emergency = await harness.AssessAsync(RejectApproval,
            scenario.FarmReference, "Flood-displacement requires urgent accommodation and transport support.");
        WelfareAssessmentOutcome continuation = await harness.AssessAsync(RejectApproval,
            scenario.FarmReference, scenario.ComplaintText);

        Assert.Equal(WelfareUrgency.High, emergency.Case.Urgency);
        Assert.Equal(WelfareUrgency.High, continuation.Case.Urgency);
        Assert.Equal(emergency.Case.Route, continuation.Case.Route);
        Assert.Equal(2, continuation.Case.Version);
        Assert.Equal(2, approvalRequests);
        Assert.Equal(WelfareCaseStatus.DispatchDenied, continuation.Case.Status);
        Assert.Empty(harness.Simulator.Reservations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedLocalExecution_RecoversTheActualReservationOutcome(bool reserved)
    {
        Harness harness = new();
        using CancellationTokenSource cancellation = new();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (WelfareAssessmentEvent item in harness.Events(
                               (_, _) => Task.FromResult(new AgentMcpApprovalDecision(true, "operator-approved")),
                               cancellation.Token))
            {
                if ((!reserved && item.Kind == AssessmentEventKind.McpApprovalDecided) ||
                    (reserved && item.Kind == AssessmentEventKind.AgentToolResultReceived &&
                     harness.Simulator.Reservations.Count == 1))
                {
                    cancellation.Cancel();
                }
            }
        });

        WelfareCaseRecord current = (await harness.Store.GetAsync(harness.CaseId))!;
        Assert.False(current.SupportExecutionInProgress);
        Assert.False(current.SupportExecutionRequiresReconciliation);
        Assert.Equal(reserved ? 1 : 0, harness.Simulator.Reservations.Count);
        if (reserved)
        {
            Assert.Equal(WelfareCaseStatus.VetDispatched, current.Status);
            WelfareApprovalRecord approval = (await harness.Store.GetAsync(harness.CaseId, current.ApprovalRequestId!))!;
            Assert.Equal(harness.Simulator.Reservations[0].ReservationReference, approval.DispatchReference);
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Admin.DispatchVetAsync(harness.CaseId));
        }
        else
        {
            Assert.Null(current.ApprovalRequestId);
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Admin.DispatchVetAsync(harness.CaseId));
            WelfareAssessmentOutcome retry = await harness.AssessAsync(
                (_, _) => Task.FromResult(new AgentMcpApprovalDecision(false, "new-request-rejected")));
            Assert.Equal(2, retry.Case.Version);
            Assert.Equal(WelfareCaseStatus.DispatchDenied, retry.Case.Status);
        }
    }

    [Fact]
    public async Task DisposingAnApprovedIterator_RetiresAnUnexecutedLocalRequest()
    {
        Harness harness = new();
        await using (IAsyncEnumerator<WelfareAssessmentEvent> iterator = harness.Events(
                         (_, _) => Task.FromResult(new AgentMcpApprovalDecision(true, "operator-approved")))
                     .GetAsyncEnumerator())
        {
            while (await iterator.MoveNextAsync())
            {
                if (iterator.Current.Kind == AssessmentEventKind.McpApprovalDecided)
                {
                    break;
                }
            }
        }

        WelfareCaseRecord current = (await harness.Store.GetAsync(harness.CaseId))!;
        Assert.False(current.SupportExecutionInProgress);
        Assert.False(current.SupportExecutionRequiresReconciliation);
        Assert.Null(current.ApprovalRequestId);
        Assert.Empty(harness.Simulator.Reservations);
    }

    [Fact]
    public async Task CapacityFailure_RetiresTheRequestWithoutBlockingAnotherAssessment()
    {
        Harness harness = new();
        _ = harness.Simulator.Reserve(Request(), harness.Time.Now);
        await Assert.ThrowsAsync<SupportReservationException>(() => harness.AssessAsync(
            (_, _) => Task.FromResult(new AgentMcpApprovalDecision(true, "operator-approved"))));
        WelfareCaseRecord current = (await harness.Store.GetAsync(harness.CaseId))!;
        Assert.False(current.SupportExecutionInProgress);
        Assert.False(current.SupportExecutionRequiresReconciliation);
        Assert.Null(current.ApprovalRequestId);
        Assert.Single(harness.Simulator.Reservations);
        _ = await harness.AssessAsync(
            (_, _) => Task.FromResult(new AgentMcpApprovalDecision(false, "new-request-rejected")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownOutcome_RetainsTheExactActionForAuthorisedReconciliation(bool expired)
    {
        Harness harness = new(new FailedContinuationAgent(), azure: true);
        await Assert.ThrowsAsync<HttpRequestException>(() => harness.AssessAsync(
            (_, _) => Task.FromResult(new AgentMcpApprovalDecision(true, "operator-approved"))));
        WelfareCaseRecord current = (await harness.Store.GetAsync(harness.CaseId))!;
        Assert.False(current.SupportExecutionInProgress);
        Assert.True(current.SupportExecutionRequiresReconciliation);
        Assert.NotNull(current.SupportRequest);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.AssessAsync(
            (_, _) => Task.FromResult(new AgentMcpApprovalDecision(false, "not-permitted"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Admin.OverrideUrgencyAsync(
            harness.CaseId, WelfareUrgency.Low, "not-permitted"));
        WelfareApprovalRecord approval = (await harness.Store.GetAsync(harness.CaseId, current.ApprovalRequestId!))!;
        if (expired)
        {
            harness.Time.Now = approval.Request.ExpiresAt;
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Admin.DispatchVetAsync(harness.CaseId));
            Assert.Empty(harness.Simulator.Reservations);
            Assert.True((await harness.Store.GetAsync(harness.CaseId))!.SupportExecutionRequiresReconciliation);
        }
        else
        {
            _ = await harness.Admin.DispatchVetAsync(harness.CaseId);
            SupportReservationReceipt receipt = Assert.Single(harness.Simulator.Reservations);
            Assert.Equal(current.SupportRequest, receipt.Request);
            Assert.False((await harness.Store.GetAsync(harness.CaseId))!.SupportExecutionRequiresReconciliation);
            Assert.Equal(WelfareCaseStatus.VetDispatched, (await harness.Store.GetAsync(harness.CaseId))!.Status);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task InterruptedReconciliation_PreservesTheOriginalUnknownOutcome(bool localToolbox, bool cancelled)
    {
        Harness harness = new(new FailedContinuationAgent(), azure: true);
        await Assert.ThrowsAsync<HttpRequestException>(() => harness.AssessAsync(
            (_, _) => Task.FromResult(new AgentMcpApprovalDecision(true, "operator-approved"))));
        WelfareCaseRecord original = (await harness.Store.GetAsync(harness.CaseId))!;
        Demo2Options options = new();
        NeverCalledToolbox remote = new();
        IWelfareToolboxClient toolbox = localToolbox
            ? new InMemoryWelfareToolboxClient(Options.Create(options), harness.Time, harness.Simulator)
            : remote;
        Demo2AdminWorkflowService interruptedAdmin = new(
            new InterruptedExecutionStore(harness.Store, cancelled), harness.Store, harness.Store, toolbox,
            new ToolPolicy(), Options.Create(options),
            new Demo2AuditTrail(new NullAudit(), new FixtureRuleProvider(), harness.Time), harness.Time);
        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interruptedAdmin.DispatchVetAsync(harness.CaseId));
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => interruptedAdmin.DispatchVetAsync(harness.CaseId));
        }

        WelfareCaseRecord preserved = (await harness.Store.GetAsync(harness.CaseId))!;
        Assert.True(preserved.SupportExecutionRequiresReconciliation);
        Assert.False(preserved.SupportExecutionInProgress);
        Assert.Equal(original.ApprovalRequestId, preserved.ApprovalRequestId);
        Assert.Equal(original.SupportRequest, preserved.SupportRequest);
        Assert.Equal(original.IssuedActionRequestId, preserved.IssuedActionRequestId);
        Assert.Empty(harness.Simulator.Reservations);
        Assert.Equal(0, remote.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.AssessAsync(
            (_, _) => Task.FromResult(new AgentMcpApprovalDecision(false, "not-permitted"))));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StaleReassessment_CannotOverwriteExecutionOrCompletion(bool completed, bool withoutApproval)
    {
        Harness harness = new();
        TaskCompletionSource pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<AgentMcpApprovalDecision> decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource approved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource continueExecution = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task first = Task.Run(async () =>
        {
            await foreach (WelfareAssessmentEvent item in harness.Events((_, _) =>
                           {
                               pending.TrySetResult();
                               return decision.Task;
                           }))
            {
                if (item.Kind == AssessmentEventKind.McpApprovalDecided)
                {
                    approved.TrySetResult();
                    await continueExecution.Task.WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
        });
        try
        {
            await pending.Task.WaitAsync(TimeSpan.FromSeconds(10));
            bool secondModalShown = false;
            await using IAsyncEnumerator<WelfareAssessmentEvent> second = harness.Events((_, _) =>
                {
                    secondModalShown = true;
                    return Task.FromResult(new AgentMcpApprovalDecision(false, "second-rejected"));
                }, areaReference: withoutApproval ? null : "AREA-1001").GetAsyncEnumerator();
            Assert.True(await second.MoveNextAsync());
            Assert.Equal(AssessmentEventKind.Accepted, second.Current.Kind);
            decision.TrySetResult(new(true, "first-approved"));
            await approved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (completed)
            {
                continueExecution.TrySetResult();
                await first.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                while (await second.MoveNextAsync()) { }
            });
            Assert.False(secondModalShown);
        }
        finally
        {
            decision.TrySetResult(new(false, "test-cleanup"));
            continueExecution.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
        }

        WelfareCaseRecord current = (await harness.Store.GetAsync(harness.CaseId))!;
        Assert.Equal(1, current.Version);
        Assert.Equal(WelfareCaseStatus.VetDispatched, current.Status);
        Assert.Single(harness.Simulator.Reservations);
        Assert.Equal(harness.Simulator.Reservations[0].Request.ActionRequestId, current.IssuedActionRequestId);
    }

    private sealed class Harness
    {
        public string CaseId { get; } = $"FLOOD-{Guid.NewGuid():N}";
        public InMemoryDemo2StateStore Store { get; } = new();
        public FloodSupportReservationSimulator Simulator { get; } = new();
        public MutableTime Time { get; } = new();
        public Demo2AdminWorkflowService Admin { get; }
        private readonly WelfareAssessmentService _service;

        public Harness(IWelfareAgentClient? agent = null, bool azure = false)
        {
            Demo2Options options = new();
            if (azure) options.Agent.Mode = Demo2AgentModes.Azure;
            FixtureRuleProvider rules = new();
            ToolPolicy policy = new();
            Demo2AuditTrail audit = new(new NullAudit(), rules, Time);
            _service = new(new ComplaintInputGuard(policy, Options.Create(options)), new(rules),
                agent ?? new SyntheticContractWelfareAgentClient(Simulator), Store, Store, Store, policy,
                audit, Options.Create(options), Time);
            Admin = new(Store, Store, Store, new InMemoryWelfareToolboxClient(Options.Create(options), Time, Simulator),
                policy, Options.Create(options), audit, Time);
        }

        public IAsyncEnumerable<WelfareAssessmentEvent> Events(
            AgentMcpApprovalHandler handler,
            CancellationToken cancellationToken = default,
            string? areaReference = "AREA-1001",
            string situation = "Riverton flood-displacement: accommodation reaches capacity at 18:00.") =>
            _service.AssessAsync(new(CaseId, areaReference, situation, Time.Now, Demo2SyntheticOperator.ObjectId),
                cancellationToken, handler);

        public async Task<WelfareAssessmentOutcome> AssessAsync(
            AgentMcpApprovalHandler handler,
            string areaReference = "AREA-1001",
            string situation = "Riverton flood-displacement: accommodation reaches capacity at 18:00.")
        {
            WelfareAssessmentOutcome? result = null;
            await foreach (WelfareAssessmentEvent item in _service.AssessAsync(new(CaseId, areaReference,
                situation, Time.Now, Demo2SyntheticOperator.ObjectId),
                approvalHandler: handler)) result = item.Outcome ?? result;
            return Assert.IsType<WelfareAssessmentOutcome>(result);
        }
    }

    private sealed class InterruptedExecutionStore(IWelfareCaseStore inner, bool cancelled) : IWelfareCaseStore
    {
        public Task<WelfareCaseRecord?> GetAsync(string caseId, CancellationToken cancellationToken = default) =>
            inner.GetAsync(caseId, cancellationToken);

        public Task<IReadOnlyList<WelfareCaseRecord>> ListAsync(CancellationToken cancellationToken = default) =>
            inner.ListAsync(cancellationToken);

        public Task UpsertAsync(WelfareCaseRecord record, CancellationToken cancellationToken = default) =>
            record.SupportExecutionInProgress
                ? cancelled
                    ? Task.FromCanceled(new CancellationToken(true))
                    : Task.FromException(new IOException("The execution-state write failed."))
                : inner.UpsertAsync(record, cancellationToken);
    }

    private sealed class NeverCalledToolbox : IWelfareToolboxClient
    {
        public int Calls { get; private set; }

        public Task<VetDispatchReceipt> DispatchVetAsync(WelfareCaseRecord caseRecord, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("Execution must not start after the state write fails.");
        }
    }

    private sealed class FailedContinuationAgent : IWelfareAgentClient
    {
        public async Task<AgentAssessment> AssessAsync(AgentAssessmentRequest request,
            AgentMcpApprovalHandler? approvalHandler = null, CancellationToken cancellationToken = default)
        {
            SupportPackageRequest package = Request() with
            {
                ActionRequestId = request.DispatchActionRequestId,
                IncidentReference = request.IncidentReference
            };
            await approvalHandler!(new("remote-request", "reserveSupportPackage",
                FloodSupportCatalogue.ToArguments(package), [], DateTimeOffset.UtcNow), cancellationToken);
            throw new HttpRequestException("The remote continuation ended without a confirmed outcome.");
        }
    }

    private sealed class RejectedContinuationAgent : IWelfareAgentClient
    {
        public async Task<AgentAssessment> AssessAsync(AgentAssessmentRequest request,
            AgentMcpApprovalHandler? approvalHandler = null, CancellationToken cancellationToken = default)
        {
            SupportPackageRequest package = Request() with
            {
                ActionRequestId = request.DispatchActionRequestId,
                IncidentReference = request.IncidentReference
            };
            await approvalHandler!(new("rejected-request", "reserveSupportPackage",
                FloodSupportCatalogue.ToArguments(package), [], DateTimeOffset.UtcNow), cancellationToken);
            throw new SupportReservationNotExecutedException(package.ActionRequestId);
        }
    }

    private sealed class RequestAgent(Func<AgentAssessmentRequest, IReadOnlyDictionary<string, object?>> arguments) : IWelfareAgentClient
    {
        public async Task<AgentAssessment> AssessAsync(AgentAssessmentRequest request, AgentMcpApprovalHandler? approvalHandler = null,
            CancellationToken cancellationToken = default)
        {
            await approvalHandler!(new("mcp-test", "reserveSupportPackage", arguments(request), [], DateTimeOffset.UtcNow), cancellationToken);
            throw new InvalidOperationException("Invalid arguments must not reach continuation.");
        }
    }

    private sealed class FixtureRuleProvider : IWelfareRuleProvider
    {
        public WelfareRuleSet Rules { get; } = new("1.0.0", "flood-support-tests", true, 7, 2, 90, 60, 30,
            ["flood-displacement", "accommodation reaches capacity"], ["reserveSupportPackage"], false);
    }

    private sealed class ToolPolicy : IDemo2ToolPolicyProvider
    {
        public PolicyConfiguration Policy { get; } = McpServicePolicy.CreateDefault();
    }

    private sealed class MutableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class NullAudit : IAppendOnlyAuditWriter
    {
        public ValueTask AppendAsync(SanitizedAuditEvent auditEvent, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
