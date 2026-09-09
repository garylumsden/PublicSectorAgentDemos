#pragma warning disable OPENAI001

using System.Text.Json;
using System.Text.Json.Nodes;
using Defra.Contracts.FloodSupport;
using Demo2.Web.Agent;
using Defra.Contracts.V1;
using Demo2.Web.Domain;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace Defra.UnitTests;

public sealed class Demo2AgentResponseParserTests
{
    [Theory]
    [InlineData("capacity.support_unavailable", true)]
    [InlineData("reservation.generation_changed", true)]
    [InlineData("audit.unavailable", false)]
    [InlineData("toolbox.idempotency_conflict", false)]
    public void ReservationRejection_UsesOnlyDefinitiveBoundToolResults(string code, bool definite)
    {
        SupportPackageRequest package = new("approval-33333333333333333333333333333333", "AREA-1001", "INC-2001",
            "PKG-RIV-060", 1, "Support is required.");
        AgentAssessmentRequest issued = new("case-1", "AREA-1001", "Support is required.", DateTimeOffset.UtcNow,
            7, WelfareUrgency.High, null, package.ActionRequestId, package.IncidentReference, null, null);
        McpServerToolCallContent call = new("call-rejected", "reserveSupportPackage", "flood-support")
        {
            Arguments = new Dictionary<string, object?>(FloodSupportCatalogue.ToArguments(package))
        };
        string body = JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0", isSuccess = false, data = (object?)null, error = new { code }
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        McpServerToolResultContent result = new(call.CallId) { Outputs = [new TextContent(body)] };
        ChatMessage[] messages = [new(ChatRole.Assistant, [call]), new(ChatRole.Tool, [result])];
        if (definite)
        {
            SupportReservationNotExecutedException error = Assert.Throws<SupportReservationNotExecutedException>(
                () => AgentResponseParser.ThrowIfCapacityRejected(messages, issued));
            Assert.Equal(package.ActionRequestId, error.ActionRequestId);
            Assert.Equal(code, error.ErrorCode);
        }
        else
        {
            AgentResponseParser.ThrowIfCapacityRejected(messages, issued);
        }
        AgentResponseParser.ThrowIfCapacityRejected(messages, issued with
        {
            DispatchActionRequestId = "approval-44444444444444444444444444444444"
        });
        AgentResponseParser.ThrowIfCapacityRejected([new ChatMessage(ChatRole.Assistant, body)], issued);
    }

    [Fact]
    public void ReservationRejection_UsesTheExactApprovedCallWhenReturnedArgumentsChange()
    {
        const string callId = "call-approved";
        const string actionRequestId = "approval-33333333333333333333333333333333";
        AgentAssessmentRequest issued = new("case-1", "AREA-1001", "Support is required.", DateTimeOffset.UtcNow,
            7, WelfareUrgency.High, null, actionRequestId, "INC-2001", null, null)
        {
            ReservationGeneration = "generation-1"
        };
        McpServerToolCallContent call = new(callId, "reserveSupportPackage", "flood-support")
        {
            Arguments = new Dictionary<string, object?>()
        };
        string body = JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            isSuccess = false,
            data = (object?)null,
            error = new { code = "capacity.support_unavailable" }
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        McpServerToolResultContent result = new(callId) { Outputs = [new TextContent(body)] };
        ChatMessage[] messages = [new(ChatRole.Assistant, [call]), new(ChatRole.Tool, [result])];

        SupportReservationNotExecutedException error = Assert.Throws<SupportReservationNotExecutedException>(() =>
            AgentResponseParser.ThrowIfCapacityRejected(messages, issued, new HashSet<string>(StringComparer.Ordinal) { callId }));

        Assert.Equal(actionRequestId, error.ActionRequestId);
        Assert.Equal("capacity.support_unavailable", error.ErrorCode);
    }

    [Fact]
    public void Prompt_UsesStrictStructuredOutput()
    {
        ChatResponseFormatJson format = Assert.IsType<ChatResponseFormatJson>(
            WelfareAgentPrompt.CreateResponseFormat());

        Assert.NotNull(format.Schema);
        Assert.Equal("flood_support_assessment", format.SchemaName);
    }

