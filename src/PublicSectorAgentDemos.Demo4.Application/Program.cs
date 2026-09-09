using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using PublicSectorAgentDemos.Contracts.Demo4;
using PublicSectorAgentDemos.Demo4.Application;
using PublicSectorAgentDemos.Identity;
using PublicSectorAgentDemos.Observability;

var builder = WebApplication.CreateBuilder(args);

// App Service EasyAuth v2 is the browser gate for every route except /health.
// Microsoft Identity Web validates bearer tokens for programmatic API callers.
// The Razor pages stay anonymous in process so the EasyAuth cookie session works.
builder.Services
    .AddAuthentication()
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
builder.Services.PostConfigure<JwtBearerOptions>(
    JwtBearerDefaults.AuthenticationScheme,
    options =>
    {
        options.RequireHttpsMetadata = true;
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidateIssuerSigningKey = true;
        options.TokenValidationParameters.ValidateAudience = true;
        options.TokenValidationParameters.ValidateLifetime = true;
        options.TokenValidationParameters.RequireSignedTokens = true;
        options.TokenValidationParameters.RequireExpirationTime = true;
        options.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(2);
    });
builder.Services.AddAuthorization();
builder.Services
    .AddDemoAzureIdentity(builder.Configuration["AZURE_TENANT_ID"])
    .AddDemoObservability("PublicSectorAgentDemos.Demo4.Application");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(provider =>
{
    string endpoint = builder.Configuration["FOUNDRY_PROJECT_ENDPOINT"]
        ?? throw new InvalidOperationException("FOUNDRY_PROJECT_ENDPOINT is required.");
    return new MemoryOptions { ProjectEndpoint = endpoint };
});
builder.Services.AddSingleton(provider =>
{
    string endpoint = builder.Configuration["DEMO4_HOSTED_AGENT_ENDPOINT"]
        ?? throw new InvalidOperationException("DEMO4_HOSTED_AGENT_ENDPOINT is required.");
    return new HostedAgentOptions
    {
        ResponsesEndpoint = endpoint,
        ModelDeploymentName =
            builder.Configuration["AZURE_AI_MODEL_DEPLOYMENT_NAME"] ?? "gpt-5.4-mini"
    };
});
builder.Services.AddSingleton<IFoundryAccessTokenProvider>(provider =>
    new AzureFoundryAccessTokenProvider(provider.GetRequiredService<TokenCredential>()));
builder.Services.AddHttpClient<IFoundryMemoryItemClient, FoundryMemoryItemClient>();
builder.Services.AddHttpClient<IHostedCasePatternAgentClient, FoundryHostedCasePatternAgentClient>();
builder.Services.AddSingleton<InvestigationMemoryCoordinator>();
builder.Services.AddSingleton<AssessmentProcessingService>();
builder.Services.AddRazorPages();

WebApplication app = builder.Build();
app.UseExceptionHandler("/Error");
app.UseHsts();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'; " +
        "object-src 'none'; img-src 'self'; style-src 'self'; script-src 'self'";
    // EasyAuth needs the same-origin POST headers for its CSRF checks.
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers["Permissions-Policy"] =
        "camera=(), microphone=(), geolocation=()";
    await next().ConfigureAwait(false);
});
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapPost(
    "/api/case-pattern-assessments",
    async (
        CasePatternAssessmentRequest request,
        AssessmentProcessingService processor,
        CancellationToken cancellationToken) =>
    {
        try
        {
            AssessmentProcessingReceipt receipt = await processor.ProcessAsync(
                request,
                cancellationToken).ConfigureAwait(false);
            return Results.Ok(receipt);
        }
        catch (Exception exception) when (
            exception is HostedAssessmentProcessingException or
            UnsafeMemoryReferenceException or
            MemoryWriteCompensatedException or
            MemoryWriteIndeterminateException or
            MemoryWriteIntegrityException)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Assessment processing failed.");
        }
    }).RequireAuthorization();
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "Demo4.Application",
    preview = true
})).AllowAnonymous();
app.MapRazorPages();
app.Run();

public partial class Program;
