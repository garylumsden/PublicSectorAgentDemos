namespace GovernanceCouncil.Agents.Debate;

using GovernanceCouncil.Core.Interfaces;
using GovernanceCouncil.Core.Models;
using GovernanceCouncil.Agents.Runtime;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Bid = GovernanceCouncil.Agents.Debate.ContributionParser.Bid;

/// <summary>
/// The live "council debate" engine. An explicit, observable loop:
///
///   1. Independent Assessment — all 7 tooled+grounded members assess the dossier in parallel.
///   2. Debate — for each round: members BID (hands-up) to speak,
///      the Chair SELECTS one speaker (push-to-talk), that member speaks (streamed + cited).
///   3. Synthesis — the Chair produces the final Assessment via structured (JSON) output.
///
/// Every persona agent is invoked as a BLACK BOX (final text + citations only); we build a
/// clean, text-only transcript (DisplayName + Content) and never replay tool-call/tool-result
/// messages across the council. This is what lets the TOOLED agents debate safely.
/// </summary>
internal sealed class CouncilDebate
{
    private readonly ICouncilRuntime _runtime;
    private readonly IDeliberationNotifier _notifier;
    private readonly ILogger _logger;

    private const string ChairName = "gc-chair";

    /// <summary>Max debate rounds (configurable via COUNCIL_DEBATE_MAX_ROUNDS; default 5).</summary>
    private static readonly int MaxRounds = ResolveMaxRounds();

