using System.Security.Cryptography;

namespace PublicSectorAgentDemos.Demo1.Web;

public sealed class ComparisonSnapshot
{
    public required string Id { get; init; }
    public required string Owner { get; init; }
    public required string Prompt { get; init; }
    public required AgentAnswer Foundation { get; init; }
    public required AgentAnswer Ground { get; init; }
    public required AssessmentReference Reference { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public QualityAssessment? Assessment { get; set; }
}

public sealed class ComparisonSnapshotStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, ComparisonSnapshot> snapshots = new(StringComparer.Ordinal);

    public static string CreateId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public ComparisonSnapshot Add(string id, string owner, string prompt, AgentAnswer foundation, AgentAnswer ground, AssessmentReference reference)
    {
        lock (gate)
        {
            Prune();
            while (snapshots.Count >= DemoLimits.CompletedSnapshots)
            {
                string oldest = snapshots.Values.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id, StringComparer.Ordinal).First().Id;
                snapshots.Remove(oldest);
            }

            ComparisonSnapshot snapshot = new()
            {
                Id = id,
                Owner = owner,
                Prompt = prompt,
                Foundation = foundation,
                Ground = ground,
                Reference = reference,
                CreatedAt = DateTimeOffset.UtcNow
            };
            snapshots.Add(snapshot.Id, snapshot);
            return snapshot;
        }
    }

    public bool TryGet(string id, string owner, out ComparisonSnapshot? snapshot)
    {
        lock (gate)
        {
            Prune();
            if (snapshots.TryGetValue(id, out snapshot) && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(snapshot.Owner), System.Text.Encoding.UTF8.GetBytes(owner)))
            {
                return true;
            }
            snapshot = null;
            return false;
        }
    }

    private void Prune()
    {
        DateTimeOffset cutoff = DateTimeOffset.UtcNow - DemoLimits.SnapshotLifetime;
        foreach (string id in snapshots.Where(item => item.Value.CreatedAt < cutoff).Select(item => item.Key).ToArray())
        {
            snapshots.Remove(id);
        }
    }

    public static string GetOwner(HttpRequest request)
    {
        if (!request.Cookies.TryGetValue("Demo1.Antiforgery", out string? cookie) || string.IsNullOrWhiteSpace(cookie))
        {
            throw new InvalidOperationException("The browser session cookie is unavailable.");
        }
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(cookie))).ToLowerInvariant();
    }
}
