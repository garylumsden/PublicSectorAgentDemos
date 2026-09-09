namespace GovernanceCouncil.Agents.Debate;

using System.Text;
using System.Text.Json;
using GovernanceCouncil.Core.Models;

/// <summary>
/// Renders the running debate into the clean prose context handed to each agent, and provides the
/// small text utilities the debate uses (clip, first-sentence, real-source filter). Every entry is
/// reduced to narrative prose so output formats never leak from one speaker into the next.
/// </summary>
internal static class TranscriptRenderer
{
    public static string Render(IReadOnlyList<CouncilContribution> transcript, int maxPerEntry)
    {
        var sb = new StringBuilder();

        var initial = transcript.Where(c => c.Kind == ContributionKind.InitialPosition).ToList();
        if (initial.Count > 0)
        {
            sb.AppendLine("Initial positions:");
            foreach (var c in initial)
                sb.AppendLine($"{c.DisplayName}: \"{Narrative(c.Content, maxPerEntry)}\"");
            sb.AppendLine();
        }

        var debate = transcript.Where(c => c.Kind != ContributionKind.InitialPosition).ToList();
        if (debate.Count > 0)
        {
            sb.AppendLine("Debate:");
            foreach (var c in debate)
                sb.AppendLine($"{c.DisplayName}: \"{Narrative(c.Content, maxPerEntry)}\"");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Reduces a contribution to narrative prose only. Defensively unwraps a stray {summary,detail}
    /// JSON object, drops code fences and source annotations, collapses blank lines, and clips length.
    /// </summary>
    private static string Narrative(string content, int maxPerEntry)
    {
        var text = (content ?? string.Empty).Trim();

        if (text.StartsWith('{') && text.EndsWith('}'))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
                        text = d.GetString() ?? "";
                    else if (doc.RootElement.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String)
                        text = s.GetString() ?? "";
                }
            }
            catch (JsonException) { /* not valid JSON after all — treat as prose */ }
        }

        text = ModelText.StripSourceAnnotations(text).Replace("```", "").Trim();
        text = System.Text.RegularExpressions.Regex.Replace(text, "\n{3,}", "\n\n");
        return text.Length > maxPerEntry ? text[..maxPerEntry] + "…" : text;
    }

    public static string FirstSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Trim();
        var idx = t.IndexOfAny(['.', '!', '?']);
        var sentence = idx > 0 ? t[..(idx + 1)] : t;
        return Clip(sentence, 200);
    }

    public static string Clip(string text, int max) =>
        text.Length > max ? text[..max] + "…" : text;

    /// <summary>
    /// True only for real http(s) source links. Drops empty URLs and internal tool references such
    /// as <c>mcp://answersynthesis</c> so only genuine sources reach the client.
    /// </summary>
    public static bool IsRealSourceLink(Citation c) =>
        !string.IsNullOrWhiteSpace(c.Url) &&
        (c.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         c.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}
