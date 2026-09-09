namespace GovernanceCouncil.Core.Models;

using System.Text.RegularExpressions;

/// <summary>
/// Helpers for cleaning raw model output before it is displayed or stored.
/// </summary>
public static class ModelText
{
    // Foundry/Azure OpenAI grounding annotations leak inline citation markers into the response
    // text, e.g. 【6:3†source】 or 【6†source】. They are bracketed with fullwidth brackets (U+3010 /
    // U+3011) and contain a dagger (†) or the word "source"; the actual citations are surfaced
    // separately, so these markers are noise in the displayed text.
    private static readonly Regex SourceAnnotation =
        new(@"【[^】]*?(?:†|source)[^】]*?】", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CollapseSpaces = new(@"[ \t]{2,}", RegexOptions.Compiled);

    /// <summary>Strips inline Foundry/OpenAI source-citation annotations (e.g. 【6:3†source】) from text.</summary>
    public static string StripSourceAnnotations(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var cleaned = SourceAnnotation.Replace(text, "");
        return CollapseSpaces.Replace(cleaned, " ");
    }
}
