namespace GovernanceCouncil.Agents.Debate;

/// <summary>
/// Retries a model call on transient failures — HTTP 429 (throttle) or 5xx — with exponential
/// backoff. Low-quota / preview deployments (e.g. Grok 4.1 Fast) throttle or 500 under a debate's
/// burst of calls.
/// </summary>
internal static class ModelRetry
{
    public static async Task<T> OnTransient<T>(Func<Task<T>> call, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return await call(); }
            catch (System.ClientModel.ClientResultException ex) when ((ex.Status == 429 || ex.Status >= 500) && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
            }
        }
    }
}
