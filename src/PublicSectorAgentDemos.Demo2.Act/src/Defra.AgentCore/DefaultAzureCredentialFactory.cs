using Azure.Identity;

namespace Defra.AgentCore;

public static class DefaultAzureCredentialFactory
{
    public static DefaultAzureCredential Create(
        string? tenantId,
        string? managedIdentityClientId = null) =>
        new(CreateOptions(tenantId, managedIdentityClientId));

    public static DefaultAzureCredentialOptions CreateOptions(
        string? tenantId,
        string? managedIdentityClientId = null)
    {
        DefaultAzureCredentialOptions options = new();

        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            options.TenantId = NormalizeGuid(tenantId, nameof(tenantId));
        }

        if (!string.IsNullOrWhiteSpace(managedIdentityClientId))
        {
            options.ManagedIdentityClientId = NormalizeGuid(
                managedIdentityClientId,
                nameof(managedIdentityClientId));
        }

        return options;
    }

    private static string NormalizeGuid(string value, string parameterName) =>
        Guid.TryParse(value.Trim(), out Guid parsed) && parsed != Guid.Empty
            ? parsed.ToString("D")
            : throw new ArgumentException(
                "Value must be a non-empty GUID.",
                parameterName);
}
