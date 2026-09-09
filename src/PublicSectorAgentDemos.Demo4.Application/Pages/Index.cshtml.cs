using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PublicSectorAgentDemos.Contracts.Demo4;

namespace PublicSectorAgentDemos.Demo4.Application.Pages;

[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class IndexModel(
    AssessmentProcessingService processor,
    ILogger<IndexModel> logger) : PageModel
{
    private static readonly IReadOnlyList<CasePatternScenario> CanonicalScenarios =
    [
        new(
            "CASE-BASELINE-GRANTS",
            1,
            "Grants body: missing supplier assurance",
            "Run this first. It writes the baseline record. This is a first review of this control and the request asks for no other public body, so continuity cannot help and the agent skips Memory.",
            "Memory skipped",
            "Assess case CG-8101. A grants administration body released supplier payments and the assurance evidence is missing."),
        new(
            "CASE-RECURRENCE-HOUSING",
            2,
            "Housing body: same failure repeats",
            "Run this second. The notebook now holds the grants record, so the search matches the same reason code in another case.",
            "Memory searched. Expect a match",
            "Assess case CG-8202. Check whether another public body has already recorded a related control failure."),
        new(
            "CASE-NEAR-MATCH-TRANSPORT",
            3,
            "Transport body: near match ruled out",
            "Run this third. The search returns the earlier records. A different reason code rules them out.",
            "Memory searched. Expect a rule-out",
            "Assess case CG-8303. Check whether a related control failure elsewhere explains the current evidence."),
        new(
            "CASE-UNRELATED-CONTROL",
            4,
            "Audit body: records-retention issue",
            "Run this last. The control type differs, so continuity cannot help and the agent skips Memory.",
            "Memory skipped",
            "Assess case CG-8404. A public audit body reports an overdue records retention schedule.")
    ];

    [BindProperty]
    [Required]
    [StringLength(128)]
    public string ScenarioId { get; set; } = CanonicalScenarios[0].ScenarioId;

    [BindProperty]
    [Required]
    [StringLength(4_000, MinimumLength = 1)]
    public string Prompt { get; set; } = CanonicalScenarios[0].Prompt;

    public IReadOnlyList<CasePatternScenario> Scenarios => CanonicalScenarios;

    public AssessmentProcessingResult? Result { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostInvestigateAsync(
        CancellationToken cancellationToken)
    {
        if (!CanonicalScenarios.Any(
                scenario => string.Equals(
                    scenario.ScenarioId,
                    ScenarioId,
                    StringComparison.Ordinal)))
        {
            ModelState.AddModelError(
                nameof(ScenarioId),
                "Select one of the canonical cross-government control scenarios.");
        }

        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Page();
        }

        string investigationReference =
            $"D4-{Guid.NewGuid():N}"[..15];
        try
        {
            Result = await processor.ProcessDetailedAsync(
                new(investigationReference, ScenarioId, Prompt),
                cancellationToken).ConfigureAwait(false);
            return Page();
        }
        catch (HostedAssessmentProcessingException exception)
        {
            return Fail(exception);
        }
        catch (UnsafeMemoryReferenceException exception)
        {
            return Fail(exception);
        }
        catch (MemoryWriteCompensatedException exception)
        {
            return Fail(exception);
        }
        catch (MemoryWriteIndeterminateException exception)
        {
            return Fail(exception);
        }
        catch (MemoryWriteIntegrityException exception)
        {
            return Fail(exception);
        }
    }

    private PageResult Fail(Exception exception)
    {
        string traceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ??
            HttpContext.TraceIdentifier;
        logger.LogError(
            exception,
            "Demo 4 cross-government control investigation failed. Trace {TraceId}.",
            traceId);
        Response.StatusCode = StatusCodes.Status502BadGateway;
        ModelState.AddModelError(
            string.Empty,
            $"The investigation could not complete safely. Trace: {traceId}");
        return Page();
    }
}

public sealed record CasePatternScenario(
    string ScenarioId,
    int RunOrder,
    string DisplayName,
    string RunGuidance,
    string MemoryExpectation,
    string Prompt);
