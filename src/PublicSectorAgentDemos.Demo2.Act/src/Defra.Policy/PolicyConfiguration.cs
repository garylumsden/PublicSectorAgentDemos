using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Defra.Policy;

public sealed record PolicyConfiguration(
    string PolicyId,
    string Version,
    string[] AllowedTools,
    ToolApprovalRule[] ToolApprovals,
    PolicyInputLimits InputLimits,
    string[] InjectionPatterns,
    Dictionary<string, string> RuleValues);

public sealed record ToolApprovalRule(string ToolName, bool RequiresApproval);

public sealed record PolicyInputLimits(
    int MaxInputCharacters,
    int MaxToolArguments,
    int MaxToolArgumentCharacters,
    int MaxToolCallsPerTurn);

public sealed record PolicyValidationResult(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class PolicyValidator
{
    public static PolicyValidationResult Validate(PolicyConfiguration? policy)
    {
        List<string> errors = [];
        if (policy is null)
        {
            return new(["Policy document is required."]);
        }

        RequireText(policy.PolicyId, "PolicyId", errors);
        RequireText(policy.Version, "Version", errors);

        if (policy.AllowedTools is null || policy.AllowedTools.Length == 0)
        {
            errors.Add("AllowedTools must contain at least one tool.");
        }
        else
        {
            ValidateUniqueNames(policy.AllowedTools, "AllowedTools", errors);
        }

        if (policy.ToolApprovals is null)
        {
            errors.Add("ToolApprovals is required.");
        }
        else
        {
            string[] approvalTools = policy.ToolApprovals.Select(rule => rule.ToolName).ToArray();
            ValidateUniqueNames(approvalTools, "ToolApprovals", errors);

            HashSet<string> allowedTools = new(policy.AllowedTools ?? [], StringComparer.Ordinal);
            foreach (string tool in approvalTools.Where(tool => !allowedTools.Contains(tool)))
            {
                errors.Add($"ToolApprovals contains tool '{tool}' which is not in AllowedTools.");
            }

            foreach (string tool in allowedTools.Where(tool => !approvalTools.Contains(tool, StringComparer.Ordinal)))
            {
                errors.Add($"Allowed tool '{tool}' must have an explicit ToolApprovals rule.");
            }
        }

        if (policy.InputLimits is null)
        {
            errors.Add("InputLimits is required.");
        }
        else if (policy.InputLimits.MaxInputCharacters <= 0 ||
                 policy.InputLimits.MaxToolArguments <= 0 ||
                 policy.InputLimits.MaxToolArgumentCharacters <= 0 ||
                 policy.InputLimits.MaxToolCallsPerTurn <= 0)
        {
            errors.Add("All input and tool-call limits must be greater than zero.");
        }

        if (policy.InjectionPatterns is null)
        {
            errors.Add("InjectionPatterns is required.");
        }
        else
        {
            foreach (string pattern in policy.InjectionPatterns)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    errors.Add("InjectionPatterns cannot contain empty patterns.");
                    continue;
                }

                try
                {
                    _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    errors.Add($"Injection pattern '{pattern}' is not a valid regular expression.");
                }
            }
        }

        if (policy.RuleValues is null)
        {
            errors.Add("RuleValues is required.");
        }
        else if (policy.RuleValues.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null))
        {
            errors.Add("RuleValues must use non-empty keys and non-null deterministic string values.");
        }

        return new(errors.AsReadOnly());
    }

    private static void RequireText(string? value, string name, ICollection<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{name} is required.");
        }
    }

    private static void ValidateUniqueNames(IEnumerable<string> names, string name, ICollection<string> errors)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string tool in names)
        {
            if (string.IsNullOrWhiteSpace(tool))
            {
                errors.Add($"{name} cannot contain empty tool names.");
            }
            else if (!seen.Add(tool))
            {
                errors.Add($"{name} contains duplicate tool '{tool}'.");
            }
        }
    }
}

public sealed class PolicyValidationException : Exception
{
    public PolicyValidationException(IReadOnlyList<string> errors)
        : base($"Policy validation failed: {string.Join(" ", errors)}")
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

public interface IPolicyLoader
{
    PolicyConfiguration Load(string json);
}

public sealed class JsonPolicyLoader : IPolicyLoader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public PolicyConfiguration Load(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new PolicyValidationException(["Policy JSON is required."]);
        }

        PolicyConfiguration? policy;
        try
        {
            policy = JsonSerializer.Deserialize<PolicyConfiguration>(json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new PolicyValidationException([$"Policy JSON is invalid: {exception.Message}"]);
        }

        PolicyValidationResult validation = PolicyValidator.Validate(policy);
        if (!validation.IsValid)
        {
            throw new PolicyValidationException(validation.Errors);
        }

        return policy!;
    }
}
