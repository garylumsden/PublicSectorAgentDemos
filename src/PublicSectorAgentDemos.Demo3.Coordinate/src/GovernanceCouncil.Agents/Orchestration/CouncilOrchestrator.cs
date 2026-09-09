namespace GovernanceCouncil.Agents.Orchestration;

using System.Collections.Concurrent;
using Azure.AI.Projects;
using GovernanceCouncil.Core.Interfaces;
using GovernanceCouncil.Core.Models;
using GovernanceCouncil.Agents.Debate;
using GovernanceCouncil.Agents.Knowledge;
using GovernanceCouncil.Agents.Nexus;
using GovernanceCouncil.Agents.Provisioning;
using Microsoft.Extensions.Logging;

/// <summary>
/// Coordinates the Governance Council deliberation lifecycle: provisions the Foundry Prompt Agents
/// (via <see cref="AgentProvisioner"/>), records each deliberation, then runs a live council debate
/// (<see cref="CouncilDebate"/>) and parses the Chair's verdict (<see cref="AssessmentParser"/>).
/// The orchestrator is plumbing — all AI decision-making happens in the Foundry agents.
/// </summary>
public sealed class CouncilOrchestrator
{
    private readonly IDeliberationNotifier _notifier;
    private readonly IDossierStore _dossierStore;
    private readonly IDossierBlobService _blobService;
    private readonly IDeliberationStore _deliberationStore;
    private readonly IAssessmentStore _assessmentStore;
    private readonly NexusAnalystService? _nexusAnalyst;
    private readonly GovernanceCouncil.Agents.Runtime.CouncilRuntimeProvider _runtimes;
    private readonly GovernanceCouncil.Agents.Provisioning.RaiPolicyManager? _raiPolicy;
    private readonly KnowledgeBaseManager? _knowledgeBase;
    private readonly ILogger<CouncilOrchestrator> _logger;

    public CouncilOrchestrator(
        IDeliberationNotifier notifier,
        IDossierStore dossierStore,
        IDossierBlobService blobService,
        IDeliberationStore deliberationStore,
        IAssessmentStore assessmentStore,
        GovernanceCouncil.Agents.Runtime.CouncilRuntimeProvider runtimes,
        NexusAnalystService? nexusAnalyst = null,
        GovernanceCouncil.Agents.Provisioning.RaiPolicyManager? raiPolicy = null,
        KnowledgeBaseManager? knowledgeBase = null,
        ILogger<CouncilOrchestrator>? logger = null)
    {
        _notifier = notifier;
        _dossierStore = dossierStore;
        _blobService = blobService;
        _deliberationStore = deliberationStore;
        _assessmentStore = assessmentStore;
        _nexusAnalyst = nexusAnalyst;
        _runtimes = runtimes;
        _raiPolicy = raiPolicy;
        _knowledgeBase = knowledgeBase;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CouncilOrchestrator>.Instance;
    }

    private readonly SemaphoreSlim _reprovisionLock = new(1, 1);

    /// <summary>True while a runtime profile switch is re-provisioning the agents.</summary>
    public bool IsReprovisioning { get; private set; }

    /// <summary>
    /// Applies the council settings (model profile, grounding provider, agent runtime) in one shot and
    /// rebuilds the active runtime a single time so the change is picked up. The runtime mode is set
    /// first so <c>_runtimes.Active</c> resolves to the chosen runtime, then a reload re-provisions
    /// every agent on the new model set + grounding. Serialised via a lock — avoid calling while a
    /// deliberation is in progress.
    /// </summary>
    public async Task ApplySettingsAsync(
        CouncilModels.ModelProfile profile, Grounding.Provider provider, AgentRuntime.Mode runtime,
        CancellationToken ct = default)
    {
        await _reprovisionLock.WaitAsync(ct);
        try
        {
            IsReprovisioning = true;
            _logger.LogInformation(
                "Applying council settings: profile {Profile}, grounding {Provider}, runtime {Runtime}",
                profile, provider, runtime);
            CouncilModels.SetProfile(profile);
            Grounding.SetProvider(provider);
            AgentRuntime.SetMode(runtime);

            // Foundry IQ is self-provisioning: switching to it at runtime must ensure the KB exists
            // (startup only provisions it when Foundry IQ is the boot provider). A KB hiccup must not
            // block the runtime reload — same isolation as the startup path.
            if (provider == Grounding.Provider.FoundryIq && _knowledgeBase is { IsConfigured: true })
            {
                try
                {
                    await _knowledgeBase.EnsureKnowledgeBaseAsync(ct);
                }
                catch (Exception kbEx)
                {
                    _logger.LogError(kbEx, "Failed to ensure Foundry IQ knowledge base while applying settings");
                }
            }

            await _runtimes.Active.ReloadAsync(ct);
            _logger.LogInformation("Council settings applied");
        }
        finally
        {
            IsReprovisioning = false;
            _reprovisionLock.Release();
        }
    }

