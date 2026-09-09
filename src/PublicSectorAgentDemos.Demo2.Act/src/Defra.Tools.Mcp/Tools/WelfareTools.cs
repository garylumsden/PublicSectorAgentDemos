using System.ComponentModel;
using Defra.Contracts.FloodSupport;
using Defra.Tools.Mcp.Contracts;
using Defra.Tools.Mcp.Providers;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Defra.Tools.Mcp.Tools;

[McpServerToolType]
public static class WelfareTools
{
    [McpServerTool(Name = McpToolNames.LookupFarmRegister, Title = "Get situation reports",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(McpToolResponse<SituationReport>))]
    [Description("Read flood situation reports, displaced household demand, capacity deadlines and repeated unmet needs. Preserve evidence timestamps.")]
    public static ValueTask<CallToolResult> LookupFarmRegister(
        [Description("Fictional area situation request.")] FarmRegisterInput? request,
        McpToolExecutor executor, IWelfareDataProvider provider, CancellationToken cancellationToken) =>
        executor.ExecuteAsync(McpToolNames.LookupFarmRegister, request, McpValidation.FarmRegister,
            provider.LookupFarmRegisterAsync, cancellationToken);

    [McpServerTool(Name = McpToolNames.GetComplaintHistory, Title = "Find accommodation",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(McpToolResponse<AccommodationOutput>))]
    [Description("Read bounded accommodation packages and accessible rooms. Retain the older 90-room versus latest 60-room Riverton discrepancy as an open risk.")]
    public static ValueTask<CallToolResult> GetComplaintHistory(
        [Description("Fictional area accommodation request.")] ComplaintHistoryInput? request,
        McpToolExecutor executor, IWelfareDataProvider provider, CancellationToken cancellationToken) =>
        executor.ExecuteAsync(McpToolNames.GetComplaintHistory, request, McpValidation.ComplaintHistory,
            provider.GetComplaintHistoryAsync, cancellationToken);

    [McpServerTool(Name = McpToolNames.GetWeatherData, Title = "Check transport capacity",
        ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(McpToolResponse<TransportCapacityOutput>))]
    [Description("Read coach capacity and accessible transport places for an area. Seats do not establish household coverage or evacuation authority.")]
    public static ValueTask<CallToolResult> GetWeatherData(
        [Description("Fictional area transport request.")] WeatherDataInput? request,
        McpToolExecutor executor, IWelfareDataProvider provider, CancellationToken cancellationToken) =>
        executor.ExecuteAsync(McpToolNames.GetWeatherData, request, McpValidation.WeatherData,
            provider.GetWeatherDataAsync, cancellationToken);

    [McpServerTool(Name = McpToolNames.DispatchVet, Title = "Reserve support package",
        ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false,
        UseStructuredContent = true, OutputSchemaType = typeof(McpToolResponse<SupportReservationReceipt>))]
    [Description("Request approval for an exact versioned accommodation and transport package. Foundry must pause for human approval. Approval applies only to that package.")]
    public static ValueTask<CallToolResult> DispatchVet(
        [Description("Exact six-field package request. Approval remains external policy state.")] SupportPackageRequest? request,
        McpToolExecutor executor, IHighImpactActionAuthorizer actionAuthorizer,
        IWelfareDataProvider provider, CancellationToken cancellationToken) =>
        executor.ExecuteAsync(McpToolNames.DispatchVet, request, McpValidation.VetDispatch,
            (input, token) =>
            {
                actionAuthorizer.EnsureAuthorized(HighImpactActionDomains.Welfare);
                return provider.DispatchVetAsync(input, token);
            }, cancellationToken);
}
