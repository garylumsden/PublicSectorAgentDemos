using System.Diagnostics;
using System.Security.Cryptography;
using Defra.Audit;
using Defra.Contracts.FloodSupport;
using Defra.Contracts.V1;
using Defra.Policy;
using Defra.Tools.Mcp.Contracts;
using Defra.Tools.Mcp.Providers;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Defra.Tools.Mcp;

public static class McpApplication
{
    private const long MaximumRequestBodyBytes = 256 * 1024;

    public static WebApplication Build(
        string[] args,
        Action<WebApplicationBuilder>? configureBuilder = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        configureBuilder?.Invoke(builder);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = MaximumRequestBodyBytes;
            options.ConfigureEndpointDefaults(endpoint => endpoint.Protocols = HttpProtocols.Http1AndHttp2);
        });

        builder.Services.AddAuthorization();
        builder.Services.AddHttpContextAccessor();
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.TryAddSingleton(McpServicePolicy.CreateDefault());
        builder.Services.TryAddSingleton<PolicyScreener>();
        builder.Services.TryAddSingleton<
            IHighImpactActionAuthorizer,
            EasyAuthHighImpactActionAuthorizer>();
        builder.Services.TryAddSingleton<IWelfareDataProvider>(_ =>
            new InMemoryWelfareDataProvider(new FloodSupportReservationSimulator(Guid.NewGuid().ToString("N"))));
        builder.Services.TryAddSingleton<IAppendOnlyAuditWriter>(
            _ => new JsonLinesAuditWriter(Console.OpenStandardError(), leaveOpen: true));
        builder.Services.TryAddSingleton<McpToolExecutor>();
        builder.Services.AddMcpObservability(builder.Configuration);

        builder.Services
            .AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "defra-tools-mcp",
                    Version = McpToolCatalog.Version
                };
                options.ServerInstructions =
                    "Connect to the flood-support endpoint. Preserve source timestamps, conflicts, unmet demand and required approval state.";
            })
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
                options.ConfigureSessionOptions = ConfigureSessionAsync;
            })
            .AddAuthorizationFilters()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                if (context.Params is { } call && McpToolCatalog.AllToolNames.Contains(call.Name))
                {
                    bool valid = call.Arguments is { Count: 1 } arguments && arguments.Keys.Single() == "request";
                    if (valid && call.Name == McpToolNames.DispatchVet)
                    {
                        valid = FloodSupportCatalogue.TryParseApprovalArguments(
                            call.Arguments!.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal), out _);
                    }
                    if (!valid)
                    {
                        context.Services!.GetRequiredService<ILoggerFactory>().CreateLogger("Defra.Tools.Mcp.WireValidation")
                            .LogWarning("MCP tool {ToolName} rejected an invalid argument shape.", call.Name);
                        throw new McpProtocolException("Supply only the exact typed request argument. Unknown, missing or malformed fields are rejected.", McpErrorCode.InvalidParams);
                    }
                }
                return await next(context, cancellationToken);
            }))
            .WithToolsFromAssembly(
                typeof(McpApplication).Assembly,
                McpJson.SerializerOptions);

        WebApplication app = builder.Build();
        _ = app.Services.GetRequiredService<PolicyScreener>();
        ILogger exceptionLogger = app.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("Defra.Tools.Mcp.Unhandled");

        app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                Exception exception = context.Features.Get<IExceptionHandlerFeature>()?.Error
                    ?? new InvalidOperationException("MCP request failed without exception detail.");
                string traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
                exceptionLogger.LogError(
                    exception,
                    "Unhandled MCP request failure. Trace {TraceId}.",
                    traceId);

                ProblemDetails problem = new()
                {
                    Title = "MCP request failed",
                    Detail = "The tools service could not complete the request. Retry once, then give the trace reference to the service operator.",
                    Status = StatusCodes.Status500InternalServerError
                };
                problem.Extensions["errorCode"] = "mcp.unhandled";
                problem.Extensions["traceId"] = traceId;

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(
                    problem,
                    cancellationToken: context.RequestAborted);
            });
        });

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.Use(
            async (context, next) =>
            {
                context.Response.Headers["Content-Security-Policy"] =
                    "default-src 'none'; frame-ancestors 'none'";
                context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                context.Response.Headers["X-Frame-Options"] = "DENY";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                context.Response.Headers["Permissions-Policy"] =
                    "camera=(), microphone=(), geolocation=()";
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.Pragma = "no-cache";
                await next(context).ConfigureAwait(false);
            });

        app.MapGet(
            "/health",
            () => Results.Ok(
                new HealthResponse(
                    "Healthy",
                    "Defra.Tools.Mcp",
                    McpToolCatalog.Version,
                    McpToolCatalog.Groups.Select(group => group.Name).ToArray())));

        foreach (McpEndpointGroup group in McpToolCatalog.Groups)
        {
            app.MapMcp(group.Path);
        }

        app.MapGet("/admin/reservations", (IWelfareDataProvider provider) =>
            Results.Ok(provider.GetInventoryState()));
        app.MapPost("/admin/reservations/reset", async (
            IHighImpactActionAuthorizer authorizer,
            IWelfareDataProvider provider,
            IAppendOnlyAuditWriter audit,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            try
            {
                authorizer.EnsureAuthorized(HighImpactActionDomains.Welfare);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            CorrelationId correlation = CorrelationId.Create();
            string inputHash = Convert.ToHexStringLower(SHA256.HashData("resetReservations"u8));
            await audit.AppendAsync(new SanitizedAuditEvent(
                Guid.NewGuid().ToString("N"), time.GetUtcNow(), correlation,
                AuditEventKind.ToolRequested, "mcp", "reservation-administration", "reset",
                "resetReservations", "operator-requested", "1.0.0", "requested", 0, inputHash),
                cancellationToken);
            ReservationInventoryState state = provider.ResetReservations();
            await audit.AppendAsync(new SanitizedAuditEvent(
                Guid.NewGuid().ToString("N"), time.GetUtcNow(), correlation,
                AuditEventKind.ToolCompleted, "mcp", "reservation-administration", "reset",
                "resetReservations", "operator-requested", "1.0.0", "completed", 0, inputHash),
                cancellationToken);
            return Results.Ok(state);
        });

        return app;
    }

    private static Task ConfigureSessionAsync(
        HttpContext context,
        McpServerOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        McpServerPrimitiveCollection<McpServerTool> filteredTools =
            new(StringComparer.Ordinal);

        if (McpToolCatalog.TryGetGroup(context.Request.Path, out McpEndpointGroup? group))
        {
            foreach (McpServerTool tool in options.ToolCollection ?? [])
            {
                if (group!.ToolNames.Contains(tool.ProtocolTool.Name))
                {
                    filteredTools.Add(tool);
                }
            }

            options.ServerInfo = new Implementation
            {
                Name = group!.ServerName,
                Version = McpToolCatalog.Version
            };
            options.ServerInstructions = group.Instructions;
        }

        options.ToolCollection = filteredTools;
        return Task.CompletedTask;
    }
}
