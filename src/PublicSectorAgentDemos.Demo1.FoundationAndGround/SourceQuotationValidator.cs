using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PublicSectorAgentDemos.Demo1.FoundationAndGround;

public static partial class SourceQuotationValidator
{
    public static IReadOnlyList<SourceQuotation> Inspect(
        JsonElement output,
        IReadOnlyList<MaterialOutputUnit> units,
        ApprovedCitationSource source)
    {
        List<RetrievedPassage> passages = [];
        foreach (JsonElement item in output.EnumerateArray())
        {
            if (!FoundryResponseCitationValidator.TryGetString(item, "type", out string type) ||
                type != "mcp_call" ||
                !FoundryResponseCitationValidator.TryGetString(item, "name", out string name) ||
                name != "knowledge_base_retrieve" ||
                !FoundryResponseCitationValidator.TryGetString(item, "status", out string status) ||
                status != "completed" ||
                !FoundryResponseCitationValidator.TryGetString(item, "output", out string retrieval))
            {
                continue;
            }

            foreach (Match match in RetrievalMarker().Matches(retrieval))
            {
                // The service returns citation markers followed by JSON snippet objects.
                byte[] json = Encoding.UTF8.GetBytes(retrieval[(match.Index + match.Length)..].TrimStart());
                Utf8JsonReader reader = new(json);
                try
                {
                    using JsonDocument document = JsonDocument.ParseValue(ref reader);
                    JsonElement passage = document.RootElement;
                    if (FoundryResponseCitationValidator.TryGetString(passage, "snippet", out string snippet) &&
                        FoundryResponseCitationValidator.TryGetString(passage, "blob_url", out string url) &&
                        source.ApprovedUris.Contains(url))
                    {
                        passages.Add(new(match.Value, Normalize(snippet)));
                    }
                }
                catch (JsonException)
                {
                    // A changed or malformed retrieval format cannot substantiate a quotation.
                    return units.Where(IsQuotation).Select(unit => new SourceQuotation(unit.Text, false)).ToArray();
                }
            }
        }

        return units.Where(IsQuotation).Select(unit =>
        {
            MatchCollection markers = RetrievalMarker().Matches(unit.Text);
            string quote = Normalize(RetrievalMarker().Replace(unit.Text[1..], "").Trim());
            RetrievedPassage? matchingPassage = passages.FirstOrDefault(passage =>
                markers.Count == 1 &&
                passage.Marker == markers[0].Value &&
                passage.Text.Contains(quote, StringComparison.Ordinal));
            bool verified = unit.HasValidCitation &&
                markers.Count == 1 &&
                quote.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length is >= 4 and <= 20 &&
                matchingPassage is not null;
            return new SourceQuotation(quote, verified,
                matchingPassage is not null && EndsAtSourceBoundary(quote, matchingPassage.Text));
        }).ToArray();
    }

    public static bool HasApplication(string text)
    {
        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        for (int index = 0; index < lines.Length - 1; index++)
        {
            if (lines[index].Trim().Trim('#', '*', ':', ' ') != "Application")
            {
                continue;
            }

            string? next = lines.Skip(index + 1).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
            return next is not null &&
                !next.TrimStart().StartsWith('>') &&
                next.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 5;
        }

        return false;
    }

    private static bool IsQuotation(MaterialOutputUnit unit) => unit.Text.StartsWith("> ", StringComparison.Ordinal);
    private static string Normalize(string text) => Whitespace().Replace(text, " ").Trim();

    private static bool EndsAtSourceBoundary(string quote, string passage)
    {
        if (quote.Length == 0)
        {
            return false;
        }
        if (".;?!".Contains(quote[^1], StringComparison.Ordinal))
        {
            return true;
        }
        int end = passage.IndexOf(quote, StringComparison.Ordinal) + quote.Length;
        string remainder = passage[end..].TrimStart();
        return remainder.Length == 0 || remainder[0] is '.' or ';' or '?' or '!' or '\u2022' or '\u25cf';
    }

    [GeneratedRegex(@"\u3010[^\u3011\r\n]+\u3011")]
    private static partial Regex RetrievalMarker();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private sealed record RetrievedPassage(string Marker, string Text);
}

public sealed record SourceQuotation(
    string Text,
    bool IsVerbatimRetrievedExcerpt,
    bool HasSourceBoundary = false);
