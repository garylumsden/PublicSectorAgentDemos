namespace GovernanceCouncil.Web;

/// <summary>Shared UI helpers for mapping domain values to design-system CSS classes.</summary>
public static class UiStyles
{
    /// <summary>Maps a stance/recommendation string to a <c>.gc-pill</c> modifier class.</summary>
    public static string StanceClass(string? value)
    {
        var v = (value ?? "").Trim().ToLowerInvariant();
        if (v.Contains("condition")) return "badge-conditions";
        if (v.Contains("support") || v.Contains("approve")) return "badge-support";
        if (v.Contains("oppose") || v.Contains("reject")) return "badge-oppose";
        if (v.Contains("defer")) return "badge-defer";
        if (v.Contains("abstain")) return "badge-abstain";
        return "badge-abstain";
    }
}
