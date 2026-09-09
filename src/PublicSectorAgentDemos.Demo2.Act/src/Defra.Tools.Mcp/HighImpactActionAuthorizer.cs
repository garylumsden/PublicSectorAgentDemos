using Defra.Tools.Mcp.Providers;

namespace Defra.Tools.Mcp;

public interface IHighImpactActionAuthorizer
{
    void EnsureAuthorized(string actionDomain);
}

public static class HighImpactActionDomains
{
    public const string Welfare = "flood-support";
    public const string Outbreak = "outbreak";
}

public sealed class EasyAuthHighImpactActionAuthorizer(
    IHttpContextAccessor httpContextAccessor,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<EasyAuthHighImpactActionAuthorizer> logger) : IHighImpactActionAuthorizer
{
    private const string PrincipalIdHeader = "X-MS-CLIENT-PRINCIPAL-ID";
    private const string WelfareCallerConfiguration =
        "MCP_FLOOD_SUPPORT_ACTION_CALLER_PRINCIPAL_ID";
    private const string OutbreakCallerConfiguration =
        "MCP_OUTBREAK_ACTION_CALLER_PRINCIPAL_ID";

    public void EnsureAuthorized(string actionDomain)
    {
        string configurationKey = actionDomain switch
        {
            HighImpactActionDomains.Welfare => WelfareCallerConfiguration,
            HighImpactActionDomains.Outbreak => OutbreakCallerConfiguration,
            _ => throw new ArgumentOutOfRangeException(
                nameof(actionDomain),
                actionDomain,
                "Unsupported high-impact action domain.")
        };
        string? configuredPrincipalIds = configuration[configurationKey];
        if (string.IsNullOrWhiteSpace(configuredPrincipalIds))
        {
            if (environment.IsDevelopment())
            {
                return;
            }

            throw new SyntheticProviderException(
                "authorization.action_caller_not_configured",
                "High-impact action caller authorization is not configured.",
                isProtocolError: true);
        }

        // App Service Easy Auth injects this header and strips caller-supplied values.
        string? actualPrincipalId = httpContextAccessor.HttpContext?
            .Request.Headers[PrincipalIdHeader]
            .SingleOrDefault();
        Guid[] allowedPrincipals = configuredPrincipalIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Guid.TryParse(value, out Guid parsed) ? parsed : Guid.Empty)
            .ToArray();
        if (allowedPrincipals.Length == 0 ||
            allowedPrincipals.Contains(Guid.Empty))
        {
            throw new SyntheticProviderException(
                "authorization.action_caller_not_configured",
                "High-impact action caller authorization is invalid.",
                isProtocolError: true);
        }

        if (!Guid.TryParse(actualPrincipalId, out Guid actual) ||
            !allowedPrincipals.Contains(actual))
        {
            logger.LogWarning(
                "High-impact action authorization denied for domain {ActionDomain}; authenticated caller did not match the configured allowlist.",
                actionDomain);
            throw new SyntheticProviderException(
                "authorization.action_caller_denied",
                "The authenticated caller is not authorized for this high-impact action.",
                isProtocolError: true);
        }
    }
}
