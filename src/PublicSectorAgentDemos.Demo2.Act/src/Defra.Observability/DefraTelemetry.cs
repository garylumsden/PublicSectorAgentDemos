using System.Diagnostics;
using Defra.Contracts.V1;

namespace Defra.Observability;

public static class DefraActivitySourceNames
{
    public const string AgentCore = "Defra.AgentCore";
    public const string Policy = "Defra.Policy";
    public const string Tools = "Defra.Tools";
    public const string Audit = "Defra.Audit";
}

public static class DefraTelemetryTags
{
    public const string CorrelationId = "defra.correlation_id";
    public const string PolicyId = "defra.policy.id";
    public const string PolicyVersion = "defra.policy.version";
    public const string ToolName = "defra.tool.name";
    public const string ToolCallId = "defra.tool.call_id";
    public const string DecisionCode = "defra.decision.code";
    public const string ApprovalRequired = "defra.approval.required";
}

public static class DefraActivities
{
    public static readonly ActivitySource AgentCore = new(DefraActivitySourceNames.AgentCore);
    public static readonly ActivitySource Policy = new(DefraActivitySourceNames.Policy);
    public static readonly ActivitySource Tools = new(DefraActivitySourceNames.Tools);
    public static readonly ActivitySource Audit = new(DefraActivitySourceNames.Audit);

    public static Activity? StartAgentTurn(
        CorrelationId correlationId,
        string operationName = "agent.turn")
    {
        Activity? activity = AgentCore.StartActivity(operationName, ActivityKind.Internal);
        activity?.SetTag(DefraTelemetryTags.CorrelationId, correlationId.Value);
        return activity;
    }

    public static Activity? StartToolCall(
        CorrelationId correlationId,
        string toolName,
        string callId)
    {
        Activity? activity = Tools.StartActivity("tool.call", ActivityKind.Client);
        activity?.SetTag(DefraTelemetryTags.CorrelationId, correlationId.Value);
        activity?.SetTag(DefraTelemetryTags.ToolName, toolName);
        activity?.SetTag(DefraTelemetryTags.ToolCallId, callId);
        return activity;
    }

    public static void SetPolicy(Activity? activity, string policyId, string policyVersion)
    {
        activity?.SetTag(DefraTelemetryTags.PolicyId, policyId);
        activity?.SetTag(DefraTelemetryTags.PolicyVersion, policyVersion);
    }

    public static void SetDecision(Activity? activity, string decisionCode) =>
        activity?.SetTag(DefraTelemetryTags.DecisionCode, decisionCode);
}
