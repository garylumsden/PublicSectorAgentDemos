using Defra.Contracts.FloodSupport;
using Defra.Contracts.V1;

namespace Demo2.Web.Domain;

public static class FloodSupportApprovalGuard
{
    public static SupportPackageRequest Validate(
        WelfareCaseRecord caseRecord, WelfareApprovalRecord approval, DateTimeOffset now, bool requireDecision)
    {
        SupportPackageRequest request = ValidateCasePackage(caseRecord);
        if (approval.CaseId != caseRecord.CaseId || approval.CaseVersion != caseRecord.Version ||
            approval.Request.RequestId != caseRecord.ApprovalRequestId ||
            approval.Request.ToolName != "reserveSupportPackage" ||
            approval.Request.ArgumentNames is null ||
            !approval.Request.ArgumentNames.Order(StringComparer.Ordinal).SequenceEqual(
                FloodSupportCatalogue.ToArguments(request).Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            approval.SupportRequest != request || approval.PackageSnapshot is null ||
            !FloodSupportCatalogue.MatchesSnapshot(approval.PackageSnapshot, caseRecord.PackageSnapshot!) ||
            approval.Request.ExpiresAt <= now || approval.Request.RequestedAt > now ||
            approval.Request.ExpiresAt <= approval.Request.RequestedAt ||
            (requireDecision && (approval.Decision?.Outcome != ApprovalOutcome.Approved ||
                approval.Decision.DecidedByObjectId != caseRecord.OwnerObjectId ||
                approval.Decision.RequestId != approval.Request.RequestId ||
                approval.Decision.CorrelationId != approval.Request.CorrelationId ||
                approval.Decision.ToolName != approval.Request.ToolName ||
                approval.Decision.DecidedAt < approval.Request.RequestedAt || approval.Decision.DecidedAt > now)))
        {
            throw new InvalidOperationException("The support approval is missing, tampered, expired or stale.");
        }

        return request;
    }

    public static SupportPackageRequest ValidateCasePackage(WelfareCaseRecord caseRecord)
    {
        if (caseRecord.SupportRequest is not { } request || caseRecord.PackageSnapshot is not { } snapshot ||
            !FloodSupportCatalogue.ValidateRequest(request, caseRecord.FarmReference!, out SupportPackageDefinition? canonical) ||
            request.ActionRequestId != caseRecord.IssuedActionRequestId ||
            request.IncidentReference != caseRecord.IncidentReference ||
            !FloodSupportCatalogue.MatchesSnapshot(snapshot, canonical!))
        {
            throw new InvalidOperationException("The issued support request or canonical package snapshot is missing or invalid.");
        }

        return request;
    }

    public static void ValidateReceipt(SupportReservationReceipt receipt, SupportPackageRequest request)
    {
        if (!MatchesReceipt(receipt, request))
        {
            throw new InvalidOperationException("The reservation receipt does not match the exact approved support package.");
        }
    }

    public static bool MatchesReceipt(SupportReservationReceipt receipt, SupportPackageRequest request) =>
        receipt.Request == request && receipt.Status == "simulated" &&
        !string.IsNullOrWhiteSpace(receipt.ReservationReference) &&
        receipt.ReservationReference.StartsWith("SIM-RES-", StringComparison.Ordinal) &&
        receipt.RecordedAt != default && receipt.Package is not null &&
        FloodSupportCatalogue.ValidateRequest(request, request.AreaReference, out SupportPackageDefinition? canonical) &&
        FloodSupportCatalogue.MatchesSnapshot(receipt.Package, canonical!);
}
