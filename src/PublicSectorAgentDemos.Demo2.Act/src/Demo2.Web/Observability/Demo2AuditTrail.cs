using Defra.Audit;
using Defra.Contracts.V1;
using Demo2.Web.Domain;

namespace Demo2.Web.Observability;

public sealed class Demo2AuditTrail(
    IAppendOnlyAuditWriter writer,
    IWelfareRuleProvider ruleProvider,
    TimeProvider timeProvider)
{
    public ValueTask WriteAsync(
        AuditEventKind kind,
        CorrelationId correlationId,
        string stage,
        string tool,
        string decision,
        string outcome,
        string inputHash,
        long durationMs = 0,
        string? approvalRequestId = null,
        CancellationToken cancellationToken = default)
    {
        WelfareRuleSet rules = ruleProvider.Rules;
        string? traceId = System.Diagnostics.Activity.Current?.TraceId.ToString();
        SanitizedAuditEvent auditEvent = new(
            Guid.NewGuid().ToString("N"),
            timeProvider.GetUtcNow(),
            correlationId,
            kind,
            "demo2",
            "cross-government-flood-support",
            stage,
            tool,
            decision,
            rules.ContractVersion,
            outcome,
            durationMs,
            inputHash,
            rules.RuleSetId,
            approvalRequestId,
            traceId);
        return writer.AppendAsync(auditEvent, cancellationToken);
    }
}
