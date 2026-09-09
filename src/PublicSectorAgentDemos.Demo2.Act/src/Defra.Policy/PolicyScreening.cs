using System.Text.RegularExpressions;
using Defra.Contracts.V1;

namespace Defra.Policy;

public sealed record PolicyDecision(bool IsAllowed, string Code)
{
    public static PolicyDecision Allow(string code = "allowed") => new(true, code);

    public static PolicyDecision Deny(string code) => new(false, code);
}

public sealed class PolicyScreener(PolicyConfiguration policy)
{
    private readonly PolicyConfiguration _policy = Validate(policy);
    private readonly Regex[] _injectionPatterns = policy.InjectionPatterns
        .Select(pattern => new Regex(
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100)))
        .ToArray();

    public PolicyDecision ScreenInput(string? input)
    {
        if (input is null)
        {
            return PolicyDecision.Deny("input.missing");
        }

        if (input.Length > _policy.InputLimits.MaxInputCharacters)
        {
            return PolicyDecision.Deny("input.limit_exceeded");
        }

        return ContainsInjection(input)
            ? PolicyDecision.Deny("input.injection_detected")
            : PolicyDecision.Allow();
    }

    public PolicyDecision ScreenToolArguments(string toolName, IReadOnlyDictionary<string, string>? arguments)
    {
        if (string.IsNullOrWhiteSpace(toolName) ||
            !_policy.AllowedTools.Contains(toolName, StringComparer.Ordinal))
        {
            return PolicyDecision.Deny("tool.not_allowed");
        }

        if (arguments is null)
        {
            return PolicyDecision.Deny("tool.arguments_missing");
        }

        if (arguments.Count > _policy.InputLimits.MaxToolArguments)
        {
            return PolicyDecision.Deny("tool.argument_count_exceeded");
        }

        foreach ((string key, string value) in arguments)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
            {
                return PolicyDecision.Deny("tool.argument_invalid");
            }

            if (key.Length + value.Length > _policy.InputLimits.MaxToolArgumentCharacters)
            {
                return PolicyDecision.Deny("tool.argument_limit_exceeded");
            }

            if (ContainsInjection(key) || ContainsInjection(value))
            {
                return PolicyDecision.Deny("tool.argument_injection_detected");
            }
        }

        return PolicyDecision.Allow();
    }

    private bool ContainsInjection(string value)
    {
        try
        {
            return _injectionPatterns.Any(pattern => pattern.IsMatch(value));
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    private static PolicyConfiguration Validate(PolicyConfiguration policy)
    {
        PolicyValidationResult validation = PolicyValidator.Validate(policy);
        return validation.IsValid ? policy : throw new PolicyValidationException(validation.Errors);
    }
}

public static class ApprovalPolicy
{
    public static PolicyDecision Authorize(
        PolicyConfiguration policy,
        ApprovalRequest request,
        ApprovalDecision? decision,
        DateTimeOffset now)
    {
        PolicyValidationResult validation = PolicyValidator.Validate(policy);
        if (!validation.IsValid)
        {
            return PolicyDecision.Deny("policy.invalid");
        }

        if (!policy.AllowedTools.Contains(request.ToolName, StringComparer.Ordinal))
        {
            return PolicyDecision.Deny("tool.not_allowed");
        }

        ToolApprovalRule? rule = policy.ToolApprovals.SingleOrDefault(
            candidate => string.Equals(candidate.ToolName, request.ToolName, StringComparison.Ordinal));

        if (rule?.RequiresApproval != true)
        {
            return PolicyDecision.Allow("approval.not_required");
        }

        if (decision is null)
        {
            return PolicyDecision.Deny("approval.required");
        }

        if (request.ExpiresAt < now)
        {
            return PolicyDecision.Deny("approval.expired");
        }

        if (!string.Equals(decision.RequestId, request.RequestId, StringComparison.Ordinal) ||
            decision.CorrelationId != request.CorrelationId ||
            !string.Equals(decision.ToolName, request.ToolName, StringComparison.Ordinal))
        {
            return PolicyDecision.Deny("approval.mismatch");
        }

        if (decision.DecidedAt < request.RequestedAt ||
            decision.DecidedAt > request.ExpiresAt ||
            decision.DecidedAt > now)
        {
            return PolicyDecision.Deny("approval.invalid_time");
        }

        return decision.Outcome == ApprovalOutcome.Approved
            ? PolicyDecision.Allow("approval.approved")
            : PolicyDecision.Deny("approval.denied");
    }
}
