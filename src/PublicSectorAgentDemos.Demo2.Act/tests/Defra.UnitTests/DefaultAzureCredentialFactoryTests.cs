using Defra.AgentCore;

namespace Defra.UnitTests;

public sealed class DefaultAzureCredentialFactoryTests
{
    [Fact]
    public void CreateOptions_PinsTenantAndManagedIdentity()
    {
        string tenantId = Guid.NewGuid().ToString("D");
        string managedIdentityClientId = Guid.NewGuid().ToString("D");

        var options = DefaultAzureCredentialFactory.CreateOptions(
            tenantId,
            managedIdentityClientId);

        Assert.Equal(tenantId, options.TenantId);
        Assert.Equal(managedIdentityClientId, options.ManagedIdentityClientId);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void CreateOptions_RejectsInvalidTenant(string tenantId)
    {
        Assert.Throws<ArgumentException>(
            () => DefaultAzureCredentialFactory.CreateOptions(tenantId));
    }

    [Fact]
    public void CreateOptions_AllowsCredentialChainWithoutTenantForDeterministicLocalMode()
    {
        var options = DefaultAzureCredentialFactory.CreateOptions(null);

        Assert.Null(options.TenantId);
        Assert.Null(options.ManagedIdentityClientId);
    }
}
