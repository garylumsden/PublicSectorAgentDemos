namespace GovernanceCouncil.Agents.Debate;

using GovernanceCouncil.Core.Interfaces;
using GovernanceCouncil.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text.Json;

/// <summary>
/// Runs the "Independent Assessment" phase of the live debate in parallel.
/// All 7 council members produce independent assessments concurrently (no anchoring bias).
/// Each member is its UNIFIED tooled agent (Foundry IQ knowledge_base), invoked
/// as a black box: we stream only the final text and collect web-grounding citations.
/// Returns clean text contributions (for the transcript) plus transcript entries.
/// </summary>
internal sealed class ParallelPhase1Runner
{
    private readonly IDeliberationNotifier _notifier;
    private readonly ILogger _logger;

    public ParallelPhase1Runner(IDeliberationNotifier notifier, ILogger logger)
    {
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<(List<CouncilContribution> Contributions, List<TranscriptEntry> Transcript, List<MemberAssessment> Assessments)> RunAsync(
        string deliberationId,
        string dossierPrompt,
        string[] agentNames,
        IReadOnlyList<IChatClient> clients,
        CancellationToken ct = default)
    {
        var maxConcurrent = Grounding.Active == Grounding.Provider.WebIq
            ? Math.Min(CouncilModels.MaxParallelism, CouncilModels.GroundingMaxParallelism)
            : CouncilModels.MaxParallelism;
        _logger.LogInformation("Independent Assessment: invoking {Count} member clients (max {Max} concurrent)", clients.Count, maxConcurrent);

        using var gate = new SemaphoreSlim(maxConcurrent);
        var tasks = agentNames.Zip(clients, (name, client) => (name, client)).Select(async pair =>
        {
            await gate.WaitAsync(ct);
            try { return await InvokeAgentAsync(deliberationId, pair.name, pair.client, dossierPrompt, ct); }
            finally { gate.Release(); }
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        var contributions = new List<CouncilContribution>();
        var transcript = new List<TranscriptEntry>();
        var assessments = new List<MemberAssessment>();

        foreach (var result in results)
        {
            if (string.IsNullOrWhiteSpace(result.Detail)) continue;
            var display = AgentNaming.DisplayName(result.AgentName);
            contributions.Add(new CouncilContribution(result.AgentName, display, result.Detail, ContributionKind.InitialPosition));
            transcript.Add(new TranscriptEntry { AgentName = result.AgentName, Role = "assistant", Content = result.Detail });
            assessments.Add(result);
        }

        _logger.LogInformation("Independent Assessment complete. {Count}/{Total} agents responded",
            contributions.Count, clients.Count);

        return (contributions, transcript, assessments);
    }

    // Structured Outputs: force the model to return EXACTLY { summary, detail, stance } so gpt-5-mini
    // cannot drift to a wrong JSON shape or bid format (verified: gpt-5-mini accepts this strict schema).
    private static readonly System.Text.Json.JsonElement AssessmentSchema =
        System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "summary", "detail", "stance" },
            properties = new
            {
                summary = new { type = "string", description = "One concise plain-text sentence (<=200 chars) capturing stance and key reason." },
                detail = new { type = "string", description = "Full independent assessment in Markdown." },
                stance = new { type = "string", @enum = new[] { "Support", "Support with Conditions", "Oppose", "Abstain" } }
            }
        });