    public CouncilDebate(
        ICouncilRuntime runtime,
        IDeliberationNotifier notifier,
        ILogger logger)
    {
        _runtime = runtime;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<(string AssessmentText, List<TranscriptEntry> Transcript)> RunAsync(
        string deliberationId,
        string dossierPrompt,
        CancellationToken ct = default)
    {
        _logger.LogInformation("CouncilDebate starting for {DeliberationId} (max {MaxRounds} rounds)", deliberationId, MaxRounds);

        var transcript = new List<CouncilContribution>();
        var minutes = new List<TranscriptEntry>();

        // --- Phase 1: Independent Assessment ------------------------------------
        await _notifier.DebatePhaseAsync(deliberationId, "Independent Assessment");
        var phase1 = new ParallelPhase1Runner(_notifier, _logger);
        var (contributions, phase1Minutes, assessments) = await phase1.RunAsync(
            deliberationId, dossierPrompt, _runtime.MemberNames, _runtime.MemberClients, ct);
        transcript.AddRange(contributions);
        minutes.AddRange(phase1Minutes);

        // The Moderator confirms the roll-call: all initial positions received, one sentence each.
        await ConfirmInitialPositionsAsync(deliberationId, assessments, ct);

        // --- Phase 2: Debate (hands-up / push-to-talk) --------------------------
        await _notifier.DebatePhaseAsync(deliberationId, "Debate");
        var speakCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string? lastSpeaker = null;
        var concludeReason = "Round limit reached";

        for (var round = 1; round <= MaxRounds; round++)
        {
            await _notifier.DebateRoundStartedAsync(deliberationId, round, MaxRounds);

            // BID ROUND: every member decides (in parallel) whether to raise a hand.
            var bids = await CollectBidsAsync(deliberationId, transcript, round, speakCounts, lastSpeaker, ct);

            var raised = bids.Where(b => b.Bid.Speak).ToList();
            if (raised.Count == 0)
            {
                concludeReason = "No further contributions";
                break;
            }

            // MODERATOR SELECTS one speaker, balancing urgency and breadth.
            var selected = await SelectSpeakerAsync(deliberationId, raised, speakCounts, ct);

            // SELECTED SPEAKER speaks (streamed + cited).
            var spoken = await SpeakAsync(deliberationId, selected, transcript, ct);
            if (!string.IsNullOrWhiteSpace(spoken))
            {
                var display = AgentNaming.DisplayName(selected);
                transcript.Add(new CouncilContribution(selected, display, spoken));
                minutes.Add(new TranscriptEntry { AgentName = selected, Role = "assistant", Content = spoken });
            }

            speakCounts[selected] = speakCounts.GetValueOrDefault(selected) + 1;
            lastSpeaker = selected;
        }

        await _notifier.DebateConcludedAsync(deliberationId, concludeReason);
        _logger.LogInformation("CouncilDebate debate phase concluded: {Reason}", concludeReason);

        // --- Phase 3: Synthesis -------------------------------------------------
        await _notifier.DebatePhaseAsync(deliberationId, "Synthesis");
        var assessmentText = await SynthesiseAsync(deliberationId, transcript, ct);
        minutes.Add(new TranscriptEntry { AgentName = ChairName, Role = "assistant", Content = assessmentText });

        _logger.LogInformation("CouncilDebate complete. {Turns} transcript turns, assessment {Len} chars",
            minutes.Count, assessmentText.Length);

        return (assessmentText, minutes);
    }

    // ----------------------------------------------------------------------------
    // BID ROUND
    // ----------------------------------------------------------------------------

    private async Task<List<(string AgentName, Bid Bid)>> CollectBidsAsync(
        string deliberationId,
        IReadOnlyList<CouncilContribution> transcript,
        int round,
        IReadOnlyDictionary<string, int> speakCounts,
        string? lastSpeaker,
        CancellationToken ct)
    {
        var transcriptText = TranscriptRenderer.Render(transcript, maxPerEntry: 1200);

        // The Moderator opens the floor first, so the gallery sees the call go out before hands rise.
        await _notifier.ModeratorCallingForSpeakersAsync(deliberationId, round);

        // The member who spoke in the immediately-preceding turn is excluded from this round's bids
        // entirely — they cannot bid and cannot be selected. This is deterministic, so the moderator
        // is never asked to avoid them.
        var bidders = _runtime.MemberNames.Zip(_runtime.MemberClients, (name, client) => (name, client))
            .Where(m => lastSpeaker is null || !string.Equals(m.name, lastSpeaker, StringComparison.OrdinalIgnoreCase));

        // Gate concurrency so a 6–7-wide bid round doesn't burst the model into 429s — tighter under
        // Web IQ (limited RPM). Matches the Phase-1 Independent Assessment cap.
        var maxConcurrent = Grounding.Active == Grounding.Provider.WebIq
            ? Math.Min(CouncilModels.MaxParallelism, CouncilModels.GroundingMaxParallelism)
            : CouncilModels.MaxParallelism;
        using var gate = new SemaphoreSlim(maxConcurrent);

        var tasks = bidders.Select(async m =>
        {
            var (name, client) = m;
            var spokenSoFar = speakCounts.GetValueOrDefault(name);
            // Identify the member in the prompt so MAF-mode bids (which share one fast client with no
            // persona prompt) still bid AS that member; harmless reinforcement in Foundry mode.
            var member = CouncilMembers.ByAgentName(name);
            var identity = member is not null
                ? $"You are {member.Name}, the council's {member.Role}."
                : $"You are {AgentNaming.DisplayName(name)}.";
            var question = $$"""
                {{identity}}

                This is round {{round}} of the live council debate. You have spoken {{spokenSoFar}} time(s) so far.
                Here is the debate transcript so far:

                {{transcriptText}}

                Decide whether YOU specifically need to speak THIS round to add something new —
                a challenge, a correction, a cross-domain risk, or a response to the floor.

                Only raise your hand if you have something genuinely NEW and important that has not
                already been said. Do NOT repeat or merely re-emphasise a point you already made.

                Do NOT use any tools. Respond with ONLY a JSON object:
                {"speak": <true|false>, "reason": "<why, <=140 chars>", "urgency": <1-5>}
                """;

            await gate.WaitAsync(ct);
            Bid bid;
            try { bid = await AskBidAsync(deliberationId, name, client, question, ct); }
            finally { gate.Release(); }
            await _notifier.AgentHandRaisedAsync(deliberationId, name, bid.Speak, bid.Reason);
            return (AgentName: name, Bid: bid);
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        var raisedNames = results.Where(r => r.Bid.Speak)
            .Select(r => AgentNaming.DisplayName(r.AgentName))
            .ToList();
        _logger.LogInformation(
            "Round {Round}: {Count} hand(s) raised — {Names}",
            round, raisedNames.Count, raisedNames.Count > 0 ? string.Join(", ", raisedNames) : "none");

        return results.ToList();
    }

    private async Task<Bid> AskBidAsync(string deliberationId, string agentName, IChatClient memberClient, string question, CancellationToken ct)
    {
        try
        {
            // MAF mode routes bids to the fast non-reasoning client; Foundry mode uses the member's own.
            var chatClient = _runtime.FastClient ?? memberClient;

            // Structured Outputs: enforce { speak, reason, urgency }.
            var options = new ChatOptions
            {
                ResponseFormat = CouncilModels.UseStructuredOutputs
                    ? ChatResponseFormat.ForJsonSchema(DebateSchemas.Bid, "council_bid", "A member's hand-raise bid.")
                    : null
            };
            var response = await ModelRetry.OnTransient(() => chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, question)], options, ct), ct);

            return ContributionParser.ParseBid(response.Text);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (ContentSafety.IsContentFilterBlock(ex))
            {
                // The member couldn't even bid this round because their input was content-filtered —
                // surface it so the trigger is visible, then treat as a no-bid.
                _logger.LogWarning("Bid blocked by the content-safety policy for {Agent} ({Scope})", agentName, ContentSafety.Scope(ex));
                await _notifier.ContentSafetyTriggeredAsync(deliberationId, agentName, ContentSafety.Scope(ex));
            }
            else
            {
                _logger.LogWarning(ex, "Bid call failed; treating as no-bid");
            }
            return new Bid(false, "bid error", 1);
        }
    }

