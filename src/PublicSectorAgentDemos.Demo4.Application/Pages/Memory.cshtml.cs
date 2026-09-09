using System.Diagnostics;
using Microsoft.AspNetCore.Mvc.RazorPages;
using PublicSectorAgentDemos.Contracts.Demo4;

namespace PublicSectorAgentDemos.Demo4.Application.Pages;

public sealed class MemoryModel(
    IFoundryMemoryItemClient client,
    TimeProvider timeProvider,
    ILogger<MemoryModel> logger) : PageModel
{
    public IReadOnlyList<MemoryNotebookEntry> Records { get; private set; } = [];

    public int InspectedItemCount { get; private set; }

    public int RejectedItemCount { get; private set; }

    public string? ErrorMessage { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            MemoryNotebookListing listing = await client.ListValidRecordsAsync(
                timeProvider.GetUtcNow(),
                cancellationToken).ConfigureAwait(false);
            Records = listing.Records;
            InspectedItemCount = listing.InspectedItemCount;
            RejectedItemCount = listing.RejectedItemCount;
        }
        catch (Exception exception) when (
            exception is MemoryNotebookReadException or
            MemoryWriteIntegrityException or
            UnsafeMemoryReferenceException or
            HttpRequestException)
        {
            string traceId = Activity.Current?.TraceId.ToString() ??
                HttpContext.TraceIdentifier;
            logger.LogError(
                "Demo 4 shared notebook listing failed. Trace {TraceId}.",
                traceId);
            Response.StatusCode = StatusCodes.Status502BadGateway;
            ErrorMessage =
                $"The shared notebook could not be listed safely. Trace: {traceId}";
        }
    }
}
