using Demo2.Web.Configuration;
using Microsoft.Extensions.Hosting;

namespace Defra.UnitTests;

public sealed class Demo2AuditPathResolverTests
{
    [Fact]
    public void Production_UsesWritableAppServiceHome()
    {
        string home = Path.Combine(Path.GetTempPath(), "demo2-home");

        string path = Demo2AuditPathResolver.Resolve(
            "audit/demo2-audit.jsonl",
            Environments.Production,
            home,
            Path.Combine(Path.GetTempPath(), "read-only-package"));

        Assert.Equal(
            Path.Combine(home, "LogFiles", "audit", "demo2-audit.jsonl"),
            path);
    }

    [Fact]
    public void Development_RemainsUnderApplicationDirectory()
    {
        string application = Path.Combine(Path.GetTempPath(), "demo2-app");

        string path = Demo2AuditPathResolver.Resolve(
            "audit/demo2-audit.jsonl",
            Environments.Development,
            homeDirectory: null,
            appBaseDirectory: application);

        Assert.Equal(
            Path.Combine(application, "audit", "demo2-audit.jsonl"),
            path);
    }

    [Fact]
    public void Production_FailsClosedWithoutHome()
    {
        Assert.Throws<InvalidOperationException>(
            () => Demo2AuditPathResolver.Resolve(
                "audit/demo2-audit.jsonl",
                Environments.Production,
                homeDirectory: null,
                appBaseDirectory: AppContext.BaseDirectory));
    }
}
