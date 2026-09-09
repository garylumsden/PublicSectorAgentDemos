namespace GovernanceCouncil.Agents.Debate;

using System.Text.Json;
using GovernanceCouncil.Core.Models;

/// <summary>
/// Structured-output JSON schema for the Chair's final Assessment. Every council member is ALWAYS
/// present in <c>votes</c> + <c>deliberationSummary</c>; a <c>responded</c> flag with null content
/// marks members who did not speak — this keeps the "no fabrication" rule while still allowing a
/// strict schema. The member id set is taken from the active scenario roster
/// (<see cref="CouncilMembers.DeliberationMembers"/>), so the schema adapts to any council.
/// Mirrors what <see cref="AssessmentParser"/> reads back.
/// </summary>
internal static class DebateSchemas
{
    public static JsonElement Chair => BuildChairSchema();

    /// <summary>Schema for a member's hand-raise bid: { speak, reason, urgency }.</summary>
    public static readonly JsonElement Bid = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "speak", "reason", "urgency" },
        properties = new
        {
            speak = new { type = "boolean", description = "Whether you need to speak this round." },
            reason = new { type = "string", description = "Why, <=140 chars." },
            urgency = new { type = "integer", description = "1 (low) to 5 (high)." }
        }
    });

    /// <summary>Schema for the moderator's next-speaker selection: { agentName, reason }.</summary>
    public static readonly JsonElement Selection = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "agentName", "reason" },
        properties = new
        {
            agentName = new { type = "string", description = "The gc- id of the member to speak next." },
            reason = new { type = "string", description = "Why, <=120 chars." }
        }
    });

    /// <summary>Schema forcing a speaking member to { summary, detail } so it cannot drift to a bid shape.</summary>
    public static readonly JsonElement Speak = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
        required = new[] { "summary", "detail" },
        properties = new
        {
            summary = new { type = "string", description = "One concise plain-text sentence (<=200 chars) capturing your point." },
            detail = new { type = "string", description = "Your spoken debate contribution in Markdown." }
        }
    });

    private static JsonElement BuildChairSchema()
    {
        static object Nullable(string t) => new { type = new[] { t, "null" } };

        var vote = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "responded", "position", "summary" },
            properties = new Dictionary<string, object>
            {
                ["responded"] = new { type = "boolean" },
                ["position"] = Nullable("string"),
                ["summary"] = Nullable("string")
            }
        };
        var memberIds = CouncilMembers.DeliberationMembers.Select(m => m.Id).ToArray();
        var voteProps = memberIds.ToDictionary(m => m, _ => (object)vote);

        var schema = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "participants", "overallRecommendation", "chairSummary", "deliberationSummary", "votes", "conditions", "dissent", "risks" },
            properties = new Dictionary<string, object>
            {
                ["participants"] = new { type = "array", items = new { type = "string" } },
                ["overallRecommendation"] = new { type = "string", @enum = new[] { "Approve", "Approve with Conditions", "Defer", "Reject" } },
                ["chairSummary"] = new { type = "string" },
                ["deliberationSummary"] = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "member", "responded", "position", "keyPoints" },
                        properties = new Dictionary<string, object>
                        {
                            ["member"] = new { type = "string" },
                            ["responded"] = new { type = "boolean" },
                            ["position"] = Nullable("string"),
                            ["keyPoints"] = Nullable("string")
                        }
                    }
                },
                ["votes"] = new { type = "object", additionalProperties = false, required = memberIds, properties = voteProps },
                ["conditions"] = new { type = "array", items = new { type = "string" } },
                ["dissent"] = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "member", "concern" },
                        properties = new Dictionary<string, object>
                        {
                            ["member"] = new { type = "string" },
                            ["concern"] = new { type = "string" }
                        }
                    }
                },
                ["risks"] = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "category", "description", "mitigation" },
                        properties = new Dictionary<string, object>
                        {
                            ["category"] = new { type = "string" },
                            ["description"] = new { type = "string" },
                            ["mitigation"] = new { type = "string" }
                        }
                    }
                }
            }
        };
        return JsonSerializer.SerializeToElement(schema);
    }
}