    [Fact]
    public void EmptyResponse_FailsAsIncompleteContractOutput()
    {
        AgentResponseFormatException exception = Assert.Throws<AgentResponseFormatException>(
            () => AgentResponseParser.Parse("", "resp_123", "conv_123"));

        Assert.Contains("incomplete", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyFoundrySourceResponse_FailsBeforeStructuredFormatting()
    {
        AgentResponse response = new([]);

        AgentResponseFormatException exception = Assert.Throws<AgentResponseFormatException>(
            () => AgentResponseParser.EnsureSourceResponseHasContent(response));

        Assert.Contains("failed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("urgency")]
    [InlineData("route")]
    [InlineData("summary")]
    [InlineData("evidence")]
    [InlineData("repeatedUnmetNeedsCount")]
    [InlineData("sourceConflictPresent")]
    [InlineData("approvalRecommended")]
    public void MissingRequiredProperty_FailsClosed(string propertyName)
    {
        JsonObject document = JsonNode.Parse(
            """
            {
              "urgency": "Medium",
              "route": "StandardSupportReview",
              "summary": "Housing concern requires review.",
              "evidence": ["complaint narrative"],
              "repeatedUnmetNeedsCount": 0,
              "sourceConflictPresent": false,
              "approvalRecommended": false
            }
            """)!.AsObject();
        Assert.True(document.Remove(propertyName));

        AgentResponseFormatException exception = Assert.Throws<AgentResponseFormatException>(
            () => AgentResponseParser.Parse(
                document.ToJsonString(),
                "response-missing",
                "conversation-missing"));

        Assert.Contains("valid contract JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MafStructuredResponse_ProducesAssessmentAndSanitizedToolTrace()
    {
        McpServerToolCallContent call = new(
            "call-1",
            "getSituationReports",
            "flood-support")
        {
            Arguments = new Dictionary<string, object?>
            {
                ["areaReference"] = "SENSITIVE-AREA"
            }
        };
        McpServerToolResultContent result = new("call-1")
        {
            Outputs = [new TextContent("Synthetic lookup completed.")]
        };
        string json = JsonSerializer.Serialize(
            new WelfareAgentResponseDocument(
                WelfareUrgency.Medium,
                WelfareRoute.StandardInspectorReview,
                "Synthetic assessment.",
                ["Fictional area context"],
                1,
                true,
                false),
            WelfareAgentPrompt.SerializerOptions);
        AgentResponse raw = new(
        [
            new ChatMessage(ChatRole.Assistant, [call]),
            new ChatMessage(ChatRole.Tool, [result]),
            new ChatMessage(ChatRole.Assistant, json)
        ])
        {
            ResponseId = "resp_123",
            CreatedAt = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero)
        };
        AgentResponse<WelfareAgentResponseDocument> response =
            new(raw, WelfareAgentPrompt.SerializerOptions);

        AgentAssessment assessment = AgentResponseParser.Parse(response, "conv_123");

        Assert.Equal(WelfareUrgency.Medium, assessment.Urgency);
        ToolTraceItem trace = Assert.Single(assessment.ToolTrace);
        Assert.Equal(ToolCallStatus.Succeeded, trace.Status);
        Assert.DoesNotContain(
            "SENSITIVE-AREA",
            JsonSerializer.Serialize(assessment.ToolTrace),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ToolItems_ProduceSanitizedTraceWithoutArgumentValues()
    {
        McpToolCallItem call = ResponseItem.CreateMcpToolCallItem(
            "flood-support",
            "getSituationReports",
            BinaryData.FromObjectAsJson(new
            {
                areaReference = "SENSITIVE-AREA"
            }));

        AgentToolTrace result = AgentResponseParser.ParseToolItems(
            [call],
            "resp_123",
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));

        ToolTraceItem trace = Assert.Single(result.ToolTrace);
        Assert.Equal("getSituationReports", trace.ToolName);
        Assert.Equal(ToolCallStatus.Succeeded, trace.Status);
        Assert.Empty(result.ApprovalRequests);
        string serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("SENSITIVE-AREA", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void ApprovalItems_ExposeOnlyArgumentNames()
    {
        McpToolCallApprovalRequestItem approval = ResponseItem.CreateMcpApprovalRequestItem(
            "approval_123",
            "flood-support",
            "reserveSupportPackage",
            BinaryData.FromObjectAsJson(new
            {
                areaReference = "SENSITIVE-AREA",
                incidentReference = "SENSITIVE-INCIDENT"
            }));

        AgentToolTrace result = AgentResponseParser.ParseToolItems(
            [approval],
            "resp_123",
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));

        ApprovalRequest request = Assert.Single(result.ApprovalRequests);
        Assert.Equal(["areaReference", "incidentReference"], request.ArgumentNames);
        Assert.Equal(ToolCallStatus.Denied, Assert.Single(result.ToolTrace).Status);
        string serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("SENSITIVE-AREA", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("SENSITIVE-INCIDENT", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void DispatchToolResult_MapsCapacityRejectionToTheOperatorError()
    {
        const string actionRequestId = "approval-33333333333333333333333333333333";
        McpServerToolCallContent call = new("call-rejected-dispatch", "reserveSupportPackage", "flood-support");
        string body = JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0.0",
            isSuccess = false,
            data = (object?)null,
            error = new { code = "capacity.support_unavailable" }
        }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        McpServerToolResultContent result = new(call.CallId) { Outputs = [new TextContent(body)] };

        SupportReservationNotExecutedException error = Assert.Throws<SupportReservationNotExecutedException>(() =>
            AgentResponseParser.ParseDispatch(
                [new ChatMessage(ChatRole.Assistant, [call]), new ChatMessage(ChatRole.Tool, [result])],
                "reserveSupportPackage",
                actionRequestId));

        Assert.Equal(actionRequestId, error.ActionRequestId);
        Assert.Equal("capacity.support_unavailable", error.ErrorCode);
    }

    [Fact]
    public void DispatchToolResult_ProducesOperationalReceipt()
    {
        McpServerToolCallContent call = new(
            "call-dispatch",
            "reserveSupportPackage",
            "flood-support");
        SupportPackageRequest request = new("approval-33333333333333333333333333333333", "AREA-1001", "INC-2001",
            "PKG-RIV-060", 1, "Temporary flood support is needed.");
        DateTimeOffset recordedAt = new(2026, 9, 6, 15, 40, 0, TimeSpan.Zero);
        SupportReservationReceipt receipt = new FloodSupportReservationSimulator().Reserve(request, recordedAt);
        McpServerToolResultContent result = new("call-dispatch")
        {
            Outputs =
            [
                new TextContent(JsonSerializer.Serialize(new { schemaVersion = "1.0.0", isSuccess = true, data = receipt },
                    WelfareAgentPrompt.SerializerOptions))
            ]
        };

        WelfareDispatchRecord dispatch = Assert.IsType<WelfareDispatchRecord>(
            AgentResponseParser.ParseDispatch(
                [
                    new ChatMessage(ChatRole.Assistant, [call]),
                    new ChatMessage(ChatRole.Tool, [result])
                ],
                "reserveSupportPackage"));

        Assert.Equal(receipt.ReservationReference, dispatch.DispatchReference);
        Assert.Equal(recordedAt, dispatch.DispatchedAt);
        Assert.Equal(request, dispatch.Receipt!.Request);
    }

    [Fact]
    public void DuplicatedDispatchToolResult_ProducesOneOperationalReceipt()
    {
        McpServerToolCallContent call = new(
            "call-duplicated-dispatch",
            "reserveSupportPackage",
            "flood-support");
        SupportPackageRequest request = new("approval-44444444444444444444444444444444", "AREA-1001", "INC-2001",
            "PKG-RIV-060", 1, "Temporary flood support is needed.");
        DateTimeOffset recordedAt = new(2026, 9, 6, 15, 40, 0, TimeSpan.Zero);
        SupportReservationReceipt receipt = new FloodSupportReservationSimulator().Reserve(request, recordedAt);
        object envelope = new { schemaVersion = "1.0.0", isSuccess = true, data = receipt };
        string compact = JsonSerializer.Serialize(envelope, WelfareAgentPrompt.SerializerOptions);
        string structured = JsonSerializer.Serialize(
            new { structuredResponse = envelope },
            new JsonSerializerOptions(WelfareAgentPrompt.SerializerOptions)
            {
                WriteIndented = true
            });
        McpServerToolResultContent result = new("call-duplicated-dispatch")
        {
            Outputs = [new TextContent($"{compact}\n{structured}")]
        };

        WelfareDispatchRecord dispatch = Assert.IsType<WelfareDispatchRecord>(
            AgentResponseParser.ParseDispatch(
                [
                    new ChatMessage(ChatRole.Assistant, [call]),
                    new ChatMessage(ChatRole.Tool, [result])
                ],
                "reserveSupportPackage"));

        Assert.Equal(receipt.ReservationReference, dispatch.DispatchReference);
        Assert.Equal(request, dispatch.Receipt!.Request);
    }
}
