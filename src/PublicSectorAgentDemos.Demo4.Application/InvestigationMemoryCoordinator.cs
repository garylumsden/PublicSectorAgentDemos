using PublicSectorAgentDemos.Contracts.Demo4;

namespace PublicSectorAgentDemos.Demo4.Application;

public enum MemoryWriteStatus
{
    Updated,
    SkippedNonSuccessfulOutcome
}

public sealed record MemoryWriteOutcome(
    MemoryWriteStatus Status,
    NotebookRecordEnvelope? Record,
    string? SerializedRecord);

public sealed class InvestigationMemoryCoordinator(
    IFoundryMemoryItemClient client,
    TimeProvider timeProvider)
{
    public async Task<MemoryWriteOutcome> SaveAsync(
        ValidatedInvestigationResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        TrustedToolExecution[] memorySearches = result.ToolExecutions
            .Where(execution => execution.ToolName == Demo4MemoryContract.SearchToolName)
            .ToArray();
        if (memorySearches.Length > 1)
        {
            throw new UnsafeMemoryReferenceException(
                "memory_search.duplicate",
                "The hosted agent called the read-only Memory function more than once.");
        }

        if (result.Assessment.Outcome is InvestigationOutcome.Refused or InvestigationOutcome.Unsupported &&
            memorySearches.Length != 0)
        {
            throw new UnsafeMemoryReferenceException(
                "memory_search.disallowed",
                "A refused or unsupported request cannot consult Memory.");
        }

        if (memorySearches.Length == 1 &&
            (!memorySearches[0].Succeeded ||
             string.IsNullOrWhiteSpace(memorySearches[0].StructuredResultJson)))
        {
            throw new UnsafeMemoryReferenceException(
                "memory_search.invalid_execution",
                "The Memory search did not complete with one trusted result.");
        }

        if (result.Assessment.Outcome != InvestigationOutcome.Completed)
        {
            return new(MemoryWriteStatus.SkippedNonSuccessfulOutcome, null, null);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        NotebookRecordEnvelope record = NotebookRecordCodec.Create(result, now);
        string serializedRecord = NotebookRecordCodec.Serialize(record);
        await client.CreateAndVerifyAsync(
            record,
            serializedRecord,
            cancellationToken).ConfigureAwait(false);
        return new(MemoryWriteStatus.Updated, record, serializedRecord);
    }
}