    // ----------------------------------------------------------------------------
    // MODERATOR SELECTS SPEAKER
    // ----------------------------------------------------------------------------

    private async Task<string> SelectSpeakerAsync(
        string deliberationId,
        List<(string AgentName, Bid Bid)> raised,
        IReadOnlyDictionary<string, int> speakCounts,
        CancellationToken ct)
    {
        // Prefer the voices that have spoken least, then highest urgency.
        var candidates = raised
            .OrderBy(r => speakCounts.GetValueOrDefault(r.AgentName))
            .ThenByDescending(r => r.Bid.Urgency)
            .ToList();

        var fallback = candidates[0].AgentName;
        var selected = fallback;
        var reason = candidates[0].Bid.Reason;

        try
        {
            var roster = string.Join("\n", candidates.Select(c =>
                $"- {c.AgentName} ({AgentNaming.DisplayName(c.AgentName)}): urgency {c.Bid.Urgency}, spoken {speakCounts.GetValueOrDefault(c.AgentName)}x — {c.Bid.Reason}"));

            var prompt = $$"""
                These members raised their hands to speak next:
                {{roster}}

                Pick exactly ONE to speak next. Balance urgency against breadth of voices, and avoid
                letting the same member dominate.
                Respond with ONLY JSON: {"agentName": "<gc-id from the list>", "reason": "<why, <=120 chars>"}
                """;

            var moderator = _runtime.FastClient ?? _runtime.ModeratorClient;
            if (moderator is not null)
            {
                // Structured Outputs: enforce { agentName, reason }.
                var options = new ChatOptions
                {
                    ResponseFormat = CouncilModels.UseStructuredOutputs
                        ? ChatResponseFormat.ForJsonSchema(DebateSchemas.Selection, "council_speaker_selection", "The moderator's next-speaker pick.")
                        : null
                };
                var response = await moderator.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], options, ct);
                var pick = ContributionParser.ParseSelection(response.Text);
                if (pick.AgentName is not null &&
                    candidates.Any(c => string.Equals(c.AgentName, pick.AgentName, StringComparison.OrdinalIgnoreCase)))
                {
                    selected = candidates.First(c => string.Equals(c.AgentName, pick.AgentName, StringComparison.OrdinalIgnoreCase)).AgentName;
                    reason = string.IsNullOrWhiteSpace(pick.Reason) ? reason : pick.Reason;
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Moderator selection call failed; using heuristic pick {Agent}", fallback);
        }

        await _notifier.ModeratorSelectedSpeakerAsync(deliberationId, selected, reason);
        _logger.LogInformation(
            "Moderator selected {DisplayName} ({Agent}) — {Reason}",
            AgentNaming.DisplayName(selected), selected, reason);
        return selected;
    }

    // ----------------------------------------------------------------------------
    // SPEAK
    // ----------------------------------------------------------------------------


