using Defra.Contracts.V1;
using Defra.Policy;

namespace Defra.AgentCore;

public sealed class TurnPolicyEnforcer
{
    private readonly PolicyScreener _screener;
    private readonly PolicyConfiguration _policy;
    private readonly ToolCallBudget _budget;

    public TurnPolicyEnforcer(PolicyConfiguration policy)
    {
        _policy = policy;
        _screener = new(policy);
        _budget = new(policy.InputLimits.MaxToolCallsPerTurn);
    }

    public PolicyDecision ScreenInput(string? input) => _screener.ScreenInput(input);

    public PolicyDecision AuthorizeToolCall(
        ApprovalRequest request,
        IReadOnlyDictionary<string, string>? arguments,
        ApprovalDecision? approvalDecision,
        DateTimeOffset now)
    {
        PolicyDecision argumentDecision = _screener.ScreenToolArguments(request.ToolName, arguments);
        if (!argumentDecision.IsAllowed)
        {
            return argumentDecision;
        }

        PolicyDecision approval = ApprovalPolicy.Authorize(_policy, request, approvalDecision, now);
        if (!approval.IsAllowed)
        {
            return approval;
        }

        return _budget.TryConsume()
            ? PolicyDecision.Allow("tool.authorized")
            : PolicyDecision.Deny("tool.budget_exhausted");
    }
}
