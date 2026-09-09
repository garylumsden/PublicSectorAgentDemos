namespace GovernanceCouncil.Agents.Debate;

using System.Text.Json;

/// <summary>
/// Parses the small JSON payloads exchanged during a debate: a member's hand-raise <see cref="Bid"/>,
/// the moderator's next-speaker selection, and a member's spoken { summary, detail }. All helpers are
/// defensive — a malformed or non-JSON response yields a safe default rather than throwing.
/// </summary>
internal static class ContributionParser
{
    public sealed record Bid(bool Speak, string Reason, int Urgency);

    public static Bid ParseBid(string? text)
    {
        var json = ExtractJsonObject(text);
        if (json is null) return new Bid(false, "no bid", 1);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var speak = root.TryGetProperty("speak", out var s) &&
                        (s.ValueKind == JsonValueKind.True ||
                         (s.ValueKind == JsonValueKind.String && bool.TryParse(s.GetString(), out var b) && b));
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            var urgency = root.TryGetProperty("urgency", out var u) && u.TryGetInt32(out var ui) ? Math.Clamp(ui, 1, 5) : 1;
            if (reason.Length > 140) reason = reason[..140];
            return new Bid(speak, reason, urgency);
        }
        catch (JsonException)
        {
            return new Bid(false, "parse error", 1);
        }
    }

    public static (string? AgentName, string Reason) ParseSelection(string? text)
    {
        var json = ExtractJsonObject(text);
        if (json is null) return (null, "");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var name = root.TryGetProperty("agentName", out var n) ? n.GetString() : null;
            var reason = root.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
            return (name, reason);
        }
        catch (JsonException)
        {
            return (null, "");
        }
    }

    public static bool TryParseSpoken(string? text, out string detail)
    {
        detail = "";
        var json = ExtractJsonObject(text);
        if (json is null) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? "" : "";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the text looks like a member's bid-format JSON ({"speak":..,"reason":..,"urgency":..})
    /// rather than a real prose contribution — used to discard mis-formatted model output.
    /// </summary>
    public static bool LooksLikeBidJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('{')) return false;

        if (trimmed.Contains("\"speak\"", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("\"urgency\"", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   (doc.RootElement.TryGetProperty("speak", out _) ||
                    doc.RootElement.TryGetProperty("urgency", out _));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }
}
