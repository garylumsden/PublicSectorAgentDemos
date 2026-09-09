using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PublicSectorAgentDemos.Identity;

public static class DemoIdentityExtensions
{
    public static IServiceCollection AddDemoAzureIdentity(
        this IServiceCollection services,
        string? tenantId = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<TokenCredential>(_ =>
        {
            var options = new DefaultAzureCredentialOptions();

            if (!string.IsNullOrWhiteSpace(tenantId))
            {
                options.TenantId = NormalizeGuid(tenantId, nameof(tenantId));
            }

            return new DefaultAzureCredential(options);
        });

        return services;
    }

    private static string NormalizeGuid(string value, string parameterName) =>
        Guid.TryParse(value.Trim(), out Guid parsed) && parsed != Guid.Empty
            ? parsed.ToString("D")
            : throw new ArgumentException(
                "Value must be a non-empty GUID.",
                parameterName);
}
