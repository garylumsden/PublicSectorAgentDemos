using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using PublicSectorAgentDemos.Demo1.FoundationAndGround;
using PublicSectorAgentDemos.Demo1.Web;
using PublicSectorAgentDemos.Identity;
using PublicSectorAgentDemos.Observability;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
if (builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Any())
{
    throw new InvalidOperationException("Custom Kestrel endpoints are not supported by this local-only application.");
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(LocalBoundary.Port);
    options.Limits.MaxRequestBodySize = DemoLimits.RequestBytes;
    options.Limits.MaxConcurrentConnections = 32;
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogLevel.None);
builder.Services.AddDemoObservability("PublicSectorAgentDemos.Demo1.Web");
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-Demo1-CSRF";
    options.Cookie.Name = "Demo1.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddSingleton(provider => FoundryProjectSettings.Parse(
    provider.GetRequiredService<IConfiguration>()["AZURE_AI_FOUNDRY_ENDPOINT"]));
builder.Services.AddDemoAzureIdentity();
builder.Services.AddSingleton(_ => new HttpClient(new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    MaxConnectionsPerServer = DemoLimits.ConcurrentComparisons * 2,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5)
}) { Timeout = Timeout.InfiniteTimeSpan });
builder.Services.AddSingleton<IAgentClient, FoundryAgentClient>();
builder.Services.AddSingleton(provider => QualityAssessmentSettings.Parse(
    provider.GetRequiredService<IConfiguration>()["DEMO1_ASSESSMENT_MODEL"]));
builder.Services.AddSingleton<IQualityAssessmentClient, FoundryQualityAssessmentClient>();
builder.Services.AddSingleton<AssessmentReferenceBuilder>();
builder.Services.AddSingleton<ComparisonSnapshotStore>();
builder.Services.AddSingleton<ComparisonRunner>();
builder.Services.AddSingleton<QualityAssessmentRunner>();

WebApplication app = builder.Build();
_ = app.Services.GetRequiredService<FoundryProjectSettings>();
using Stream fixtureStream = typeof(Program).Assembly.GetManifestResourceStream("Demo1.Fixtures.json")
    ?? throw new InvalidOperationException("The embedded Demo 1 fixture is missing.");
using JsonDocument fixtures = JsonDocument.Parse(fixtureStream);
string defaultPrompt = fixtures.RootElement.GetProperty("fixtures").EnumerateArray()
    .Single(fixture => fixture.GetProperty("scenarioId").GetString() == "MPM-005")
    .GetProperty("prompt").GetString() ?? throw new InvalidDataException("MPM-005 has no prompt.");

