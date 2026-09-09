using System.Collections.Concurrent;

namespace Demo2.Web.Domain;

// Demo processes share these gates between assessment continuation and admin actions.
public static class Demo2CaseTransitions
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public static bool MatchesSnapshot(WelfareCaseRecord? current, WelfareCaseRecord? expected) =>
        current is null || expected is null
            ? current is null && expected is null
            : current.CaseId == expected.CaseId &&
              current.OwnerObjectId == expected.OwnerObjectId &&
              current.Version == expected.Version &&
              current.Status == expected.Status &&
              current.ApprovalRequestId == expected.ApprovalRequestId &&
              current.IssuedActionRequestId == expected.IssuedActionRequestId &&
              current.SupportExecutionInProgress == expected.SupportExecutionInProgress &&
              current.SupportExecutionRequiresReconciliation == expected.SupportExecutionRequiresReconciliation &&
              current.UpdatedAt == expected.UpdatedAt;

    public static async Task<IDisposable> EnterAsync(string caseId, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = Gates.GetOrAdd(caseId, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken);
        return new Lease(gate);
    }

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
