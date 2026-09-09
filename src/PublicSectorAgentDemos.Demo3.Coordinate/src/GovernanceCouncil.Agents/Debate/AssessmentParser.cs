namespace GovernanceCouncil.Agents.Debate;

using System.Text.Json;
using GovernanceCouncil.Core.Models;

/// <summary>
/// Parses the Chair's final JSON verdict into an <see cref="Assessment"/>. The shape mirrors the
/// output contract defined in <see cref="DossierPromptBuilder"/> and the Chair persona prompt.
/// Members that did not respond (responded=false / blank position) are skipped so we never surface
/// fabricated votes.
/// </summary>
internal static class AssessmentParser
{
    public static Assessment Parse(string chairResponse, string dossierId, string dossierTitle, string deliberationId)
    {
        // Extract JSON from the Chair's response (may be wrapped in markdown code fences)
        var json = chairResponse;
        var jsonStart = chairResponse.IndexOf('{');
        var jsonEnd = chairResponse.LastIndexOf('}');
        if (jsonStart >= 0 && jsonEnd > jsonStart)
            json = chairResponse[jsonStart..(jsonEnd + 1)];

        var assessmentId = $"AS-{DateTime.UtcNow:yyyy}-{Guid.NewGuid().ToString("N")[..8]}";

        // The Chair's JSON can be truncated (token limit) or cut short by a content filter on the
        // completion, leaving an unclosed object. Don't hard-fail the deliberation: fall back to a clear
        // "could not be parsed" Defer assessment so the user still gets a result with an explanation.
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return UnparseableFallback(dossierId, dossierTitle, deliberationId);
        }
        using (doc)
        {
        var root = doc.RootElement;

        var votes = new Dictionary<string, MemberVote>();
        if (root.TryGetProperty("votes", out var votesEl) && votesEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in votesEl.EnumerateObject())
            {
                if (prop.Value.TryGetProperty("responded", out var rsp) && rsp.ValueKind == JsonValueKind.False)
                    continue;
                var position = prop.Value.TryGetProperty("position", out var posEl) && posEl.ValueKind == JsonValueKind.String
                    ? posEl.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(position)) continue;
                votes[prop.Name] = new MemberVote
                {
                    Position = position!,
                    Summary = prop.Value.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : ""
                };
            }
        }

        var conditions = new List<string>();
        if (root.TryGetProperty("conditions", out var conditionsEl))
        {
            foreach (var c in conditionsEl.EnumerateArray())
                conditions.Add(c.GetString() ?? "");
        }

        var dissent = new List<Dissent>();
        if (root.TryGetProperty("dissent", out var dissentEl))
        {
            foreach (var d in dissentEl.EnumerateArray())
            {
                dissent.Add(new Dissent
                {
                    Member = d.GetProperty("member").GetString() ?? "Unknown",
                    Concern = d.TryGetProperty("concern", out var concern)
                        ? concern.GetString() ?? "" : ""
                });
            }
        }

        var risks = new List<Risk>();
        if (root.TryGetProperty("risks", out var risksEl))
        {
            foreach (var r in risksEl.EnumerateArray())
            {
                risks.Add(new Risk
                {
                    Category = r.GetProperty("category").GetString() ?? "Unknown",
                    Description = r.GetProperty("description").GetString() ?? "",
                    Mitigation = r.TryGetProperty("mitigation", out var m) ? m.GetString() ?? "" : ""
                });
            }
        }

        var deliberationSummary = new List<MemberContribution>();
        if (root.TryGetProperty("deliberationSummary", out var delSumEl) && delSumEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var ds in delSumEl.EnumerateArray())
            {
                if (ds.TryGetProperty("responded", out var dr) && dr.ValueKind == JsonValueKind.False)
                    continue;
                var position = ds.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.String ? pos.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(position)) continue;
                deliberationSummary.Add(new MemberContribution
                {
                    Member = ds.TryGetProperty("member", out var mem) ? mem.GetString() ?? "" : "",
                    Position = position,
                    KeyPoints = ds.TryGetProperty("keyPoints", out var kp) && kp.ValueKind == JsonValueKind.String ? kp.GetString() ?? "" : ""
                });
            }
        }

        var chairSummary = root.TryGetProperty("chairSummary", out var cs) ? cs.GetString() : null;

        var participants = new List<string>();
        if (root.TryGetProperty("participants", out var participantsEl) && participantsEl.ValueKind == JsonValueKind.Array)
            foreach (var p in participantsEl.EnumerateArray())
                if (p.GetString() is { } val) participants.Add(val);

        return new Assessment
        {
            Id = assessmentId,
            AssessmentId = assessmentId,
            DossierId = dossierId,
            DossierTitle = dossierTitle,
            DeliberationId = deliberationId,
            ReviewDate = DateTimeOffset.UtcNow,
            OverallRecommendation = root.TryGetProperty("overallRecommendation", out var rec)
                ? rec.GetString() ?? "Defer" : "Defer",
            ChairSummary = chairSummary,
            Participants = participants.Count > 0 ? participants : null,
            DeliberationSummary = deliberationSummary.Count > 0 ? deliberationSummary : null,
            Votes = votes,
            Conditions = conditions,
            Dissent = dissent,
            Risks = risks,
            Status = "Completed"
        };
        }
    }

    /// <summary>
    /// Builds a graceful "Defer" Assessment for when the Chair's synthesis JSON could not be parsed —
    /// usually because it was truncated (token limit) or cut short by a content filter on the
    /// completion. The deliberation completes with a clear explanation instead of hard-failing.
    /// </summary>
    public static Assessment UnparseableFallback(string dossierId, string dossierTitle, string deliberationId)
    {
        var assessmentId = $"AS-{DateTime.UtcNow:yyyy}-{Guid.NewGuid().ToString("N")[..8]}";
        return new Assessment
        {
            Id = assessmentId,
            AssessmentId = assessmentId,
            DossierId = dossierId,
            DossierTitle = dossierTitle,
            DeliberationId = deliberationId,
            ReviewDate = DateTimeOffset.UtcNow,
            OverallRecommendation = "Defer",
            ChairSummary =
                "The Council reached a verdict but the Chair's final assessment could not be read back: " +
                "the structured output was incomplete (it was likely truncated, or cut short by the " +
                "content-safety policy filtering the response). Re-run the deliberation; if it recurs on " +
                "the same dossier, the content may be triggering the Responsible AI filter — set the " +
                "Content safety control on the dossier page to a more permissive level, then try again.",
            Participants = null,
            DeliberationSummary = null,
            Votes = new Dictionary<string, MemberVote>(),
            Conditions = new List<string>(),
            Dissent = new List<Dissent>(),
            Risks = new List<Risk>(),
            Status = "Completed"
        };
    }
    /// <summary>
    /// Builds a graceful "Defer" Assessment for when the Chair's synthesis was blocked by the
    /// content-safety (RAI) policy, so the deliberation completes with clear, honest feedback (shown
    /// in the Chair Summary) instead of a hard failure. No fabricated votes/risks are invented.
    /// </summary>
    public static Assessment ContentFilteredFallback(string dossierId, string dossierTitle, string deliberationId)
    {
        var assessmentId = $"AS-{DateTime.UtcNow:yyyy}-{Guid.NewGuid().ToString("N")[..8]}";
        return new Assessment
        {
            Id = assessmentId,
            AssessmentId = assessmentId,
            DossierId = dossierId,
            DossierTitle = dossierTitle,
            DeliberationId = deliberationId,
            ReviewDate = DateTimeOffset.UtcNow,
            OverallRecommendation = "Defer",
            ChairSummary =
                "The Council could not deliver a full assessment: the Chair's synthesis was blocked by " +
                "the configured content-safety (Responsible AI) policy, meaning the deliberation content " +
                "was rated at or above the policy's blocking threshold. If this content is expected for " +
                "your scenario, set the Content safety control on the dossier page to a more permissive " +
                "level and re-run the deliberation — note a less restrictive policy requires a " +
                "subscription approved for modified content filters (Azure OpenAI Limited Access).",
            Participants = null,
            DeliberationSummary = null,
            Votes = new Dictionary<string, MemberVote>(),
            Conditions = new List<string>(),
            Dissent = new List<Dissent>(),
            Risks = new List<Risk>(),
            Status = "Completed"
        };
    }
}
