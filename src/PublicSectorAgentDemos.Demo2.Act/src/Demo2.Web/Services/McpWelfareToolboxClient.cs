using System.Text.Json;
using Azure.Core;
using Defra.Contracts.FloodSupport;
using Demo2.Web.Configuration;
using Demo2.Web.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Demo2.Web.Services;

public sealed class WelfareToolboxException(string errorCode, string message) : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
}

public sealed class McpWelfareToolboxClient(
    IOptions<Demo2Options> options,
    TokenCredential credential) : IWelfareToolboxClient
{
    private const string ContractVersion = "1.0.0";
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);
    private readonly Demo2ToolboxOptions _options = options.Value.Toolbox;
    private readonly TokenCredential _credential =
        credential ?? throw new ArgumentNullException(nameof(credential));

    public Task<ReservationInventoryState> GetInventoryStateAsync(CancellationToken cancellationToken = default) =>
        SendInventoryRequestAsync(HttpMethod.Get, "/admin/reservations", cancellationToken);

    public Task<ReservationInventoryState> ResetReservationsAsync(CancellationToken cancellationToken = default) =>
        SendInventoryRequestAsync(HttpMethod.Post, "/admin/reservations/reset", cancellationToken);

    private async Task<ReservationInventoryState> SendInventoryRequestAsync(
        HttpMethod method, string path, CancellationToken cancellationToken)
    {
        using HttpClientHandler handler = new() { AllowAutoRedirect = false };
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds) };
        using HttpRequestMessage request = new(method, new Uri(new Uri(_options.Endpoint), path));
        if (!string.IsNullOrWhiteSpace(_options.Audience))
        {
            AccessToken token = await _credential.GetTokenAsync(
                new TokenRequestContext([GetScope(_options.Audience)]), cancellationToken);
            request.Headers.Authorization = new("Bearer", token.Token);
        }
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        ReservationInventoryState? state = await response.Content.ReadFromJsonAsync<ReservationInventoryState>(
            SerializerOptions, cancellationToken);
        if (state is null || string.IsNullOrWhiteSpace(state.Generation) || state.ReservationCount < 0)
        {
            throw new WelfareToolboxException("inventory.invalid_response", "The reservation service returned an invalid state.");
        }
        return state;
    }

    public async Task<VetDispatchReceipt> DispatchVetAsync(
        WelfareCaseRecord caseRecord,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caseRecord);
        if (string.IsNullOrWhiteSpace(caseRecord.FarmReference))
        {
            throw new WelfareToolboxException(
                "toolbox.farm_reference_required",
                "Support reservation requires a verified area reference.");
        }
        if (string.IsNullOrWhiteSpace(caseRecord.ApprovalRequestId))
        {
            throw new WelfareToolboxException(
                "toolbox.approval_request_required",
                "Support reservation requires an approval request reference.");
        }

        if (caseRecord.Status != WelfareCaseStatus.DispatchApproved)
        {
            throw new InvalidOperationException("Support reservation requires approved case state.");
        }
        SupportPackageRequest approvedRequest = FloodSupportApprovalGuard.ValidateCasePackage(caseRecord);
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(_options.Audience))
        {
            AccessToken token = await _credential.GetTokenAsync(
                new TokenRequestContext([GetScope(_options.Audience)]),
                cancellationToken).ConfigureAwait(false);
            headers["Authorization"] = $"Bearer {token.Token}";
        }

        HttpClientTransport transport = new(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(_options.Endpoint, UriKind.Absolute),
                Name = "demo2-flood-support-toolbox",
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromSeconds(_options.ConnectionTimeoutSeconds),
                AdditionalHeaders = headers
            },
            NullLoggerFactory.Instance);
        await using McpClient client = await McpClient.CreateAsync(
            transport,
            clientOptions: null,
            loggerFactory: NullLoggerFactory.Instance,
            cancellationToken).ConfigureAwait(false);
        CallToolResult result = await client.CallToolAsync(
            _options.DispatchToolName,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["request"] = FloodSupportCatalogue.ToArguments(approvedRequest)
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.StructuredContent.HasValue)
        {
            throw new WelfareToolboxException(
                "toolbox.invalid_response",
                "Flood-support toolbox returned no structured response.");
        }

        McpToolResponse<SupportReservationReceipt>? response =
            result.StructuredContent.Value.Deserialize<McpToolResponse<SupportReservationReceipt>>(
                SerializerOptions);
        if (response is null ||
            !string.Equals(response.SchemaVersion, ContractVersion, StringComparison.Ordinal))
        {
            throw new WelfareToolboxException(
                "toolbox.contract_mismatch",
                "Flood-support toolbox returned an unsupported contract.");
        }

        if (result.IsError == true || !response.IsSuccess)
        {
            throw new WelfareToolboxException(
                response.Error?.Code ?? "toolbox.call_failed",
                response.Error?.SafeDetail ?? "Flood-support toolbox call failed.");
        }

        SupportReservationReceipt dispatch = response.Data ?? throw new WelfareToolboxException(
            "toolbox.data_missing", "Flood-support toolbox returned no reservation record.");
        FloodSupportApprovalGuard.ValidateReceipt(dispatch, approvedRequest);

        return new(
            dispatch.ReservationReference,
            _options.Name,
            _options.DispatchToolName,
            dispatch.RecordedAt);
    }

    private static string GetScope(string audience) =>
        audience.EndsWith("/.default", StringComparison.Ordinal)
            ? audience
            : $"{audience.TrimEnd('/')}/.default";


    private sealed record McpToolResponse<T>(
        string SchemaVersion,
        JsonElement CorrelationId,
        bool IsSuccess,
        T? Data,
        JsonElement Evidence,
        McpToolError? Error,
        string DecisionBoundary)
        where T : class;

    private sealed record McpToolError(
        string Code,
        string Title,
        string SafeDetail,
        bool IsRetryable,
        JsonElement CorrelationId);

}