app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Cross-Origin-Resource-Policy"] = "same-origin";
    context.Response.Headers.CacheControl = "no-store";
    if (!LocalBoundary.Allows(context))
    {
        app.Logger.LogWarning("Rejected a request outside the local boundary.");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "Only direct localhost requests are supported." });
        return;
    }

    await next(context);
});
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    app.Logger.LogError("An unhandled local request failure occurred.");
    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await context.Response.WriteAsJsonAsync(new { error = "The local request failed." });
}));
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health", () => Results.Ok(new { status = "ok", scope = "local-only" }));
app.MapGet("/api/session", (HttpContext context, IAntiforgery antiforgery, QualityAssessmentSettings assessment) => Results.Ok(new
{
    scenarioId = "MPM-005",
    prompt = defaultPrompt,
    maxPromptCharacters = DemoLimits.PromptCharacters,
    agentTimeoutSeconds = (int)DemoLimits.AgentTimeout.TotalSeconds,
    agents = new
    {
        model = Demo1AgentCatalog.ModelDeploymentName,
        foundationReasoning = Demo1AgentCatalog.FoundationReasoningEffort,
        groundReasoning = Demo1AgentCatalog.GroundReasoningEffort,
        groundRetrievalReasoning = Demo1AgentCatalog.GroundRetrievalReasoningEffort,
        timingComparable = true,
        timingExplanation = "Both agents use gpt-5-mini with low reasoning. Ground additionally uses required Foundry IQ retrieval with minimal retrieval reasoning. Timing compares matched model reasoning settings, but service and retrieval variability still apply."
    },
    requestToken = antiforgery.GetAndStoreTokens(context).RequestToken,
    assessment = new
    {
        available = true,
        model = assessment.Model,
        rubricVersion = QualityAssessmentRubric.Version,
        timeoutSeconds = (int)DemoLimits.AssessmentTimeout.TotalSeconds,
        inputBytes = DemoLimits.AssessmentInputBytes,
        snapshotLifetimeMinutes = (int)DemoLimits.SnapshotLifetime.TotalMinutes
    }
}));
app.MapPost("/api/compare", async (HttpContext context, IAntiforgery antiforgery, ComparisonRunner runner,
    ComparisonSnapshotStore snapshots, AssessmentReferenceBuilder references) =>
{
    async Task Reject(int status, string error)
    {
        app.Logger.LogWarning("Rejected a comparison request: HTTP {Status}.", status);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error }, context.RequestAborted);
    }

    if (!LocalBoundary.IsSameOrigin(context.Request))
    {
        await Reject(403, "A same-origin request is required.");
        return;
    }

    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        await Reject(400, "The request token is invalid. Reload the page.");
        return;
    }

    if (!context.Request.HasJsonContentType())
    {
        await Reject(415, "A JSON request is required.");
        return;
    }

    CompareRequest? request;
    try
    {
        if (context.Request.ContentLength > DemoLimits.RequestBytes)
        {
            await Reject(413, "The request exceeds the size limit.");
            return;
        }

        byte[] body = await DemoLimits.ReadBoundedAsync(context.Request.Body, DemoLimits.RequestBytes, context.RequestAborted);
        request = JsonSerializer.Deserialize<CompareRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 4,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        });
    }
    catch (JsonException)
    {
        await Reject(400, "The JSON request is invalid.");
        return;
    }
    catch (InvalidDataException)
    {
        await Reject(413, "The request exceeds the size limit.");
        return;
    }
    catch (BadHttpRequestException)
    {
        await Reject(413, "The request exceeds the size limit.");
        return;
    }

    if (string.IsNullOrWhiteSpace(request?.Prompt) || request.Prompt.Length > DemoLimits.PromptCharacters)
    {
        await Reject(400, "The prompt must contain 1 to 8000 characters and cannot contain only whitespace.");
        return;
    }

    if (!runner.TryEnter())
    {
        context.Response.Headers.RetryAfter = "5";
        await Reject(429, "Two comparisons are already running. Wait before retrying.");
        return;
    }

    string comparisonId = ComparisonSnapshotStore.CreateId();
    string owner = ComparisonSnapshotStore.GetOwner(context.Request);
    using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
    Task<AgentUpdate>[] tasks = runner.Start(request.Prompt, cancellation.Token);
    try
    {
        context.Response.ContentType = "application/x-ndjson; charset=utf-8";
        List<Task<AgentUpdate>> pending = [.. tasks];
        Dictionary<string, AgentUpdate> updates = new(StringComparer.Ordinal);
        await context.Response.StartAsync(cancellation.Token);
        while (pending.Count > 0)
        {
            Task<AgentUpdate> completed = await Task.WhenAny(pending);
            pending.Remove(completed);
            AgentUpdate update = (await completed) with { ComparisonId = comparisonId };
            updates[update.Agent] = update;
            if (pending.Count == 0 &&
                updates.TryGetValue("foundation", out AgentUpdate? foundationUpdate) &&
                updates.TryGetValue("ground", out AgentUpdate? groundUpdate) &&
                IsAssessmentEligible(foundationUpdate, groundUpdate))
            {
                AssessmentReference reference = references.Build(request.Prompt, foundationUpdate.Answer!, groundUpdate.Answer!);
                snapshots.Add(comparisonId, owner, request.Prompt, foundationUpdate.Answer!, groundUpdate.Answer!, reference);
            }
            string json = JsonSerializer.Serialize(update, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await context.Response.WriteAsync(json + "\n", cancellation.Token);
            await context.Response.Body.FlushAsync(cancellation.Token);
        }
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
        app.Logger.LogInformation("The comparison request was cancelled.");
    }
    catch (IOException)
    {
        app.Logger.LogWarning("The comparison connection closed before completion.");
    }
    finally
    {
        await cancellation.CancelAsync();
        try
        {
            await Task.WhenAll(tasks);
        }
        finally
        {
            runner.Exit();
        }
    }
});
app.MapPost("/api/assess-quality", async (HttpContext context, IAntiforgery antiforgery,
    ComparisonSnapshotStore snapshots, QualityAssessmentRunner runner) =>
{
    async Task<IResult> Reject(int status, string error, string state)
    {
        app.Logger.LogWarning("Rejected a quality assessment request: HTTP {Status}, {State}.", status, state);
        return Results.Json(new { state, error }, statusCode: status);
    }

    if (!LocalBoundary.IsSameOrigin(context.Request))
    {
        return await Reject(403, "A same-origin request is required.", "invalid");
    }
    try
    {
        await antiforgery.ValidateRequestAsync(context);
    }
    catch (AntiforgeryValidationException)
    {
        return await Reject(400, "The request token is invalid. Reload the page.", "invalid");
    }
    if (!context.Request.HasJsonContentType())
    {
        return await Reject(415, "A JSON request is required.", "invalid");
    }

    AssessQualityRequest? request;
    try
    {
        if (context.Request.ContentLength > DemoLimits.RequestBytes)
        {
            return await Reject(413, "The request exceeds the size limit.", "invalid");
        }
        byte[] body = await DemoLimits.ReadBoundedAsync(context.Request.Body, DemoLimits.RequestBytes, context.RequestAborted);
        request = JsonSerializer.Deserialize<AssessQualityRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            MaxDepth = 3,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        });
    }
    catch (JsonException)
    {
        return await Reject(400, "The JSON request is invalid.", "invalid");
    }
    catch (InvalidDataException)
    {
        return await Reject(413, "The request exceeds the size limit.", "invalid");
    }
    catch (BadHttpRequestException)
    {
        return await Reject(413, "The request exceeds the size limit.", "invalid");
    }

    if (request is null || request.ComparisonId?.Length != 32 ||
        request.ComparisonId.Any(character => !Uri.IsHexDigit(character)))
    {
        return await Reject(400, "The comparison identifier is invalid.", "invalid");
    }

    string owner;
    try
    {
        owner = ComparisonSnapshotStore.GetOwner(context.Request);
    }
    catch (InvalidOperationException)
    {
        return await Reject(400, "The browser session is unavailable. Reload the page.", "invalid");
    }
    if (!snapshots.TryGet(request.ComparisonId, owner, out ComparisonSnapshot? snapshot))
    {
        return await Reject(404, "This comparison is unavailable or expired. Run the comparison again.", "unavailable");
    }
    if (snapshot!.Assessment is not null)
    {
        return Results.Ok(new { state = "completed", assessment = snapshot.Assessment with { Cached = true } });
    }
    if (!runner.TryEnter())
    {
        context.Response.Headers.RetryAfter = "5";
        return await Reject(429, "A quality assessment is already running. Wait before retrying.", "busy");
    }

    try
    {
        AssessmentRunResult result = await runner.RunAsync(snapshot, context.RequestAborted);
        if (result.Assessment is not null)
        {
            snapshot.Assessment = result.Assessment;
            return Results.Ok(new { state = result.State, assessment = result.Assessment });
        }
        int status = result.State switch
        {
            "input-too-large" => 413,
            "timed-out" => 504,
            "cancelled" => 408,
            _ => 502
        };
        return Results.Json(new { state = result.State, error = result.Error }, statusCode: status);
    }
    finally
    {
        runner.Exit();
    }
});
app.Run();

static bool IsAssessmentEligible(AgentUpdate foundation, AgentUpdate ground) =>
    foundation.State == "completed" && ground.State == "completed" &&
    foundation.Answer is { ResponseStatus: "completed" } foundationAnswer &&
    ground.Answer is { ResponseStatus: "completed" } groundAnswer &&
    !string.IsNullOrWhiteSpace(foundationAnswer.OriginalText) &&
    !string.IsNullOrWhiteSpace(groundAnswer.OriginalText);

public sealed record CompareRequest(string? Prompt);
public sealed record AssessQualityRequest(string? ComparisonId);
public partial class Program;
