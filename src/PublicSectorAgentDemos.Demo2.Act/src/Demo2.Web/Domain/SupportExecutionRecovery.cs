using Defra.Contracts.FloodSupport;

namespace Demo2.Web.Domain;

public static class SupportExecutionRecovery
{
    public static (WelfareCaseRecord Case, WelfareApprovalRecord Approval) Resolve(
        WelfareCaseRecord current,
        WelfareApprovalRecord approval,
        bool outcomeKnown,
        SupportReservationReceipt? receipt,
        DateTimeOffset now)
    {
        if (receipt is not null &&
            approval.SupportRequest is { } request &&
            FloodSupportApprovalGuard.MatchesReceipt(receipt, request))
        {
            return (
                current with
                {
                    Status = WelfareCaseStatus.VetDispatched,
                    SupportExecutionInProgress = false,
                    SupportExecutionRequiresReconciliation = false,
                    Summary = $"Reservation {receipt.ReservationReference} was recorded before the response ended.",
                    UpdatedAt = now
                },
                approval with { DispatchReference = receipt.ReservationReference, UpdatedAt = now });
        }

        if (outcomeKnown && receipt is null)
        {
            // Retain the original decision as history, but retire its active case binding.
            return (
                current with
                {
                    Status = WelfareCaseStatus.Assessed,
                    SupportExecutionInProgress = false,
                    SupportExecutionRequiresReconciliation = false,
                    ApprovalRequestId = null,
                    SupportRequest = null,
                    PackageSnapshot = null,
                    Summary = "The reservation did not execute. Submit a new assessment to request approval.",
                    UpdatedAt = now
                },
                approval);
        }

        return (
            current with
            {
                Status = WelfareCaseStatus.DispatchApproved,
                SupportExecutionInProgress = false,
                SupportExecutionRequiresReconciliation = true,
                Summary = "The reservation outcome is unknown. Reconcile the original action before changing this case.",
                UpdatedAt = now
            },
            approval);
    }
}
