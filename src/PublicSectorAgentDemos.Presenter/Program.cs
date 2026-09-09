using Azure.Core;
using PublicSectorAgentDemos.Identity;
using PublicSectorAgentDemos.Presenter;
using PublicSectorAgentDemos.Observability;
using System.Text.Json;
using System.Text.Json.Serialization;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5088");
builder.Services.AddDemoAzureIdentity();
builder.Services.AddDemoObservability("PublicSectorAgentDemos.Presenter");

string? catalogOverride = Environment.GetEnvironmentVariable("PRESENTER_SESSION_CATALOG_PATH");
string catalogPath = string.IsNullOrWhiteSpace(catalogOverride)
    ? Path.Combine(
        AppContext.BaseDirectory,
        "config",
        "presenter",
        "sessions.v1.json")
    : Path.GetFullPath(catalogOverride);
PresenterSessionCatalog catalog = PresenterSessionCatalog.Load(catalogPath);

builder.Services.AddSingleton(catalog);
builder.Services.ConfigureHttpJsonOptions(
    options => options.SerializerOptions.Converters.Add(
        new JsonStringEnumConverter<WarmUpOutcome>(JsonNamingPolicy.KebabCaseLower)));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IFoundryBearerTokenProvider>(provider =>
    new FoundryBearerTokenProvider(() => provider.GetRequiredService<TokenCredential>()));
builder.Services
    .AddHttpClient<PresenterWarmUpService>(client =>
        client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<PresenterWarmUpCoordinator>();

WebApplication app = builder.Build();
app.Use(
    async (context, next) =>
    {
        context.Response.Headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self'; style-src 'self'; " +
            "connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.XFrameOptions = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        await next();
    });
app.UseStaticFiles();

app.MapGet(
    "/",
    (PresenterSessionCatalog sessions) =>
        Results.Content(PresenterPage.Render(sessions), "text/html; charset=utf-8"));
app.MapPost(
    "/api/warm-up",
    (PresenterWarmUpCoordinator coordinator) =>
    {
        WarmUpRunSnapshot snapshot = coordinator.Start();
        return Results.Accepted("/api/warm-up", snapshot);
    });
app.MapGet(
    "/api/warm-up",
    (PresenterWarmUpCoordinator coordinator) => Results.Ok(coordinator.GetSnapshot()));

app.Run();

public partial class Program;
