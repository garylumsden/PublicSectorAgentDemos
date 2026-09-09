namespace GovernanceCouncil.Agents.Debate;

using GovernanceCouncil.Core.Models;

/// <summary>
/// Builds the dossier prompt sent to every council member for their independent, blind
/// initial assessment. The JSON output contract here is what <see cref="AssessmentParser"/>
/// later reads back, so the two must stay in step.
/// </summary>
internal static class DossierPromptBuilder
{
    public static string Build(Dossier dossier, string markdownContent, string? context)
    {
        var prompt = $"""
            ## Dossier for Review

            **Title:** {dossier.Title}
            **Dossier ID:** {dossier.DossierId}
            **Source:** {dossier.Source}
            **Date:** {dossier.UploadedDate:yyyy-MM-dd}

            ---

            {markdownContent}
            """;

        if (!string.IsNullOrWhiteSpace(context))
        {
            prompt += $"""


                ---

                ## Additional Context

                {context}
                """;
        }

        prompt += """

            ---

            ## Your task — INDEPENDENT INITIAL ASSESSMENT (you are BLIND)

            Provide YOUR OWN independent expert assessment of this proposal, based solely on the
            dossier above and your own domain expertise.

            You MUST use your web grounding tool first to gather current, authoritative evidence for
            your domain, and cite the sources it returns.

            You are giving this assessment BLIND: NO other council member has spoken yet and you
            have NOT heard anyone else's view. Therefore you MUST NOT reference, agree with,
            respond to, or "follow up" on any other member, role, or council colleague. Do NOT
            write a "follow-up" or a discussion reply, and do NOT assume what anyone else thinks —
            this is your first, standalone position. The cross-examination with colleagues happens
            later, in a separate debate phase — not now.

            Respond with ONLY a single JSON object and NOTHING else — no prose outside the JSON,
            no code fences:
            {
              "summary": "<one concise plain-text sentence stating your position, <= 200 chars>",
              "detail": "<your full independent assessment in Markdown>",
              "stance": "Support" | "Support with Conditions" | "Oppose" | "Abstain"
            }

            - "detail" is your complete independent assessment in Markdown — evidence-based, specific to your own domain of expertise, and kept focused (around 400 words).
            - "summary" is a single plain-text sentence (<= 200 chars) capturing your stance and key reason.
            - "stance" is EXACTLY one of:
              - Support — sound in your domain; only minor observations.
              - Support with Conditions — specific, material gaps that must be addressed first; name concrete, actionable conditions.
              - Oppose — significant concerns requiring substantial rework.
              - Abstain — the proposal does not materially engage your domain. Rare.
            Do NOT put JSON inside "detail". Output the single JSON object only.
            """;

        return prompt;
    }
}
