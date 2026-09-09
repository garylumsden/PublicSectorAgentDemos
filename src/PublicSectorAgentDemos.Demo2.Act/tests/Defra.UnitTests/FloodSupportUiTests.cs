using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Defra.Contracts.FloodSupport;
using Defra.Contracts.V1;
using Demo2.Web.Agent;
using Demo2.Web.Components.UI;
using Demo2.Web.Domain;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Defra.UnitTests;

public sealed class FloodSupportUiTests
{
    [Fact]
    public async Task Approval_ShowsWhatWhyCostEvidenceUnmetRisksAndExpiryWithoutExpandingInput()
    {
        AgentMcpApprovalRequest request = Request();
        Assert.True(DispatchApprovalPresentation.TryCreate(request, "AREA-1001", out var context));

        string html = await RenderAsync(context);
        string visible = WebUtility.HtmlDecode(html[..html.IndexOf("<details", StringComparison.Ordinal)]);
        SupportPackageDefinition package = FloodSupportCatalogue.GetPackage("PKG-RIV-060");
        foreach (string text in new[]
        {
            "WHAT is being requested", "Reserve one accommodation and transport support package",
            package.AccommodationSite, "60 rooms", "12 accessible rooms",
            "2 coaches", "120 seats", "4 accessible transport places", "24 hours",
            "GBP 8,100.00", "AREA-1001", "INC-12345678",
            "PKG-RIV-060", "v1", "WHY this package is requested", "Agent justification:",
            context!.Justification, "Authoritative package reason:", package.Why,
            "180", "18:00", "90 rooms", "60 rooms", "120 households",
            "Unresolved risks and conditions", "Approval expires",
            context.ExpiresAt.ToUniversalTime().ToString("dd MMM yyyy HH:mm:ss 'UTC'")
        })
        {
            Assert.Contains(text, visible, StringComparison.Ordinal);
        }
        Assert.All(package.Evidence, fact => Assert.Contains(fact, visible, StringComparison.Ordinal));
        Assert.All(package.Risks, risk => Assert.Contains(risk, visible, StringComparison.Ordinal));
        Assert.Contains("Exact MCP input: reserveSupportPackage", html, StringComparison.Ordinal);
        Assert.Contains("PKG-RIV-060</code> / v1", html, StringComparison.Ordinal);
        Assert.DoesNotContain("@Package.Version", html, StringComparison.Ordinal);
        Assert.Contains("Approves this package only", html, StringComparison.Ordinal);
        Assert.Contains("spending, evacuation, or eligibility authority", html, StringComparison.Ordinal);
        Assert.DoesNotContain("fictional", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, "Local simulation", "local workflow is waiting for your decision")]
    [InlineData(true, "Azure Foundry", "Foundry paused reserveSupportPackage")]
    public async Task Approval_UsesTruthfulProviderLabels(bool azure, string title, string detail)
    {
        Assert.True(DispatchApprovalPresentation.TryCreate(Request(), "AREA-1001", out var context));
        string html = await RenderAsync(context, azure: azure);
        Assert.Contains(title, html, StringComparison.Ordinal);
        Assert.Contains(detail, html, StringComparison.Ordinal);
        if (!azure)
        {
            Assert.DoesNotContain("Azure Foundry paused", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MissingContext_DisablesApprovalButKeepsRejectAndCancelUsable()
    {
        string html = await RenderAsync(null, canApprove: false);
        Assert.Contains("Decision context unavailable", html, StringComparison.Ordinal);
        Assert.Contains("disabled", Button(html, "Reserve this package and continue"), StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", Button(html, "Reject - no reservation"), StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", Button(html, "Cancel"), StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", Button(html, "Close"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecisionInProgress_DisablesRepeatedDecisionClicks()
    {
        Assert.True(DispatchApprovalPresentation.TryCreate(Request(), "AREA-1001", out var context));
        string html = await RenderAsync(context, running: true);
        foreach (string label in new[] { "Reserve this package and continue", "Reject - no reservation", "Cancel", "Close" })
        {
            Assert.Contains("disabled", Button(html, label), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExpiredPresentation_DisablesApprovalButAllowsRejection()
    {
        Assert.True(DispatchApprovalPresentation.TryCreate(Request(), "AREA-1001", out var context));
        string html = await RenderAsync(context! with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
        Assert.Contains("disabled", Button(html, "Reserve this package and continue"), StringComparison.Ordinal);
        Assert.DoesNotContain("disabled", Button(html, "Reject - no reservation"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UntrustedJustificationAndExactArguments_AreEncoded()
    {
        const string markup = "<img src=x onerror=alert(1)><script>alert('unsafe')</script>";
        AgentMcpApprovalRequest request = Request(justification: markup);
        Assert.True(DispatchApprovalPresentation.TryCreate(request, "AREA-1001", out var context));
        string html = await RenderAsync(context);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img", html, StringComparison.Ordinal);
        Assert.Contains(markup, WebUtility.HtmlDecode(html), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("packageId", "PKG-UNKNOWN")]
    [InlineData("packageId", "pkg-riv-060")]
    [InlineData("packageId", "PKG-MEA-020")]
    [InlineData("areaReference", "AREA-1002")]
    [InlineData("areaReference", "area-1001")]
    [InlineData("packageVersion", 0)]
    [InlineData("packageVersion", 2)]
    [InlineData("packageVersion", "1")]
    [InlineData("actionRequestId", "approval-short")]
    [InlineData("incidentReference", "INC-invalid")]
    [InlineData("justification", "")]
    [InlineData("rooms", 90)]
    [InlineData("estimatedCostGbp", 1)]
    public void Presenter_RejectsMalformedWrongOrModelSuppliedPackageValues(string key, object value)
    {
        AgentMcpApprovalRequest request = Request();
        Dictionary<string, object?> arguments = new(request.Arguments, StringComparer.Ordinal) { [key] = value };
        Assert.False(DispatchApprovalPresentation.TryCreate(request with { Arguments = arguments }, "AREA-1001", out _));
    }

    [Fact]
    public void Presenter_RejectsMissingArgumentWrongToolAndUnavailableExpiry()
    {
        AgentMcpApprovalRequest request = Request();
        Dictionary<string, object?> arguments = new(request.Arguments, StringComparer.Ordinal);
        arguments.Remove("justification");
        Assert.False(DispatchApprovalPresentation.TryCreate(request with { Arguments = arguments }, "AREA-1001", out _));
        Assert.False(DispatchApprovalPresentation.TryCreate(request with { ToolName = "dispatchVet" }, "AREA-1001", out _));
        Assert.False(DispatchApprovalPresentation.TryCreate(request with { ToolName = "ReserveSupportPackage" }, "AREA-1001", out _));
        Assert.False(DispatchApprovalPresentation.TryCreate(request with { ExpiresAt = null }, "AREA-1001", out _));
        Assert.False(DispatchApprovalPresentation.TryCreate(request, "", out _));
    }

    [Fact]
    public void Presenter_RejectsStaleAndReversedApprovalTimes()
    {
        AgentMcpApprovalRequest request = Request();
        Assert.False(DispatchApprovalPresentation.TryCreate(request with
        {
            RequestedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        }, "AREA-1001", out _));
        Assert.False(DispatchApprovalPresentation.TryCreate(request with
        {
            ExpiresAt = request.RequestedAt
        }, "AREA-1001", out _));
        Assert.False(DispatchApprovalPresentation.TryCreate(request with
        {
            RequestedAt = DateTimeOffset.UtcNow.AddMinutes(1)
        }, "AREA-1001", out _));
    }

    [Fact]
    public void Presenter_RejectsChangesAfterTheOperatorReviewedThePackage()
    {
        AgentMcpApprovalRequest original = Request();
        Assert.True(DispatchApprovalPresentation.TryCreate(original, "AREA-1001", out var context));
        Assert.True(context!.MatchesCurrent(original, "AREA-1001"));
        Assert.False(context.MatchesCurrent(original, "AREA-1002"));
        Dictionary<string, object?> arguments = new(original.Arguments, StringComparer.Ordinal);
        foreach ((string key, object value) in new (string, object)[]
        {
            ("packageId", "PKG-RIV-030"),
            ("incidentReference", "INC-87654321"),
            ("actionRequestId", "approval-abcdef1234567890abcdef1234567890"),
            ("justification", "A different justification.")
        })
        {
            arguments[key] = value;
            AgentMcpApprovalRequest changed = original with { Arguments = arguments };
            Assert.True(DispatchApprovalPresentation.TryCreate(changed, "AREA-1001", out _));
            Assert.False(context.MatchesCurrent(changed, "AREA-1001"));
            arguments[key] = original.Arguments[key];
        }
        Assert.False(context.MatchesCurrent(original with { ExpiresAt = original.ExpiresAt!.Value.AddMinutes(1) }, "AREA-1001"));
    }

    [Fact]
    public void Presenter_UsesCanonicalPackageAndPreservesNestedExactInput()
    {
        AgentMcpApprovalRequest request = Request();
        JsonElement nested = JsonSerializer.SerializeToElement(request.Arguments);
        request = request with { Arguments = new Dictionary<string, object?> { ["request"] = nested } };
        Assert.True(DispatchApprovalPresentation.TryCreate(request, "AREA-1001", out var context));
        Assert.Same(FloodSupportCatalogue.GetPackage("PKG-RIV-060"), context!.Package);
        KeyValuePair<string, string> exact = Assert.Single(context.ExactArguments);
        Assert.Equal("request", exact.Key);
        Assert.Equal(nested.GetRawText(), exact.Value);
        Assert.True(context.MatchesCurrent(request, "AREA-1001"));
    }

    [Fact]
    public void Presenter_RejectsMalformedNestedInputAndCaseVariations()
    {
        AgentMcpApprovalRequest request = Request();
        Assert.False(DispatchApprovalPresentation.TryCreate(request with
        {
            Arguments = new Dictionary<string, object?> { ["request"] = "not an object" }
        }, "AREA-1001", out _));
        Dictionary<string, object?> arguments = new(request.Arguments, StringComparer.Ordinal);
        arguments["PackageVersion"] = arguments["packageVersion"];
        arguments.Remove("packageVersion");
        Assert.False(DispatchApprovalPresentation.TryCreate(request with { Arguments = arguments }, "AREA-1001", out _));
    }

    [Fact]
    public async Task RecordedPackage_ShowsPersistedContextEvenAfterApprovalExpiry()
    {
        (WelfareCaseRecord record, WelfareApprovalRecord approval) = RecordedCase();
        approval = approval with { Request = approval.Request with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) } };
        Assert.True(DispatchApprovalPresentation.TryGetRecordedPackage(record, approval, out var request, out var package));
        Assert.Equal(record.SupportRequest, request);
        Assert.Same(FloodSupportCatalogue.GetPackage("PKG-RIV-060"), package);
        string html = await RenderComponentAsync<RecordedSupportPackage>(new()
        {
            [nameof(RecordedSupportPackage.Case)] = record,
            [nameof(RecordedSupportPackage.Approval)] = approval
        });
        Assert.Contains("PKG-RIV-060", html, StringComparison.Ordinal);
        Assert.Contains("GBP 8,100.00", html, StringComparison.Ordinal);
        Assert.Contains("not a new approval", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecordedPackage_RejectsWrongVersionOrTamperedSnapshot()
    {
        (WelfareCaseRecord record, WelfareApprovalRecord approval) = RecordedCase();
        Assert.False(DispatchApprovalPresentation.TryGetRecordedPackage(record, approval with { CaseVersion = 2 }, out _, out _));
        Assert.False(DispatchApprovalPresentation.TryGetRecordedPackage(record, approval with { CaseId = "OTHER" }, out _, out _));
        Assert.False(DispatchApprovalPresentation.TryGetRecordedPackage(record, approval with { SupportRequest = null }, out _, out _));
        WelfareApprovalRecord tampered = approval with
        {
            PackageSnapshot = approval.PackageSnapshot! with { Rooms = 90, EstimatedCostGbp = 1m }
        };
        Assert.False(DispatchApprovalPresentation.TryGetRecordedPackage(record, tampered, out _, out _));
        string html = await RenderComponentAsync<RecordedSupportPackage>(new()
        {
            [nameof(RecordedSupportPackage.Case)] = record,
            [nameof(RecordedSupportPackage.Approval)] = tampered
        });
        Assert.Contains("No matching package snapshot", html, StringComparison.Ordinal);
        Assert.DoesNotContain("GBP 1.00", html, StringComparison.Ordinal);
        Assert.DoesNotContain("90 rooms", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WelfareRoute.StandardInspectorReview, "Standard coordination review")]
    [InlineData(WelfareRoute.PriorityInspectorReview, "Priority coordination review")]
    [InlineData(WelfareRoute.ImmediateHumanEscalation, "Immediate incident commander review")]
    public void LegacyRoutes_HaveExplicitFloodSupportCaptions(WelfareRoute route, string expected) =>
        Assert.Equal(expected, FloodSupportCaptions.Format(route));

    [Theory]
    [InlineData(WelfareCaseStatus.DispatchApproved, "Reservation approved")]
    [InlineData(WelfareCaseStatus.DispatchDenied, "Reservation rejected")]
    [InlineData(WelfareCaseStatus.VetDispatched, "Package reserved")]
    public void LegacyStatuses_HaveExplicitFloodSupportCaptions(WelfareCaseStatus status, string expected) =>
        Assert.Equal(expected, FloodSupportCaptions.Format(status));

    [Fact]
    public void TimelineAndBoard_DoNotExposeLegacyEventCaptions()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var timeline = AssessmentTimelineItem.From(new(AssessmentEventKind.DispatchCompleted, "Receipt recorded.", now), 1);
        Assert.Equal("Reservation recorded", timeline.Title);
        timeline = AssessmentTimelineItem.From(new(AssessmentEventKind.RepeatOffenderEscalated, "Priority review required.", now), 2);
        Assert.Equal("Recurring unmet need escalated", timeline.Title);
        WelfareAssessmentBoardState board = new();
        board.Start(now, AssessmentExecutionProvider.LocalDeterministic);
        board.MarkApprovalDecision(now, false);
        Assert.Contains("reservation", board.CurrentDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("Foundry", board.CurrentDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void EditableExamplesAndFormErrors_UseFloodSupportLanguage()
    {
        string examples = string.Join(" ", WelfareScenarioCatalogue.All.Select(item =>
            item.Title + " " + item.ComplaintText + " " + item.ExpectedBehavior));
        Assert.DoesNotMatch(new Regex(@"\b(welfare|farm|vet|DEFRA|breed|complaint|inspector)\b", RegexOptions.IgnoreCase), examples);
        List<ValidationResult> errors = [];
        ComplaintFormModel form = new();
        Assert.False(Validator.TryValidateObject(form, new ValidationContext(form), errors, true));
        Assert.All(errors, error => Assert.DoesNotContain("ComplaintText", error.ErrorMessage!, StringComparison.Ordinal));
        Assert.Contains(errors, error => error.ErrorMessage == "Enter a flood situation report.");
    }

    private static AgentMcpApprovalRequest Request(string justification = "Reserve temporary support before accommodation becomes full at 18:00.")
    {
        var arguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["actionRequestId"] = "approval-1234567890abcdef1234567890abcdef",
            ["areaReference"] = "AREA-1001",
            ["incidentReference"] = "INC-12345678",
            ["packageId"] = "PKG-RIV-060",
            ["packageVersion"] = 1,
            ["justification"] = justification,
            ["reservationGeneration"] = "initial"
        };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new("approval-ui-1", "reserveSupportPackage", arguments, arguments.Keys.ToArray(), now)
        {
            ExpiresAt = now.AddMinutes(10)
        };
    }

    private static string Button(string html, string label) =>
        Assert.Single(Regex.Matches(html, @"<button\b[^>]*>.*?</button>", RegexOptions.Singleline)
            .Select(match => match.Value),
            button => Regex.Replace(button, "<[^>]+>", "").Trim() == label);

    private static (WelfareCaseRecord, WelfareApprovalRecord) RecordedCase()
    {
        AgentMcpApprovalRequest original = Request();
        Assert.True(FloodSupportCatalogue.TryParseApprovalArguments(original.Arguments, out var request));
        SupportPackageDefinition package = FloodSupportCatalogue.GetPackage("PKG-RIV-060");
        WelfareCaseRecord record = new("FLOOD-UI-1", Demo2SyntheticOperator.ObjectId, "AREA-1001",
            original.RequestedAt, "hash", WelfareUrgency.High, WelfareRoute.PriorityInspectorReview,
            WelfareCaseStatus.VetDispatched, "Simulated package reserved.", [], false, "response-1",
            original.RequestId, 1, original.RequestedAt)
        {
            SupportRequest = request,
            PackageSnapshot = package,
            IssuedActionRequestId = request!.ActionRequestId,
            IncidentReference = request.IncidentReference
        };
        ApprovalRequest approvalRequest = new(original.RequestId, new CorrelationId("correlation-ui"), original.ToolName,
            original.ArgumentNames, "mcp-approval-required", original.RequestedAt, original.ExpiresAt!.Value);
        WelfareApprovalRecord approval = new(record.CaseId, record.Version, approvalRequest, null, "SIM-RES-0001", original.RequestedAt)
        {
            SupportRequest = request,
            PackageSnapshot = package
        };
        return (record, approval);
    }

    private static Task<string> RenderAsync(
        DispatchApprovalPresentation? context, bool azure = false, bool canApprove = true, bool running = false) =>
        RenderComponentAsync<FloodSupportApprovalContent>(new()
        {
            [nameof(FloodSupportApprovalContent.Context)] = context,
            [nameof(FloodSupportApprovalContent.IsAzureFoundry)] = azure,
            [nameof(FloodSupportApprovalContent.CanApprove)] = canApprove,
            [nameof(FloodSupportApprovalContent.DecisionRunning)] = running,
            [nameof(FloodSupportApprovalContent.Rationale)] = "package-reviewed"
        });

    private static async Task<string> RenderComponentAsync<T>(Dictionary<string, object?> parameters)
        where T : IComponent
    {
        await using ServiceProvider services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using HtmlRenderer renderer = new(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters));
            return rendered.ToHtmlString();
        });
    }
}
