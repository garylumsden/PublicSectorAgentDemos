using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Defra.Policy;
using Demo2.Web.Configuration;
using Microsoft.Extensions.Options;

namespace Demo2.Web.Domain;

public sealed record WelfareRuleSet(
    string ContractVersion,
    string RuleSetId,
    bool DemonstrationOnly,
    [property: JsonPropertyName("repeatedUnmetNeedsWindowDays")] int RepeatComplaintWindowDays,
    [property: JsonPropertyName("repeatedUnmetNeedsMinimumCount")] int RepeatComplaintMinimumCount,
    int EmergencyScoreMinimum,
    int HighPriorityScoreMinimum,
    int MediumPriorityScoreMinimum,

    string[] EmergencySignals,
    string[] HumanApprovalRequiredFor,
    bool AllowAutomaticClosure);

public interface IWelfareRuleProvider
{
    WelfareRuleSet Rules { get; }
}

public sealed class WelfareRuleProvider : IWelfareRuleProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public WelfareRuleProvider(
        Demo2ConfigurationPathResolver pathResolver,
        IOptions<Demo2Options> options)
    {
        string path = pathResolver.ResolveExistingFile(options.Value.RulesPath);
        WelfareRuleSet? rules = JsonSerializer.Deserialize<WelfareRuleSet>(
            File.ReadAllText(path),
            SerializerOptions);
        Rules = Validate(rules);
    }

    public WelfareRuleSet Rules { get; }

    private static WelfareRuleSet Validate(WelfareRuleSet? rules)
    {
        if (rules is null ||
            string.IsNullOrWhiteSpace(rules.ContractVersion) ||
            string.IsNullOrWhiteSpace(rules.RuleSetId) ||
            !rules.DemonstrationOnly ||
            rules.RepeatComplaintWindowDays <= 0 ||
            rules.RepeatComplaintMinimumCount <= 0 ||
            rules.EmergencyScoreMinimum is < 1 or > 100 ||
            rules.HighPriorityScoreMinimum is < 1 or > 100 ||
            rules.MediumPriorityScoreMinimum is < 1 or > 100 ||
            rules.HighPriorityScoreMinimum <= rules.MediumPriorityScoreMinimum ||
            rules.EmergencySignals is null ||
            rules.EmergencySignals.Length == 0 ||
            rules.EmergencySignals.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("The Demo2 flood-support rule set is invalid.");
        }

        return rules;
    }
}

public interface IDemo2ToolPolicyProvider
{
    PolicyConfiguration Policy { get; }
}

public sealed class Demo2ToolPolicyProvider : IDemo2ToolPolicyProvider
{
    public Demo2ToolPolicyProvider(
        Demo2ConfigurationPathResolver pathResolver,
        IOptions<Demo2Options> options)
    {
        string path = pathResolver.ResolveExistingFile(options.Value.ToolPolicyPath);
        Policy = new JsonPolicyLoader().Load(File.ReadAllText(path));
    }

    public PolicyConfiguration Policy { get; }
}

public sealed class ComplaintInputGuard
{
    private static readonly Regex SecretPattern = new(
        @"(?ix)(?:api[_-]?key|client[_-]?secret|password)\s*[:=]\s*\S+|AKIA[0-9A-Z]{16}",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex EmailPattern = new(
        @"(?i)\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private static readonly Regex PhonePattern = new(
        @"(?<!\w)(?:\+44\s?\d{4}|\(?0\d{3,4}\)?)\s?\d{3,4}\s?\d{3,4}(?!\w)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    private readonly PolicyScreener _policyScreener;
    private readonly int _maxComplaintCharacters;

    public ComplaintInputGuard(
        IDemo2ToolPolicyProvider policyProvider,
        IOptions<Demo2Options> options)
    {
        _policyScreener = new(policyProvider.Policy);
        _maxComplaintCharacters = options.Value.MaxComplaintCharacters;
    }

    public InputGuardResult Screen(string? complaint)
    {
        string input = complaint?.Trim() ?? string.Empty;
        string hash = Hash(input);

        if (input.Length == 0 || input.Length > _maxComplaintCharacters)
        {
            return new(false, "input.invalid_length", string.Empty, hash);
        }

        PolicyDecision policyDecision = _policyScreener.ScreenInput(input);
        if (!policyDecision.IsAllowed)
        {
            return new(false, policyDecision.Code, string.Empty, hash);
        }

        if (SecretPattern.IsMatch(input))
        {
            return new(false, "input.secret_detected", string.Empty, hash);
        }

        return new(true, "allowed", RedactPii(input), hash);
    }

    public static string RedactPii(string value)
    {
        string redacted = EmailPattern.Replace(value, "[redacted-email]");
        return PhonePattern.Replace(redacted, "[redacted-phone]");
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed class WelfareDeterministicPolicy(IWelfareRuleProvider ruleProvider)
{
    public WelfareRuleSet Rules => ruleProvider.Rules;

    public EmergencyPolicyResult EvaluateEmergency(string sanitizedComplaint)
    {
        string normalizedComplaint = NormalizeSignal(sanitizedComplaint);
        string[] matched = Rules.EmergencySignals
            .Where(signal => normalizedComplaint.Contains(NormalizeSignal(signal), StringComparison.Ordinal))
            .ToArray();
        int score = matched.Length == 0 ? 0 : 100;
        return new(score >= Rules.EmergencyScoreMinimum, score, matched);
    }

    public PostModelPolicyResult ApplyPostModel(
        AgentAssessment assessment,
        WelfareCaseRecord? previousCase)
    {
        WelfareUrgency urgency = assessment.Urgency;
        WelfareRoute route = NormalizeRoute(assessment.Urgency, assessment.Route);
        List<string> reasons = ["agent-assessment"];
        bool repeatEscalated = false;
        bool continuationFloorApplied = false;

        if (assessment.RepeatComplaintCount >= Rules.RepeatComplaintMinimumCount)
        {
            urgency = WelfareUrgency.High;
            route = WelfareRoute.PriorityInspectorReview;
            repeatEscalated = true;
            reasons.Add("repeated-unmet-needs-high");
        }

        if (previousCase is not null && urgency < previousCase.Urgency)
        {
            urgency = previousCase.Urgency;
            route = previousCase.Route;
            continuationFloorApplied = true;
            reasons.Add("continuation-no-downgrade");
        }

        return new(urgency, route, reasons, repeatEscalated, continuationFloorApplied);
    }

    public static WelfareRoute NormalizeRoute(WelfareUrgency urgency, WelfareRoute requested) =>
        urgency switch
        {
            WelfareUrgency.High when requested == WelfareRoute.ImmediateHumanEscalation =>
                WelfareRoute.ImmediateHumanEscalation,
            WelfareUrgency.High => WelfareRoute.PriorityInspectorReview,
            WelfareUrgency.Medium => WelfareRoute.StandardInspectorReview,
            WelfareUrgency.Low => WelfareRoute.RecordAndClose,
            _ => WelfareRoute.RequestClarification
        };

    private static string NormalizeSignal(string value)
    {
        StringBuilder builder = new(value.Length);
        bool previousWasSpace = false;
        foreach (char character in value.ToLowerInvariant())
        {
            char normalized = char.IsAsciiLetterOrDigit(character) ? character : ' ';
            if (normalized == ' ')
            {
                if (!previousWasSpace)
                {
                    builder.Append(normalized);
                }

                previousWasSpace = true;
            }
            else
            {
                builder.Append(normalized);
                previousWasSpace = false;
            }
        }

        return builder.ToString().Trim();
    }
}