    /// <summary>Prepares the active runtime's agents (Foundry: provision + fetch; MAF: build locally).</summary>
    public Task ProvisionAgentsAsync(CancellationToken ct = default) => _runtimes.Active.LoadAsync(ct);

    /// <summary>
    /// Creates the deliberation record in Cosmos before the background workflow starts.
    /// Called from the UI thread so the live page can find the record immediately.
    /// </summary>
    // Guards against starting the same deliberation twice (e.g. a refresh or a second tab). Static so
    // the guard holds regardless of the orchestrator's DI lifetime.
    private static readonly ConcurrentDictionary<string, byte> _startedDeliberations = new();

    public async Task CreateDeliberationRecordAsync(string dossierId, string deliberationId, CancellationToken ct = default)
    {
        var deliberation = new Deliberation
        {
            Id = deliberationId,
            DeliberationId = deliberationId,
            DossierId = dossierId,
            Status = DeliberationStatus.Pending,
            SubmittedDate = DateTimeOffset.UtcNow
        };
        await _deliberationStore.UpsertAsync(deliberation, ct);
        _logger.LogInformation("Deliberation record {DeliberationId} created for dossier {DossierId}", deliberationId, dossierId);
    }

    /// <summary>
    /// Starts a deliberation in the background exactly once per id. Called by the live page AFTER the
    /// viewer has joined the SignalR group, so the opening phases (Independent Assessment, roll-call)
    /// are not emitted before anyone is listening — the debate no longer races ahead of the client.
    /// </summary>
    public void StartDeliberationOnce(string dossierId, string deliberationId, string? violenceThreshold = null)
    {
        if (!_startedDeliberations.TryAdd(deliberationId, 1)) return;
        _ = Task.Run(async () =>
        {
            try { await RunDeliberationAsync(dossierId, existingDeliberationId: deliberationId, violenceThreshold: violenceThreshold); }
            catch { /* failures are recorded on the deliberation record by the orchestrator */ }
        });
    }

