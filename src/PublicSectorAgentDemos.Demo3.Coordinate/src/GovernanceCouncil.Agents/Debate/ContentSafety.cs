namespace GovernanceCouncil.Agents.Debate;

/// <summary>
/// Detects Azure AI content-safety (Responsible AI) blocks so the debate can treat them as a
/// non-response — a member simply skips/defers, and the Chair falls back to a clear "Defer"
/// assessment — instead of surfacing the generic "the Chair did not produce an Assessment" failure.
/// A block happens when a model's input or output is rated at/above the configured RAI policy's
/// blocking threshold; the SDK raises it as an HTTP 400 whose body names the content filter.
/// </summary>
internal static class ContentSafety
{
    /// <summary>
    /// Sentinel the Chair synthesis returns when its output was blocked by the content-safety policy,
    /// so the orchestrator can build a graceful fallback Assessment with clear user-facing feedback.
    /// </summary>
    public const string BlockedMarker = "__CONTENT_FILTERED__";

    private static readonly string[] Signals =
    [
        "content_filter",
        "content management policy",
        "responsibleaipolicy",
        "the response was filtered",
        "jailbreak"
    ];

    /// <summary>True when the exception (or any inner exception) is a content-safety / RAI policy block.</summary>
    public static bool IsContentFilterBlock(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var msg = e.Message;
            if (string.IsNullOrEmpty(msg)) continue;
            foreach (var signal in Signals)
                if (msg.Contains(signal, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    /// <summary>
    /// A short human label for what the filter flagged: the model's <c>input</c> (prompt) or its
    /// <c>output</c> (response), falling back to <c>content</c>. Used in user-facing messages.
    /// </summary>
    public static string Scope(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var msg = e.Message;
            if (string.IsNullOrEmpty(msg)) continue;
            if (msg.Contains("prompt", StringComparison.OrdinalIgnoreCase)) return "input";
            if (msg.Contains("response was filtered", StringComparison.OrdinalIgnoreCase) ||
                msg.Contains("completion", StringComparison.OrdinalIgnoreCase)) return "output";
        }
        return "content";
    }
}
