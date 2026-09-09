using System.Globalization;
using System.Text.Json;

namespace PublicSectorAgentDemos.Demo1.FoundationAndGround;

public static class FoundryResponseCitationValidator
{
    private static readonly HashSet<string> CitationTypes =
        new(StringComparer.Ordinal) { "url_citation", "file_citation" };

    private static readonly HashSet<string> SectionLabels =
        new(StringComparer.Ordinal)
        {
            "Assessment",
            "Evidence",
            "Application",
            "DelegatedAuthority",
            "FourStandards",
            "RisksAndUnknowns",
            "NextAction",
            "Assumptions and unresolved facts (provisional)"
        };

    public static FoundryResponseCitationResult Inspect(
        string responseJson,
        ApprovedCitationSource approvedSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(responseJson);
        ArgumentNullException.ThrowIfNull(approvedSource);

        using JsonDocument document = JsonDocument.Parse(responseJson);
        List<string> outputText = [];
        List<FoundryCitationAnnotation> citations = [];
        List<MaterialOutputUnit> materialUnits = [];
        bool hasUnsupportedAnnotations = false;
        int outputBlockIndex = 0;

        if (!document.RootElement.TryGetProperty("output", out JsonElement output) ||
            output.ValueKind != JsonValueKind.Array)
        {
            return new("", [], [], false, false);
        }

        foreach (JsonElement item in output.EnumerateArray())
        {
            if (!HasType(item, "message") ||
                !item.TryGetProperty("content", out JsonElement content) ||
                content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement part in content.EnumerateArray())
            {
                if (!HasType(part, "output_text") ||
                    !part.TryGetProperty("text", out JsonElement textElement) ||
                    textElement.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string text = textElement.GetString()!;
                outputText.Add(text);
                materialUnits.AddRange(EnumerateMaterialUnits(text, outputBlockIndex));
                if (part.TryGetProperty("annotations", out JsonElement annotations))
                {
                    if (annotations.ValueKind != JsonValueKind.Array)
                    {
                        hasUnsupportedAnnotations = true;
                    }
                    else
                    {
                        foreach (JsonElement annotation in annotations.EnumerateArray())
                        {
                            if (annotation.ValueKind != JsonValueKind.Object ||
                                !annotation.TryGetProperty("type", out JsonElement typeElement) ||
                                typeElement.ValueKind != JsonValueKind.String ||
                                !CitationTypes.Contains(typeElement.GetString()!))
                            {
                                hasUnsupportedAnnotations = true;
                                continue;
                            }

                            string type = typeElement.GetString()!;
                            CitationLocation location =
                                GetCitationLocation(annotation, type, text);
                            citations.Add(new(
                                type,
                                outputBlockIndex,
                                location.StartIndex,
                                location.EndIndex,
                                location.FileListIndex,
                                location.IsSupported,
                                ResolvesToApprovedSource(annotation, type, approvedSource)));
                        }
                    }
                }

                outputBlockIndex++;
            }
        }

        string combinedText = string.Join(Environment.NewLine, outputText);
        bool isExactExemptMessage =
            IsExactSingleOutputTextResponse(output, GroundedResponseGate.UnsupportedRefusalMessage) ||
            IsExactSingleOutputTextResponse(output, GroundedResponseGate.FailureMessage);
        if (isExactExemptMessage)
        {
            materialUnits.Clear();
        }

        IReadOnlyList<MaterialOutputUnit> coveredUnits =
            MatchMaterialUnitsToCitations(materialUnits, citations);
        return new(
            combinedText,
            citations,
            coveredUnits,
            hasUnsupportedAnnotations,
            isExactExemptMessage)
        {
            Quotations = SourceQuotationValidator.Inspect(output, coveredUnits, approvedSource),
            HasApplication = SourceQuotationValidator.HasApplication(combinedText)
        };
    }

    private static IReadOnlyList<MaterialOutputUnit> MatchMaterialUnitsToCitations(
        IReadOnlyList<MaterialOutputUnit> materialUnits,
        IReadOnlyList<FoundryCitationAnnotation> citations) =>
        materialUnits
            .Select(unit => unit with
            {
                HasValidCitation = citations.Any(citation =>
                        citation.IsAttachedToOutputText &&
                        citation.ResolvesToApprovedSource &&
                        CoversMarker(unit, citation))
            })
            .ToArray();

    private static bool CoversMarker(
        MaterialOutputUnit unit,
        FoundryCitationAnnotation citation)
    {
        if (unit.OutputBlockIndex != citation.OutputBlockIndex)
        {
            return false;
        }

        if (citation.Type == "file_citation")
        {
            return unit.CitationMarkerNumber is int markerNumber &&
                citation.FileListIndex is int fileListIndex &&
                fileListIndex == markerNumber - 1;
        }

        return citation.StartIndex is int startIndex &&
            citation.EndIndex is int endIndex &&
            startIndex >= unit.StartIndex &&
            endIndex <= unit.EndIndex;
    }

    private static IEnumerable<MaterialOutputUnit> EnumerateMaterialUnits(
        string text,
        int outputBlockIndex)
    {
        int lineStart = 0;
        while (lineStart <= text.Length)
        {
            int newlineIndex = text.IndexOf('\n', lineStart);
            int lineEnd = newlineIndex < 0 ? text.Length : newlineIndex;
            int contentEnd = lineEnd > lineStart && text[lineEnd - 1] == '\r'
                ? lineEnd - 1
                : lineEnd;
            int contentStart = lineStart;
            while (contentEnd > contentStart && char.IsWhiteSpace(text[contentEnd - 1]))
            {
                contentEnd--;
            }
            while (contentStart < contentEnd && char.IsWhiteSpace(text[contentStart]))
            {
                contentStart++;
            }

            if (contentStart < contentEnd)
            {
                string unitText = text[contentStart..contentEnd];
                if (!IsSectionLabel(unitText))
                {
                    bool hasMarker = TryGetCitationMarker(
                        text,
                        contentStart,
                        contentEnd,
                        out int markerStartIndex,
                        out int markerNumber);
                    yield return new(
                        outputBlockIndex,
                        contentStart,
                        contentEnd,
                        unitText,
                        hasMarker ? markerStartIndex : null,
                        hasMarker ? contentEnd : null,
                        hasMarker ? markerNumber : null,
                        false);
                }
            }

            if (newlineIndex < 0)
            {
                yield break;
            }

            lineStart = newlineIndex + 1;
        }
    }

    private static bool TryGetCitationMarker(
        string text,
        int contentStart,
        int contentEnd,
        out int markerStartIndex,
        out int markerNumber)
    {
        markerStartIndex = -1;
        markerNumber = 0;
        if (contentEnd - contentStart < 4 ||
            text[contentEnd - 1] != ']')
        {
            return false;
        }

        markerStartIndex = text.LastIndexOf('[', contentEnd - 1, contentEnd - contentStart);
        if (markerStartIndex <= contentStart ||
            text[markerStartIndex - 1] != ' ')
        {
            return false;
        }

        ReadOnlySpan<char> numberText =
            text.AsSpan(markerStartIndex + 1, contentEnd - markerStartIndex - 2);
        return int.TryParse(
                numberText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out markerNumber) &&
            markerNumber > 0 &&
            numberText.SequenceEqual(
                markerNumber.ToString(CultureInfo.InvariantCulture).AsSpan());
    }

    private static bool IsExactSingleOutputTextResponse(
        JsonElement output,
        string expectedText)
    {
        JsonElement? message = null;
        foreach (JsonElement item in output.EnumerateArray())
        {
            if (HasType(item, "reasoning") || HasType(item, "mcp_list_tools"))
            {
                continue;
            }

            if (!HasType(item, "message") || message is not null)
            {
                return false;
            }

            message = item;
        }

        if (message is not JsonElement assistantMessage ||
            !assistantMessage.TryGetProperty("role", out JsonElement role) ||
            role.ValueKind != JsonValueKind.String ||
            !string.Equals(role.GetString(), "assistant", StringComparison.Ordinal) ||
            !assistantMessage.TryGetProperty("content", out JsonElement content) ||
            content.ValueKind != JsonValueKind.Array ||
            content.GetArrayLength() != 1)
        {
            return false;
        }

        JsonElement part = content[0];
        if (!HasType(part, "output_text") ||
            !part.TryGetProperty("text", out JsonElement text) ||
            text.ValueKind != JsonValueKind.String ||
            !string.Equals(text.GetString(), expectedText, StringComparison.Ordinal))
        {
            return false;
        }

        return !part.TryGetProperty("annotations", out JsonElement annotations) ||
            annotations.ValueKind == JsonValueKind.Array &&
            annotations.GetArrayLength() == 0;
    }

    private static bool IsSectionLabel(string text) =>
        SectionLabels.Contains(NormalizeLabel(text));

    private static string NormalizeLabel(string text)
    {
        string normalized = text.Trim().TrimStart('#').Trim();
        int prefixLength = 0;
        while (prefixLength < normalized.Length && char.IsDigit(normalized[prefixLength]))
        {
            prefixLength++;
        }

        if (prefixLength > 0 &&
            prefixLength < normalized.Length &&
            normalized[prefixLength] == '.')
        {
            normalized = normalized[(prefixLength + 1)..].TrimStart();
        }

        normalized = normalized.Trim('*', '_', '`').Trim();
        normalized = normalized.TrimEnd(':').Trim();
        return normalized.Trim('*', '_', '`').Trim();
    }

    private static bool HasType(JsonElement element, string expectedType) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty("type", out JsonElement type) &&
        type.ValueKind == JsonValueKind.String &&
        string.Equals(type.GetString(), expectedType, StringComparison.Ordinal);

    private static CitationLocation GetCitationLocation(
        JsonElement annotation,
        string type,
        string text)
    {
        if (type == "url_citation")
        {
            int startIndex = 0;
            int endIndex = 0;
            bool isSupported =
                TryGetInt32(annotation, "start_index", out startIndex) &&
                TryGetInt32(annotation, "end_index", out endIndex) &&
                startIndex >= 0 &&
                endIndex > startIndex &&
                endIndex <= text.Length;
            return new(startIndex, endIndex, null, isSupported);
        }

        int fileListIndex = 0;
        bool hasFileListIndex =
            TryGetInt32(annotation, "index", out fileListIndex) &&
            fileListIndex >= 0;
        return new(null, null, fileListIndex, hasFileListIndex);
    }

    private static bool ResolvesToApprovedSource(
        JsonElement annotation,
        string type,
        ApprovedCitationSource approvedSource)
    {
        if (type == "file_citation")
        {
            return TryGetString(annotation, "file_id", out string fileId) &&
                TryGetString(annotation, "filename", out string filename) &&
                approvedSource.FileIds.Contains(fileId) &&
                filename.Equals(approvedSource.Filename, StringComparison.Ordinal);
        }

        return TryGetString(annotation, "url", out string url) &&
            IsApprovedUri(url, approvedSource.ApprovedUris);
    }

    private static bool IsApprovedUri(
        string value,
        IReadOnlySet<string> approvedUris)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? actual) ||
            actual.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        string normalizedActual = actual.AbsoluteUri;
        return approvedUris.Any(approvedValue =>
            Uri.TryCreate(approvedValue, UriKind.Absolute, out Uri? approved) &&
            approved.Scheme == Uri.UriSchemeHttps &&
            normalizedActual.Equals(approved.AbsoluteUri, StringComparison.Ordinal));
    }

    internal static bool TryGetString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out JsonElement property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetInt32(
        JsonElement element,
        string propertyName,
        out int value)
    {
        value = default;
        return element.TryGetProperty(propertyName, out JsonElement property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetInt32(out value);
    }

    private readonly record struct CitationLocation(
        int? StartIndex,
        int? EndIndex,
        int? FileListIndex,
        bool IsSupported);
}