    /// <summary>
    /// Runs a full deliberation for a given dossier: the council debates live via the MAF
    /// debate engine and the Chair produces a final Assessment.
    /// </summary>
    public async Task<Assessment> RunDeliberationAsync(
        string dossierId,
        string? context = null,
        string? existingDeliberationId = null,
        string? violenceThreshold = null,
        CancellationToken ct = default)
    {
        var dossier = await _dossierStore.GetAsync(dossierId, ct);
        var markdown = await _blobService.GetMarkdownAsync(dossier.MarkdownBlobPath, ct);

        var deliberationId = existingDeliberationId ?? $"DL-{DateTime.UtcNow:yyyy}-{Guid.NewGuid().ToString("N")[..8]}";

        Deliberation deliberation;
        if (existingDeliberationId is not null)
        {
            deliberation = await _deliberationStore.GetAsync(existingDeliberationId, ct);
            if (deliberation.Status == DeliberationStatus.Pending)
            {
                deliberation = deliberation with { Status = DeliberationStatus.InProgress };
                await _deliberationStore.UpsertAsync(deliberation, ct);
            }
        }
        else
        {
            deliberation = new Deliberation
            {
                Id = deliberationId,
                DeliberationId = deliberationId,
                DossierId = dossierId,
                Status = DeliberationStatus.InProgress,
                Context = context,
                SubmittedDate = DateTimeOffset.UtcNow
            };
            await _deliberationStore.UpsertAsync(deliberation, ct);
        }

        await _notifier.DeliberationStartedAsync(deliberationId);
        _logger.LogInformation("Deliberation {DeliberationId} started for dossier {DossierId}", deliberationId, dossierId);

        try
        {
            // Per-deliberation content-filter toggle: update the account RAI policy's Violence threshold
            // before the debate so the deployments pick it up. Account-global + a few seconds to
            // propagate (single-presenter demo only). No-ops when no threshold is chosen.
            if (_raiPolicy is not null && !string.IsNullOrWhiteSpace(violenceThreshold))
            {
                await _notifier.ContentSafetyApplyingAsync(deliberationId, true);
                await _raiPolicy.EnsureViolenceThresholdAsync(violenceThreshold, ct);
                await _notifier.ContentSafetyApplyingAsync(deliberationId, false);
            }

            var dossierPrompt = DossierPromptBuilder.Build(dossier, markdown, context);

            // Live council debate (hands-up / push-to-talk) via the MAF debate engine. Agents are the
            // unified tooled+grounded Foundry agents from the startup cache.
            var debate = new CouncilDebate(_runtimes.Active, _notifier, _logger);
            var (assessmentText, transcript) = await debate.RunAsync(deliberationId, dossierPrompt, ct);

            // A content-safety (RAI) block is a non-response, not a failure: complete with a graceful
            // "Defer" assessment whose Chair Summary tells the user clearly what happened (and how to
            // relax the policy) rather than throwing the generic no-Assessment error.
            var contentFiltered = assessmentText == ContentSafety.BlockedMarker;
            Assessment assessment;
            if (contentFiltered)
            {
                _logger.LogWarning("Deliberation {DeliberationId}: Chair synthesis blocked by the content-safety policy — returning a graceful Defer assessment", deliberationId);
                assessment = AssessmentParser.ContentFilteredFallback(dossierId, dossier.Title, deliberationId);
            }
            else if (string.IsNullOrWhiteSpace(assessmentText) || assessmentText == "{}")
            {
                _logger.LogWarning("Council debate returned empty assessment. Transcript entries: {Count}", transcript.Count);
                throw new InvalidOperationException("Council debate completed but the Chair did not produce an Assessment.");
            }
            else
            {
                assessment = AssessmentParser.Parse(assessmentText, dossierId, dossier.Title, deliberationId);
            }

            // Embed once at creation so nexus Top-K is a cheap Cosmos vector query (no re-embedding).
            // Skip for the content-filtered fallback: its summary is boilerplate, so an embedding +
            // nexus pass would only add noise.
            if (_nexusAnalyst is not null && !contentFiltered)
            {
                var embedding = await _nexusAnalyst.EmbedAssessmentAsync(assessment, ct);
                if (embedding is not null)
                    assessment = assessment with { Embedding = embedding, EmbeddingModel = "text-embedding-3-small" };
            }

            await _assessmentStore.UpsertAsync(assessment, ct);

            // Post-deliberation nexus discovery — must never fail the deliberation. Surfaced in the
            // live thread (running → found N) the same way the Chair's synthesis turn is.
            if (_nexusAnalyst is not null && !contentFiltered)
            {
                var nexusKey = $"gc-{CouncilMembers.NexusAnalyst.Id}";
                try
                {
                    await _notifier.DebatePhaseAsync(deliberationId, "Nexus");
                    await _notifier.NexusAnalysisAsync(deliberationId, nexusKey, null);
                    var nexuses = await _nexusAnalyst.AnalyseAsync(assessment.AssessmentId, ct);
                    await _notifier.NexusAnalysisAsync(deliberationId, nexusKey, nexuses.Count);
                }
                catch (Exception nexusEx) { _logger.LogWarning(nexusEx, "Nexus analysis failed for assessment {AssessmentId}", assessment.AssessmentId); }
            }

            deliberation = deliberation with
            {
                Status = DeliberationStatus.Completed,
                AssessmentId = assessment.AssessmentId,
                Transcript = transcript,
                CompletedDate = DateTimeOffset.UtcNow
            };
            await _deliberationStore.UpsertAsync(deliberation, ct);

            await _notifier.DeliberationCompleteAsync(deliberationId, assessment.AssessmentId);
            _logger.LogInformation("Deliberation {DeliberationId} completed with assessment {AssessmentId}", deliberationId, assessment.AssessmentId);
            return assessment;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deliberation {DeliberationId} failed", deliberationId);
            deliberation = deliberation with
            {
                Status = DeliberationStatus.Failed,
                ErrorMessage = ex.Message,
                CompletedDate = DateTimeOffset.UtcNow
            };
            await _deliberationStore.UpsertAsync(deliberation, ct);

            await _notifier.DeliberationErrorAsync(deliberationId, ex.Message);
            throw;
        }
    }

}
