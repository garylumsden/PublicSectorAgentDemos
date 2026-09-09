using System.Text;
using Demo2.Web.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Demo2.Web.Agent;

public enum AgentAssessmentUpdateKind
{
    InvocationStarted,
    ToolCallStarted,
    ApprovalRequested,
    ApprovalDecided,
    ToolResultReceived,
    ResponseCompleted,
    ResponseValidated
}

public sealed record AgentAssessmentUpdate(
    AgentAssessmentUpdateKind Kind,
    string Message,
    DateTimeOffset Timestamp,
    string? ToolName = null,
    bool? Approved = null,
    AgentAssessment? Assessment = null);

public sealed class StructuredAgentResponseBuffer
{
    private const int MaximumResponseCharacters = 32_000;
    private readonly StringBuilder _buffer = new();

    public bool HasContent => _buffer.Length > 0;

    public void Append(string? chunk)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        if (_buffer.Length + chunk.Length > MaximumResponseCharacters)
        {
            throw new AgentResponseFormatException(
                "Agent response exceeded the configured size limit while streaming.");
        }

        _buffer.Append(chunk);
    }

    public AgentAssessment Complete(string responseId, string conversationId) =>
        AgentResponseParser.Parse(_buffer.ToString(), responseId, conversationId);
}

public sealed class AgentStreamingEventProjector
{
    private readonly Dictionary<string, string> _toolNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);

    public IReadOnlyList<AgentAssessmentUpdate> Project(AgentResponseUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        List<AgentAssessmentUpdate> projected = [];
        DateTimeOffset timestamp = update.CreatedAt ?? DateTimeOffset.UtcNow;
        foreach (AIContent content in update.Contents)
        {
            switch (content)
            {
                case ToolApprovalRequestContent approval:
                    string approvalTool = ToolName(approval.ToolCall);
                    _toolNames[approval.ToolCall.CallId] = approvalTool;
                    AddOnce(
                        projected,
                        $"approval:{approval.RequestId}",
                        new(
                            AgentAssessmentUpdateKind.ApprovalRequested,
                            $"Human approval requested for {approvalTool}.",
                            timestamp,
                            approvalTool));
                    break;
                case McpServerToolCallContent call:
                    _toolNames[call.CallId] = call.Name;
                    AddCall(projected, call.CallId, call.Name, "MCP tool", timestamp);
                    break;
                case FunctionCallContent call:
                    _toolNames[call.CallId] = call.Name;
                    AddCall(projected, call.CallId, call.Name, "Function", timestamp);
                    break;
                case McpServerToolResultContent result:
                    AddToolResult(projected, result.CallId, timestamp);
                    break;
                case FunctionResultContent result:
                    AddToolResult(projected, result.CallId, timestamp);
                    break;
            }
        }

        return projected;
    }

    public AgentAssessmentUpdate? ProjectApproval(
        ToolApprovalRequestContent approval,
        DateTimeOffset timestamp)
    {
        string toolName = ToolName(approval.ToolCall);
        _toolNames[approval.ToolCall.CallId] = toolName;
        string key = $"approval:{approval.RequestId}";
        return _emitted.Add(key)
            ? new(
                AgentAssessmentUpdateKind.ApprovalRequested,
                $"Human approval requested for {toolName}.",
                timestamp,
                toolName)
            : null;
    }

    private static string ToolName(ToolCallContent call) => call switch
    {
        McpServerToolCallContent mcp => mcp.Name,
        FunctionCallContent function => function.Name,
        _ => "protected-tool"
    };

    private void AddCall(
        ICollection<AgentAssessmentUpdate> projected,
        string callId,
        string toolName,
        string kind,
        DateTimeOffset timestamp) =>
        AddOnce(
            projected,
            $"call:{callId}",
            new(
                AgentAssessmentUpdateKind.ToolCallStarted,
                $"{kind} call started: {toolName}.",
                timestamp,
                toolName));

    private void AddToolResult(
        ICollection<AgentAssessmentUpdate> projected,
        string callId,
        DateTimeOffset timestamp)
    {
        string toolName = _toolNames.GetValueOrDefault(callId, "tool");
        AddOnce(
            projected,
            $"result:{callId}",
            new(
                AgentAssessmentUpdateKind.ToolResultReceived,
                $"Result received from {toolName}.",
                timestamp,
                toolName));
    }

    private void AddOnce(
        ICollection<AgentAssessmentUpdate> projected,
        string key,
        AgentAssessmentUpdate update)
    {
        if (_emitted.Add(key))
        {
            projected.Add(update);
        }
    }
}
