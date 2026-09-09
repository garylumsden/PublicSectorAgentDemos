using Defra.Tools.Mcp;
using Defra.Tools.Mcp.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Defra.UnitTests;

public sealed class HighImpactActionAuthorizerTests
{
    [Fact]
    public void EnsureAuthorized_AcceptsConfiguredEasyAuthPrincipal()
    {
        Guid principalId = Guid.NewGuid();
        DefaultHttpContext context = new();
        context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"] = principalId.ToString();
        EasyAuthHighImpactActionAuthorizer authorizer = Create(
            context,
            new Dictionary<string, string?>
            {
                ["MCP_FLOOD_SUPPORT_ACTION_CALLER_PRINCIPAL_ID"] = principalId.ToString()
            });

        authorizer.EnsureAuthorized(HighImpactActionDomains.Welfare);
    }

    [Fact]
    public void EnsureAuthorized_AcceptsAnyConfiguredPrincipal()
    {
        Guid actual = Guid.NewGuid();
        DefaultHttpContext context = new();
        context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"] = actual.ToString();
        EasyAuthHighImpactActionAuthorizer authorizer = Create(
            context,
            new Dictionary<string, string?>
            {
                ["MCP_OUTBREAK_ACTION_CALLER_PRINCIPAL_ID"] =
                    $"{Guid.NewGuid()},{actual}"
            });

        authorizer.EnsureAuthorized(HighImpactActionDomains.Outbreak);
    }

    [Fact]
    public void EnsureAuthorized_RejectsDifferentEasyAuthPrincipal()
    {
        DefaultHttpContext context = new();
        context.Request.Headers["X-MS-CLIENT-PRINCIPAL-ID"] = Guid.NewGuid().ToString();
        EasyAuthHighImpactActionAuthorizer authorizer = Create(
            context,
            new Dictionary<string, string?>
            {
                ["MCP_OUTBREAK_ACTION_CALLER_PRINCIPAL_ID"] = Guid.NewGuid().ToString()
            });

        SyntheticProviderException exception = Assert.Throws<SyntheticProviderException>(
            () => authorizer.EnsureAuthorized(HighImpactActionDomains.Outbreak));

        Assert.Equal("authorization.action_caller_denied", exception.Code);
        Assert.True(exception.IsProtocolError);
    }

    [Fact]
    public void EnsureAuthorized_FailsClosedWhenProductionConfigurationIsMissing()
    {
        EasyAuthHighImpactActionAuthorizer authorizer = Create(
            new DefaultHttpContext(),
            new Dictionary<string, string?>());

        SyntheticProviderException exception = Assert.Throws<SyntheticProviderException>(
            () => authorizer.EnsureAuthorized(HighImpactActionDomains.Welfare));

        Assert.Equal("authorization.action_caller_not_configured", exception.Code);
    }

    private static EasyAuthHighImpactActionAuthorizer Create(
        HttpContext context,
        IReadOnlyDictionary<string, string?> values)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        return new(
            new HttpContextAccessor { HttpContext = context },
            configuration,
            new TestHostEnvironment(),
            NullLogger<EasyAuthHighImpactActionAuthorizer>.Instance);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Defra.UnitTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