    private async Task<MemberAssessment> InvokeAgentAsync(
        string deliberationId,
        string agentName,
        IChatClient chatClient,
        string dossierPrompt,
        CancellationToken ct)
    {
        try
        {
            var messages = new List<ChatMessage> { new(ChatRole.User, dossierPrompt) };

            await _notifier.AgentSpeakingAsync(deliberationId, agentName);

            // Structured Outputs: enforce the exact { summary, detail, stance } shape (gpt-5-mini
            // otherwise occasionally drifts to a wrong shape / bid format). Falls back via try/catch.
            var options = new ChatOptions
            {
                ResponseFormat = CouncilModels.UseStructuredOutputs
                    ? ChatResponseFormat.ForJsonSchema(
                        AssessmentSchema, "council_assessment", "A council member's independent assessment of the dossier.")
                    : null
            };
            ChatResponse? response = null;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    response = await chatClient.GetResponseAsync(messages, options, cancellationToken: ct);
                    break;
                }
                catch (System.ClientModel.ClientResultException ex) when ((ex.Status == 429 || ex.Status >= 500) && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2 * (attempt + 1)), ct);
                }
            }

            // A filtered completion can come back as a ContentFilter finish rather than a thrown 400 —
            // surface it the same way (the thrown / prompt-blocked case is handled in the outer catch).
            if (response?.FinishReason == ChatFinishReason.ContentFilter)
            {
                _logger.LogWarning("Independent Assessment: {AgentName} output filtered (FinishReason=ContentFilter)", agentName);
                await _notifier.ContentSafetyTriggeredAsync(deliberationId, agentName, "output");
                await _notifier.AgentCompleteAsync(deliberationId, agentName);
                return new MemberAssessment(agentName, "", "", null);
            }

            var raw = response?.Text ?? "";
            var citations = CitationCollector.Extract(response?.Messages.SelectMany(m => m.Contents) ?? []);

            // Parse the {summary, detail, stance} contract. Discard bid-format JSON, unparseable
            // output, or an empty detail (RunAsync skips empties; the roll-call tolerates fewer members).
            var isBid = LooksLikeBidJson(raw);
            var parsed = TryParseAssessment(raw, out var summary, out var detail, out var stance);
            if (isBid || !parsed || string.IsNullOrWhiteSpace(detail))
            {
                _logger.LogWarning(
                    "Independent Assessment: {AgentName} discarded — {Reason}. Model {Model}. Raw: {Preview}",
                    agentName, DiscardReason.Classify(raw, isBid, parsed, string.IsNullOrWhiteSpace(detail)),
                    CouncilModels.Reasoning, DiscardReason.Preview(raw));
                await _notifier.AgentCompleteAsync(deliberationId, agentName);
                return new MemberAssessment(agentName, "", "", null);
            }

            var cleanDetail = ModelText.StripSourceAnnotations(detail);
            var cleanSummary = ModelText.StripSourceAnnotations(summary);

            // ONE chunk per turn = the whole parsed detail Markdown (never raw token streaming / JSON).
            await _notifier.AgentResponseChunkAsync(deliberationId, agentName, cleanDetail);
            await EmitCitationsAsync(deliberationId, agentName, citations);

            // Table this member's initial position LIVE — the moment its own assessment lands — so the
            // initial-position card fills in immediately instead of waiting for the full roll-call. Uses
            // the member's own one-sentence summary (fallback: first sentence of the detail).
            var positionLine = !string.IsNullOrWhiteSpace(cleanSummary)
                ? Clip(cleanSummary.Trim(), 200)
                : FirstSentence(cleanDetail);
            await _notifier.MemberAssessedAsync(deliberationId, new MemberPosition(agentName, positionLine));

            await _notifier.AgentCompleteAsync(deliberationId, agentName);

            _logger.LogInformation(
                "Independent Assessment: {AgentName} responded ({Length} chars, {Citations} citations, stance {Stance})",
                agentName, cleanDetail.Length, citations.Count, stance ?? "n/a");

            return new MemberAssessment(agentName, cleanDetail, cleanSummary, stance);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (ContentSafety.IsContentFilterBlock(ex))
            {
                _logger.LogWarning("Independent Assessment: {AgentName} blocked by the content-safety policy ({Scope})", agentName, ContentSafety.Scope(ex));
                await _notifier.ContentSafetyTriggeredAsync(deliberationId, agentName, ContentSafety.Scope(ex));
            }
            else
            {
                _logger.LogError(ex, "Independent Assessment: agent {AgentName} failed", agentName);
            }
            await _notifier.AgentCompleteAsync(deliberationId, agentName);
            return new MemberAssessment(agentName, "", "", null);
        }
    }

    /// <summary>
    /// Parses a persona's {summary, detail, stance} assessment JSON. Tolerant of leading/trailing
    /// prose by extracting the outermost JSON object. Returns false when no JSON object is present.
    /// </summary>
    private static bool TryParseAssessment(string? text, out string summary, out string detail, out string? stance)
    {
        summary = "";
        detail = "";
        stance = null;

        var json = ExtractJsonObject(text);
        if (json is null) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            detail = root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? "" : "";
            summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() ?? "" : "";
            stance = root.TryGetProperty("stance", out var st) && st.ValueKind == JsonValueKind.String
                ? st.GetString() : null;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : null;
    }

    /// <summary>One-sentence fallback for a member's roll-call line when its summary is blank.</summary>
    private static string FirstSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var t = text.Trim();
        var idx = t.IndexOfAny(['.', '!', '?']);
        var sentence = idx > 0 ? t[..(idx + 1)] : t;
        return Clip(sentence, 200);
    }

    private static string Clip(string text, int max) =>
        text.Length > max ? text[..max] + "…" : text;

    /// <summary>
    /// True when the text looks like a member's bid-format JSON ({"speak":..,"reason":..,"urgency":..})
    /// rather than a real prose assessment — used to discard mis-formatted model output.
    /// </summary>
    private static bool LooksLikeBidJson(string text)
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

    private async Task EmitCitationsAsync(string deliberationId, string agentName, List<Citation> citations)
    {
        if (citations.Count == 0) return;
        var distinct = citations
            .Where(IsRealSourceLink)
            .GroupBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (distinct.Count == 0) return;
        await _notifier.AgentCitationsAsync(deliberationId, agentName, distinct);
    }

    /// <summary>
    /// True only for real http(s) source links. Drops empty URLs and internal tool references such
    /// as <c>mcp://answersynthesis</c> so only genuine sources reach the client.
    /// </summary>
    private static bool IsRealSourceLink(Citation c) =>
        !string.IsNullOrWhiteSpace(c.Url) &&
        (c.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         c.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// One council member's parsed Phase-1 independent assessment: the full <c>Detail</c> Markdown
/// (used for the transcript), a one-sentence <c>Summary</c> (used for the agent-sourced roll-call),
/// and the declared <c>Stance</c> (Support / Support with Conditions / Oppose / Abstain).
/// </summary>
internal sealed record MemberAssessment(string AgentName, string Detail, string Summary, string? Stance);
