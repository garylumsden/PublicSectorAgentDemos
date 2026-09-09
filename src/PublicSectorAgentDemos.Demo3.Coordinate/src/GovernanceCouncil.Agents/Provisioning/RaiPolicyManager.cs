namespace GovernanceCouncil.Agents.Provisioning;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Logging;

/// <summary>
/// Updates the account's custom RAI (content-safety) policy at runtime so a presenter can toggle the
/// content-filter strictness per deliberation (e.g. demo a Violence block at <c>Medium</c> vs a pass at
/// <c>High</c>). Azure content filters are configured on the model <b>deployment</b>, not per request,
/// so the only way to change filtering live is to update the deployment's RAI policy (control plane).
///
/// <para><b>This is a single-presenter demo feature.</b> The update is <b>account-global</b> (it affects
/// every deployment bound to the policy) and takes a few seconds to propagate — it is not safe for
/// concurrent deliberations.</para>
///
/// No-ops cleanly when the target threshold is null/empty (the default — leaves the policy untouched and
/// makes no API call) or when any of the required env vars are unset.
/// </summary>
public sealed class RaiPolicyManager
{
    private const string ApiVersion = "2025-09-01";
    private const string ManagementScope = "https://management.azure.com/.default";

    private static readonly string[] Allowed = ["Low", "Medium", "High"];

    private readonly TokenCredential _credential;
    private readonly HttpClient _http;
    private readonly ILogger<RaiPolicyManager> _logger;

    public RaiPolicyManager(TokenCredential credential, HttpClient http, ILogger<RaiPolicyManager>? logger = null)
    {
        _credential = credential;
        _http = http;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<RaiPolicyManager>.Instance;
    }

    /// <summary>
    /// Ensures the account RAI policy blocks the Violence category at <paramref name="targetThreshold"/>
    /// (<c>Low</c> | <c>Medium</c> | <c>High</c>) for both Prompt and Completion, preserving every other
    /// category. No-op when the target is null/empty, invalid, already current, or when env config is
    /// missing. Never throws — a failure is logged and the deliberation proceeds on the live policy.
    /// </summary>
    public async Task EnsureViolenceThresholdAsync(string? targetThreshold, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(targetThreshold)) return;
        var target = Normalise(targetThreshold);
        if (target is null)
        {
            _logger.LogWarning("Content-safety toggle ignored: '{Value}' is not a recognised level.", targetThreshold);
            return;
        }

        var url = BuildPolicyUrl();
        if (url is null)
        {
            _logger.LogInformation("Content-safety toggle skipped: set AZURE_SUBSCRIPTION_ID, AZURE_RESOURCE_GROUP, AI_SERVICES_RESOURCE_NAME and COUNCIL_RAI_POLICY_NAME to enable it.");
            return;
        }

        try
        {
            var token = (await _credential.GetTokenAsync(new TokenRequestContext([ManagementScope]), ct)).Token;

            var current = await GetPolicyAsync(url, token, ct);
            if (current is null) return;

            if (string.Equals(CurrentViolence(current), target, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Content-safety toggle: already at '{Target}' — no change.", target);
                return;
            }

            SetViolence(current, target);
            await PutPolicyAsync(url, token, current, ct);
            _logger.LogInformation("Content-safety toggle: threshold set to '{Target}'. Waiting for propagation…", target);

            // Poll until the control plane reflects the change, then a short settle for the data plane.
            for (var i = 0; i < 6; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                var check = await GetPolicyAsync(url, token, ct);
                if (check is not null && string.Equals(CurrentViolence(check), target, StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    return;
                }
            }
            _logger.LogWarning("Content-safety toggle: '{Target}' not confirmed within the wait window; proceeding anyway.", target);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Content-safety toggle failed; proceeding on the live policy.");
        }
    }

    /// <summary>
    /// Reads the current content-safety severity threshold (<c>Low</c> | <c>Medium</c> | <c>High</c>)
    /// from the account RAI policy via the control plane, so the UI can show the live setting. Returns
    /// null when the env config is missing or the read fails (the caller treats that as "unknown").
    /// </summary>
    public async Task<string?> GetCurrentThresholdAsync(CancellationToken ct = default)
    {
        var url = BuildPolicyUrl();
        if (url is null) return null;
        try
        {
            var token = (await _credential.GetTokenAsync(new TokenRequestContext([ManagementScope]), ct)).Token;
            var policy = await GetPolicyAsync(url, token, ct);
            return policy is null ? null : CurrentViolence(policy);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the current content-safety level.");
            return null;
        }
    }

    /// <summary>Builds the ARM URL for the account RAI policy, or null if any required env var is unset.</summary>
    private static string? BuildPolicyUrl()
    {
        var sub = Env("AZURE_SUBSCRIPTION_ID");
        var rg = Env("AZURE_RESOURCE_GROUP");
        var account = Env("AI_SERVICES_RESOURCE_NAME");
        var policy = Env("COUNCIL_RAI_POLICY_NAME");
        if (sub is null || rg is null || account is null || policy is null) return null;
        return $"https://management.azure.com/subscriptions/{sub}/resourceGroups/{rg}/providers/Microsoft.CognitiveServices/accounts/{account}/raiPolicies/{policy}?api-version={ApiVersion}";
    }

    private async Task<JsonObject?> GetPolicyAsync(string url, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            _logger.LogWarning("Content-safety: GET policy failed ({Status}).", res.StatusCode);
            return null;
        }
        var body = await res.Content.ReadAsStringAsync(ct);
        return JsonNode.Parse(body) as JsonObject;
    }

    private async Task PutPolicyAsync(string url, string token, JsonObject policy, CancellationToken ct)
    {
        // PUT the full resource back (ARM tolerates id/name/type in the body) with the mutated filters.
        using var req = new HttpRequestMessage(HttpMethod.Put, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StringContent(policy.ToJsonString(), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("RAI toggle: PUT policy failed ({Status}): {Error}", res.StatusCode, err);
        }
    }

    /// <summary>Reads the current Violence severity threshold from the policy's content filters.</summary>
    private static string? CurrentViolence(JsonObject policy)
    {
        if (policy["properties"]?["contentFilters"] is not JsonArray filters) return null;
        foreach (var f in filters)
            if (f is JsonObject obj && string.Equals((string?)obj["name"], "Violence", StringComparison.OrdinalIgnoreCase))
                return (string?)obj["severityThreshold"];
        return null;
    }

    /// <summary>Sets the Violence threshold on every Violence filter (Prompt + Completion), preserving the rest.</summary>
    private static void SetViolence(JsonObject policy, string threshold)
    {
        if (policy["properties"]?["contentFilters"] is not JsonArray filters) return;
        foreach (var f in filters)
            if (f is JsonObject obj && string.Equals((string?)obj["name"], "Violence", StringComparison.OrdinalIgnoreCase))
                obj["severityThreshold"] = threshold;
    }

    private static string? Normalise(string value)
    {
        var v = value.Trim();
        return Array.Find(Allowed, a => string.Equals(a, v, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name)?.Trim();
        return string.IsNullOrEmpty(v) ? null : v;
    }
}