    private async Task<string> SpeakAsync(
        string deliberationId, string agentName, IReadOnlyList<CouncilContribution> transcript, CancellationToken ct)
    {
        var chatClient = ResolveClient(agentName);
        if (chatClient is null) return "";

        var transcriptText = TranscriptRenderer.Render(transcript, maxPerEntry: 4000);
        var prompt = $$"""
            This is a live council debate. The transcript so far:

            {{transcriptText}}

            It is now your turn. Respond directly to the most important point(s) above — challenge,
            agree, refine, or rebut, and address any floor questions. Do NOT repeat your earlier
            assessment verbatim. Use your grounding tool only if a new claim needs checking; if you cite current facts, cite sources.

            Respond with ONLY a single JSON object and NOTHING else — no prose outside the JSON,
            no code fences:
            {
              "summary": "<one concise plain-text sentence capturing your point, <= 200 chars>",
              "detail": "<your spoken debate contribution in Markdown — conversational and concise, a few short paragraphs at most>"
            }

            "detail" is your spoken contribution to the chamber, written as plain Markdown prose —
            no JSON, key/value objects, or code fences inside it. Output the single JSON object only.
            """;

        try
        {
            await _notifier.AgentSpeakingAsync(deliberationId, agentName);
            _logger.LogInformation("{DisplayName} taking the floor", AgentNaming.DisplayName(agentName));

            // Structured Outputs: enforce the exact { summary, detail } shape.
            var options = new ChatOptions
            {
                ResponseFormat = CouncilModels.UseStructuredOutputs
                    ? ChatResponseFormat.ForJsonSchema(DebateSchemas.Speak, "council_speak", "A council member's spoken debate contribution.")
                    : null
            };

            // The spoken turn is structured JSON we parse before showing (the chamber must never see raw
            // JSON), so there's nothing to render token-by-token — use a single non-streaming call,
            // wrapped in the transient retry so a Grok 429/5xx doesn't lose the turn.
            var response = await ModelRetry.OnTransient(() => chatClient.GetResponseAsync(
                [new ChatMessage(ChatRole.User, prompt)], options, ct), ct);

            if (response.FinishReason == ChatFinishReason.ContentFilter)
            {
                _logger.LogWarning("Speaker {Agent} output filtered (FinishReason=ContentFilter)", agentName);
                await _notifier.ContentSafetyTriggeredAsync(deliberationId, agentName, "output");
                await _notifier.AgentCompleteAsync(deliberationId, agentName);
                return "";
            }

            var raw = response.Text ?? "";
            var citations = CitationCollector.Extract(response.Messages.SelectMany(m => m.Contents));

            // Parse the {summary, detail} contract. Discard bid-format JSON, unparseable output, or
            // an empty detail so it never enters the transcript (caller skips empty text).
            var isBid = ContributionParser.LooksLikeBidJson(raw);
            var parsed = ContributionParser.TryParseSpoken(raw, out var detail);
            if (isBid || !parsed || string.IsNullOrWhiteSpace(detail))
            {
                _logger.LogWarning("Speaker {Agent} discarded — {Reason}. Model {Model}. Raw: {Preview}",
                    agentName, DiscardReason.Classify(raw, isBid, parsed, string.IsNullOrWhiteSpace(detail)),
                    CouncilModels.Reasoning, DiscardReason.Preview(raw));
                await _notifier.AgentCompleteAsync(deliberationId, agentName);
                return "";
            }

            var finalText = ModelText.StripSourceAnnotations(detail);

            // ONE chunk per turn = the whole parsed detail Markdown (never raw token streaming / JSON).
            await _notifier.AgentResponseChunkAsync(deliberationId, agentName, finalText);
            await EmitCitationsAsync(deliberationId, agentName, citations);
            await _notifier.AgentCompleteAsync(deliberationId, agentName);

            _logger.LogInformation(
                "{DisplayName} finished ({Length} chars, {Citations} citations)",
                AgentNaming.DisplayName(agentName), finalText.Length, citations.Count);
            return finalText;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            if (ContentSafety.IsContentFilterBlock(ex))
            {
                _logger.LogWarning("Speaker {Agent} blocked by the content-safety policy ({Scope})", agentName, ContentSafety.Scope(ex));
                await _notifier.ContentSafetyTriggeredAsync(deliberationId, agentName, ContentSafety.Scope(ex));
            }
            else
            {
                _logger.LogError(ex, "Speaker {Agent} failed", agentName);
            }
            await _notifier.AgentCompleteAsync(deliberationId, agentName);
            return "";
        }
    }

    // ----------------------------------------------------------------------------
    // SYNTHESIS
    // ----------------------------------------------------------------------------