public sealed record ApprovedCitationSource(
    IReadOnlySet<string> FileIds,
    string Filename,
    IReadOnlySet<string> ApprovedUris);

public sealed record FoundryCitationAnnotation(
    string Type,
    int OutputBlockIndex,
    int? StartIndex,
    int? EndIndex,
    int? FileListIndex,
    bool IsAttachedToOutputText,
    bool ResolvesToApprovedSource);

public sealed record MaterialOutputUnit(
    int OutputBlockIndex,
    int StartIndex,
    int EndIndex,
    string Text,
    int? MarkerStartIndex,
    int? MarkerEndIndex,
    int? CitationMarkerNumber,
    bool HasValidCitation);

public sealed record FoundryResponseCitationResult(
    string Text,
    IReadOnlyList<FoundryCitationAnnotation> Citations,
    IReadOnlyList<MaterialOutputUnit> MaterialUnits,
    bool HasUnsupportedAnnotations,
    bool IsExactExemptMessage)
{
    public IReadOnlyList<SourceQuotation> Quotations { get; init; } = [];
    public bool HasApplication { get; init; }
    public bool HasVerifiedQuotations =>
        Quotations.Count is >= 2 and <= 4 &&
        Quotations.All(quotation => quotation.IsVerbatimRetrievedExcerpt && quotation.HasSourceBoundary);

    public bool AllCitationsAreValid =>
        !HasUnsupportedAnnotations &&
        Citations.All(citation =>
            citation.IsAttachedToOutputText &&
            citation.ResolvesToApprovedSource);

    public bool AllMaterialUnitsHaveValidCitations =>
        MaterialUnits.All(unit => unit.HasValidCitation);
}
