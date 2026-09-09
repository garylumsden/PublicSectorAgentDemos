using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Defra.IntegrationTests;

public sealed class Demo2IntegrationTests
{
    [Fact]
    public async Task HealthPagesAndBlazorNegotiate_WorkWithoutAppAuth()
    {
        await using Demo2TestHost host = await Demo2TestHost.StartAsync();
        using HttpClient client = new() { BaseAddress = host.BaseAddress };

        using HttpResponseMessage healthResponse = await client.GetAsync("health");
        Assert.Equal(HttpStatusCode.OK, healthResponse.StatusCode);
        JsonElement health = await healthResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Healthy", health.GetProperty("status").GetString());
        Assert.Equal(
            "approval-controlled",
            health.GetProperty("actionMode").GetString());
        Assert.Equal("nosniff", healthResponse.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(healthResponse.Headers.CacheControl?.NoStore);

        string home = await client.GetStringAsync("");
        Assert.Contains("Cross-government flood support", home, StringComparison.Ordinal);
        Assert.Contains("Live assessment progress", home, StringComparison.Ordinal);
        Assert.Contains("Local simulation", home, StringComparison.Ordinal);
        Assert.Contains("Local deterministic assessment", home, StringComparison.Ordinal);
        Assert.Contains("Local approval/action", home, StringComparison.Ordinal);
        Assert.DoesNotContain("fictional", home, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(">Azure Foundry agent<", home, StringComparison.Ordinal);
        Assert.DoesNotContain(">MCP approval/action<", home, StringComparison.Ordinal);
        Assert.Contains("<details class=\"technical-trace\">", home, StringComparison.Ordinal);
        Assert.DoesNotContain("<details class=\"technical-trace\" open", home, StringComparison.Ordinal);
        Assert.Contains("Protect personal information", home, StringComparison.Ordinal);
        Assert.Contains("Reset reservations", home, StringComparison.Ordinal);
        Assert.Contains("Previous case and audit records are kept", home, StringComparison.Ordinal);

        string admin = await client.GetStringAsync("admin");
        Assert.Contains("Incident commander workspace", admin, StringComparison.Ordinal);

        using HttpResponseMessage negotiate = await client.PostAsync(
            "_blazor/negotiate?negotiateVersion=1",
            new StringContent(string.Empty));
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
    }

    private sealed class Demo2TestHost(WebApplication app, Uri baseAddress, string auditPath)
        : IAsyncDisposable
    {
        public Uri BaseAddress { get; } = baseAddress;

        public static async Task<Demo2TestHost> StartAsync()
        {
            string auditRelativePath = $"audit/demo2-integration-{Guid.NewGuid():N}.jsonl";
            WebApplication app = Demo2.Web.Program.BuildApplication(
                [
                    "--urls", "http://127.0.0.1:0",
                    "--environment", "Testing",
                    "--applicationName", typeof(Demo2.Web.Program).Assembly.GetName().Name!,
                    "--contentRoot", AppContext.BaseDirectory,
                    "--Demo2:Agent:Mode", "LocalContract",
                    "--Demo2:Persistence:Provider", "InMemory",
                    "--Demo2:Audit:Path", auditRelativePath
                ]);
            await app.StartAsync();
            IServerAddressesFeature addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel did not publish an address.");
            string address = Assert.Single(addresses.Addresses);
            return new(app, new Uri($"{address.TrimEnd('/')}/"),
                Path.Combine(AppContext.BaseDirectory, auditRelativePath));
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            if (File.Exists(auditPath))
            {
                File.Delete(auditPath);
            }
        }
    }
}
