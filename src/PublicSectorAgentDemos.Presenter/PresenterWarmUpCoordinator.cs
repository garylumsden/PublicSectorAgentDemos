namespace PublicSectorAgentDemos.Presenter;

public sealed record WarmUpRunSnapshot(
    string? RunId,
    string State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<WarmUpResult> Results);

public sealed class PresenterWarmUpCoordinator(
    PresenterWarmUpService warmUpService,
    PresenterSessionCatalog catalog,
    TimeProvider timeProvider)
{
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private WarmUpRunSnapshot _snapshot =
        new(null, "not-started", null, null, []);
    private Task? _currentRun;

    public WarmUpRunSnapshot Start()
    {
        lock (_gate)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (_currentRun is { IsCompleted: false } ||
                (_snapshot.CompletedAt is DateTimeOffset completedAt &&
                 now - completedAt < RepeatWindow))
            {
                return _snapshot;
            }

            _snapshot = new(Guid.NewGuid().ToString("N"), "running", now, null, []);
            _currentRun = RunAsync();
            return _snapshot;
        }
    }

    public WarmUpRunSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return _snapshot;
        }
    }

    public async Task WaitForCurrentRunAsync()
    {
        Task? current;
        lock (_gate)
        {
            current = _currentRun;
        }

        if (current is not null)
        {
            await current;
        }
    }

    private async Task RunAsync()
    {
        IReadOnlyList<WarmUpResult> results;
        try
        {
            results = await warmUpService.WarmAsync(catalog, CancellationToken.None);
        }
        catch (Exception)
        {
            results = catalog.AllSessions
                .Select(session => new WarmUpResult(
                    session.Id,
                    session.Title,
                    WarmUpOutcome.Failure,
                    "Warm-up failed.",
                    (catalog.Extras ?? []).Any(extra => extra.Id == session.Id)))
                .ToArray();
        }

        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                State = "completed",
                CompletedAt = timeProvider.GetUtcNow(),
                Results = results
            };
        }
    }
}