    private async Task<string> SynthesiseAsync(
        string deliberationId, IReadOnlyList<CouncilContribution> transcript, CancellationToken ct)
    {
        var transcriptText = TranscriptRenderer.Render(transcript, maxPerEntry: 6000);
        var prompt = $"""
            The debate is complete. Here is the full transcript:

            {transcriptText}

            Produce the FINAL Assessment as a single JSON object exactly per your instructions
            (overallRecommendation, chairSummary, deliberationSummary, votes, conditions, dissent,
            risks). Output JSON only — no prose, no code fences.
            """;

        try
        {
            var chair = _runtime.ChairClient;
            if (chair is null) return "{}";

            await _notifier.AgentSpeakingAsync(deliberationId, ChairName);

            // The synthesis output is structured JSON we parse before showing — nothing is rendered live
            // (the chamber shows a "finalising" placeholder), so use a single non-streaming call. Retry
            // ONCE with a higher token cap if truncated (FinishReason == Length); a ContentFilter finish
            // means the output was filtered → surface it as a content-safety block.
            ChatResponse? response = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var options = new ChatOptions
                {
                    ResponseFormat = CouncilModels.UseStructuredOutputs
                        ? ChatResponseFormat.ForJsonSchema(DebateSchemas.Chair, "council_assessment", "The Council Chair's final structured Assessment.")
                        : null,
                    // First pass uses the model default; on truncation, retry with a generous cap.
                    MaxOutputTokens = attempt == 0 ? null : 16000
                };
                response = await chair.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], options, ct);
                if (response.FinishReason != ChatFinishReason.Length) break;
                _logger.LogWarning("Chair synthesis truncated (FinishReason=Length); retrying with a higher token cap.");
            }

            await _notifier.AgentCompleteAsync(deliberationId, ChairName);

            if (response?.FinishReason == ChatFinishReason.ContentFilter)
            {
                _logger.LogWarning("Chair synthesis output filtered (FinishReason=ContentFilter).");
                await _notifier.ContentSafetyTriggeredAsync(deliberationId, ChairName, "output");
                return ContentSafety.BlockedMarker;
            }
            var text = response?.Text ?? "";
            return string.IsNullOrWhiteSpace(text) ? "{}" : text;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await _notifier.AgentCompleteAsync(deliberationId, ChairName);
            if (ContentSafety.IsContentFilterBlock(ex))
            {
                _logger.LogWarning(ex, "Chair synthesis blocked by the content-safety policy");
                await _notifier.ContentSafetyTriggeredAsync(deliberationId, ChairName, ContentSafety.Scope(ex));
                return ContentSafety.BlockedMarker;
            }
            _logger.LogError(ex, "Chair synthesis failed");
            return "{}";
        }
    }

    // ----------------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------------

    private IChatClient? ResolveClient(string agentName)
    {
        for (var i = 0; i < _runtime.MemberNames.Length; i++)
            if (string.Equals(_runtime.MemberNames[i], agentName, StringComparison.OrdinalIgnoreCase))
                return _runtime.MemberClients[i];
        if (string.Equals(agentName, ChairName, StringComparison.OrdinalIgnoreCase)) return _runtime.ChairClient;
        return null;
    }

    private async Task EmitCitationsAsync(string deliberationId, string agentName, List<Citation> citations)
    {
        if (citations.Count == 0) return;
        var distinct = citations
            .Where(TranscriptRenderer.IsRealSourceLink)
            .GroupBy(c => c.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        if (distinct.Count == 0) return;
        await _notifier.AgentCitationsAsync(deliberationId, agentName, distinct);
    }

    // ----------------------------------------------------------------------------
    // INITIAL-POSITION ROLL-CALL (end of Independent Assessment)
    // ----------------------------------------------------------------------------

    /// <summary>
    /// Confirms the roll-call after the Independent Assessment: each member's ONE-SENTENCE position
    /// is taken from that agent's OWN parsed <c>summary</c> (no separate moderator call). Falls back
    /// to the first sentence of the member's detail if a summary is missing/blank.
    /// </summary>
    private async Task ConfirmInitialPositionsAsync(
        string deliberationId, IReadOnlyList<MemberAssessment> assessments, CancellationToken ct)
    {
        var members = assessments
            .Where(a => !string.Equals(a.AgentName, ChairName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (members.Count == 0) return;

        // Each member's roll-call line comes from its OWN summary (fallback: first sentence of detail).
        var positions = members
            .Select(a => new MemberPosition(
                a.AgentName,
                !string.IsNullOrWhiteSpace(a.Summary) ? TranscriptRenderer.Clip(a.Summary.Trim(), 200) : TranscriptRenderer.FirstSentence(a.Detail)))
            .ToList();

        await _notifier.ModeratorAssessmentRollCallAsync(deliberationId, positions);
        _logger.LogInformation("Confirmed {Count} initial positions from member summaries", positions.Count);

        // A short, deliberate beat so the gallery can read the roll-call before the debate opens.
        try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { }
    }

    private static int ResolveMaxRounds()
    {
        var raw = Environment.GetEnvironmentVariable("COUNCIL_DEBATE_MAX_ROUNDS");
        return int.TryParse(raw, out var v) && v is > 0 and <= 20 ? v : 5;
    }
}
