using System.Text.Json;
using System.Text.RegularExpressions;

namespace PublicSectorAgentDemos.Demo4.HostedAgents;

public sealed record HostedAgentGuardDecision(bool IsAllowed, string Code)
{
    public static HostedAgentGuardDecision Allowed { get; } = new(true, "allowed");
}

public sealed partial class HostedAgentInputGuard(
    int maximumInputCharacters = 8_000,
    int maximumRequestBytes = 64 * 1024)
{
    private const int MaximumApprovalRequestIdCharacters = 128;
    private const int MaximumEnvelopeIdentifierCharacters = 256;
    private static readonly HashSet<string> ApprovalEnvelopeProperties =
        new(StringComparer.Ordinal)
        {
            "model",
            "conversation",
            "agent_session_id",
            "input",
            "stream"
        };

    public HostedAgentGuardDecision Screen(ReadOnlyMemory<byte> body)
    {
        if (body.Length == 0 || body.Length > maximumRequestBytes)
        {
            return new(false, "input.request_size");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("input", out JsonElement input))
            {
                return new(false, "input.missing");
            }

            ApprovalContinuationState approvalState =
                ClassifyApprovalContinuation(document.RootElement, input);
            if (approvalState == ApprovalContinuationState.Valid)
            {
                return HostedAgentGuardDecision.Allowed;
            }

            if (approvalState == ApprovalContinuationState.Invalid)
            {
                return new(false, "input.approval_invalid");
            }

            List<string> values = [];
            CollectStrings(input, values);
            string text = string.Join('\n', values);
            if (string.IsNullOrWhiteSpace(text) ||
                text.Length > maximumInputCharacters ||
                SecretPattern().IsMatch(text))
            {
                return new(false, "input.invalid");
            }

            return InjectionPattern().IsMatch(text)
                ? new(false, "input.injection_detected")
                : HostedAgentGuardDecision.Allowed;
        }
        catch (JsonException)
        {
            return new(false, "input.invalid_json");
        }
    }

    private static ApprovalContinuationState ClassifyApprovalContinuation(
        JsonElement root,
        JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Array)
        {
            return ApprovalContinuationState.NotApproval;
        }

        JsonElement[] items = input.EnumerateArray().ToArray();
        if (items.Length == 0)
        {
            return ApprovalContinuationState.NotApproval;
        }

        bool hasApprovalMarker = items.Any(item =>
            item.ValueKind == JsonValueKind.Object &&
            item.EnumerateObject().Any(property =>
                property.Name is "approval_request_id" or "approve" ||
                property.NameEquals("type") &&
                property.Value.ValueKind == JsonValueKind.String &&
                string.Equals(
                    property.Value.GetString(),
                    "mcp_approval_response",
                    StringComparison.Ordinal)));
        if (!hasApprovalMarker)
        {
            return ApprovalContinuationState.NotApproval;
        }

        HashSet<string> envelopeProperties = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!envelopeProperties.Add(property.Name))
            {
                return ApprovalContinuationState.Invalid;
            }
        }

        bool hasAgentReference = envelopeProperties.Remove("agent_reference");
        if (!envelopeProperties.SetEquals(ApprovalEnvelopeProperties) ||
            hasAgentReference &&
            (!root.TryGetProperty("agent_reference", out JsonElement agentReference) ||
             !IsValidAgentReference(agentReference)) ||
            !root.TryGetProperty("model", out JsonElement model) ||
            !IsSafeIdentifier(model, MaximumApprovalRequestIdCharacters) ||
            !root.TryGetProperty("conversation", out JsonElement conversation) ||
            !IsSafeIdentifier(conversation, MaximumEnvelopeIdentifierCharacters) ||
            !root.TryGetProperty("agent_session_id", out JsonElement agentSessionId) ||
            !IsSafeIdentifier(agentSessionId, MaximumEnvelopeIdentifierCharacters) ||
            !root.TryGetProperty("stream", out JsonElement stream) ||
            stream.ValueKind != JsonValueKind.False ||
            items.Length != 1)
        {
            return ApprovalContinuationState.Invalid;
        }

        JsonElement item = items[0];
        if (item.ValueKind != JsonValueKind.Object)
        {
            return ApprovalContinuationState.Invalid;
        }

        HashSet<string> properties = new(StringComparer.Ordinal);
        foreach (JsonProperty property in item.EnumerateObject())
        {
            if (property.Name is not ("type" or "approval_request_id" or "approve") ||
                !properties.Add(property.Name))
            {
                return ApprovalContinuationState.Invalid;
            }
        }

        if (properties.Count != 3 ||
            !item.TryGetProperty("type", out JsonElement type) ||
            type.ValueKind != JsonValueKind.String ||
            !string.Equals(
                type.GetString(),
                "mcp_approval_response",
                StringComparison.Ordinal) ||
            !item.TryGetProperty("approval_request_id", out JsonElement requestId) ||
            !IsSafeIdentifier(requestId, MaximumApprovalRequestIdCharacters) ||
            !item.TryGetProperty("approve", out JsonElement approve) ||
            approve.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return ApprovalContinuationState.Invalid;
        }

        return ApprovalContinuationState.Valid;
    }

    private static bool IsValidAgentReference(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        Dictionary<string, JsonElement> properties = new(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value))
            {
                return false;
            }
        }
        return properties.Count == 3 &&
            properties.TryGetValue("type", out JsonElement type) &&
            type.ValueKind == JsonValueKind.String &&
            type.GetString() == "agent_reference" &&
            properties.TryGetValue("name", out JsonElement name) &&
            name.ValueKind == JsonValueKind.String &&
            name.GetString() == "demo4-hosted-agent" &&
            properties.TryGetValue("version", out JsonElement version) &&
            version.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(version.GetString()) &&
            version.GetString()!.Length <= 32 &&
            version.GetString()!.All(char.IsAsciiDigit);
    }

    private static bool IsSafeIdentifier(JsonElement element, int maximumCharacters)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? value = element.GetString();
        return !string.IsNullOrWhiteSpace(value) &&
               value.Length <= maximumCharacters &&
               value.All(character =>
                   char.IsAsciiLetterOrDigit(character) ||
                   character is '-' or '_' or '.' or ':');
    }

    private static void CollectStrings(JsonElement element, ICollection<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                values.Add(element.GetString() ?? string.Empty);
                break;
            case JsonValueKind.Array:
                foreach (JsonElement child in element.EnumerateArray())
                {
                    CollectStrings(child, values);
                }

                break;
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (property.NameEquals("content") ||
                        property.NameEquals("text") ||
                        property.NameEquals("input"))
                    {
                        CollectStrings(property.Value, values);
                    }
                }

                break;
        }
    }

    [GeneratedRegex(
        @"\b(ignore|disregard)\s+(all\s+)?(previous|prior|system)\s+instructions?\b|\b(bypass|override)\s+(policy|safety|system|developer)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100)]
    private static partial Regex InjectionPattern();

    [GeneratedRegex(
        @"\b(api[_-]?key|password|client[_-]?secret|access[_-]?token)\s*[:=]\s*\S+|\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100)]
    private static partial Regex SecretPattern();

    private enum ApprovalContinuationState
    {
        NotApproval,
        Valid,
        Invalid
    }
}
