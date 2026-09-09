using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PublicSectorAgentDemos.Contracts.Demo4;
using PublicSectorAgentDemos.Demo4.Application;

namespace PublicSectorAgentDemos.Demo4.Tests;

public sealed class MemoryNotebookListingTests
{
    [Fact]
    public async Task ListingReturnsEveryValidRecordAcrossBoundedPages()
    {
        NotebookRecordEnvelope first = Record("investigation-7001");
        NotebookRecordEnvelope second = Record("investigation-7002");
        PagingHandler handler = new(page => page switch
        {
            0 => ListWire(
                [Item("memory-1", first, Now.AddHours(-5))],
                true,
                "memory-1"),
            1 => ListWire(
                [Item("memory-2", second, Now.AddHours(-1))],
                false,
                "memory-2"),
            _ => throw new InvalidOperationException("Unexpected page.")
        });
        using HttpClient http = new(handler);

        MemoryNotebookListing listing = await CreateClient(http)
            .ListValidRecordsAsync(Now, CancellationToken.None);

        Assert.Equal(2, listing.Records.Count);
        Assert.Equal(2, listing.InspectedItemCount);
        Assert.Equal(0, listing.RejectedItemCount);
        Assert.Equal("memory-2", listing.Records[0].MemoryId);
        Assert.Equal(second.RecordId, listing.Records[0].Record.RecordId);
        Assert.Equal("memory-1", listing.Records[1].MemoryId);
        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain("after=", handler.Requests[0].Query);
        Assert.Contains("after=memory-1", handler.Requests[1].Query);
        Assert.Contains("limit=32", handler.Requests[0].Query);
    }

    [Fact]
    public async Task ListingCountsInvalidAndForeignItemsWithoutRenderingThem()
    {
        NotebookRecordEnvelope valid = Record("investigation-7003");
        PagingHandler handler = new(_ => ListWire(
            [
                Item("memory-valid", valid, Now.AddMinutes(-30)),
                ItemWire("memory-not-a-record", "{\"kind\":\"other\"}", Now, Demo4MemoryContract.Scope, FoundryMemoryItemClient.ItemKind),
                ItemWire("memory-foreign-scope", NotebookRecordCodec.Serialize(valid), Now, "another-application-scope", FoundryMemoryItemClient.ItemKind),
                ItemWire("memory-foreign-kind", NotebookRecordCodec.Serialize(valid), Now, Demo4MemoryContract.Scope, "user_profile"),
                ItemWire("memory-expired", NotebookRecordCodec.Serialize(valid), Now.AddDays(-9), Demo4MemoryContract.Scope, FoundryMemoryItemClient.ItemKind)
            ],
            false,
            "memory-expired"));
        using HttpClient http = new(handler);

        MemoryNotebookListing listing = await CreateClient(http)
            .ListValidRecordsAsync(Now, CancellationToken.None);

        MemoryNotebookEntry entry = Assert.Single(listing.Records);
        Assert.Equal("memory-valid", entry.MemoryId);
        Assert.Equal(5, listing.InspectedItemCount);
        Assert.Equal(4, listing.RejectedItemCount);
    }

    [Fact]
    public async Task ListingRejectsARepeatedApplicationRecord()
    {
        NotebookRecordEnvelope duplicated = Record("investigation-7004");
        PagingHandler handler = new(_ => ListWire(
            [
                Item("memory-1", duplicated, Now.AddMinutes(-10)),
                Item("memory-2", duplicated, Now.AddMinutes(-5))
            ],
            false,
            "memory-2"));
        using HttpClient http = new(handler);

        MemoryNotebookListing listing = await CreateClient(http)
            .ListValidRecordsAsync(Now, CancellationToken.None);

        MemoryNotebookEntry entry = Assert.Single(listing.Records);
        Assert.Equal("memory-1", entry.MemoryId);
        Assert.Equal(1, listing.RejectedItemCount);
    }

    [Fact]
    public async Task ListingReturnsAnEmptyNotebookSafely()
    {
        PagingHandler handler = new(_ => ListWire([], false, null));
        using HttpClient http = new(handler);

        MemoryNotebookListing listing = await CreateClient(http)
            .ListValidRecordsAsync(Now, CancellationToken.None);

        Assert.Empty(listing.Records);
        Assert.Equal(0, listing.InspectedItemCount);
        Assert.Equal(0, listing.RejectedItemCount);
    }

    [Fact]
    public async Task ListingStopsAtThePageBound()
    {
        PagingHandler handler = new(page => ListWire(
            [ItemWire($"memory-{page}", "{}", Now, Demo4MemoryContract.Scope, FoundryMemoryItemClient.ItemKind)],
            true,
            $"cursor-{page}"));
        using HttpClient http = new(handler);

        MemoryNotebookReadException exception =
            await Assert.ThrowsAsync<MemoryNotebookReadException>(() =>
                CreateClient(http).ListValidRecordsAsync(Now, CancellationToken.None));

        Assert.Contains("page limit", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            FoundryMemoryItemClient.MaximumReconciliationPages,
            handler.Requests.Count);
    }

    [Fact]
    public async Task ListingStopsAtTheRecordBound()
    {
        PagingHandler handler = new(page =>
        {
            int count = page < 8 ? 32 : 1;
            object[] items = Enumerable.Range(0, count)
                .Select(index => ItemWire(
                    $"memory-{page}-{index}",
                    "{}",
                    Now,
                    Demo4MemoryContract.Scope,
                    FoundryMemoryItemClient.ItemKind))
                .ToArray();
            return ListWire(items, page < 8, $"cursor-{page}");
        });
        using HttpClient http = new(handler);

        MemoryNotebookReadException exception =
            await Assert.ThrowsAsync<MemoryNotebookReadException>(() =>
                CreateClient(http).ListValidRecordsAsync(Now, CancellationToken.None));

        Assert.Contains("record limit", exception.Message, StringComparison.Ordinal);
        Assert.Equal(9, handler.Requests.Count);
    }

    [Fact]
    public async Task ListingRejectsAPaginationCycle()
    {
        PagingHandler handler = new(page => page switch
        {
            0 => ListWire([ItemWire("memory-1", "{}", Now, Demo4MemoryContract.Scope, FoundryMemoryItemClient.ItemKind)], true, "cursor-a"),
            1 => ListWire([ItemWire("memory-2", "{}", Now, Demo4MemoryContract.Scope, FoundryMemoryItemClient.ItemKind)], true, "cursor-b"),
            2 => ListWire([ItemWire("memory-3", "{}", Now, Demo4MemoryContract.Scope, FoundryMemoryItemClient.ItemKind)], true, "cursor-a"),
            _ => throw new InvalidOperationException("Unexpected page.")
        });
        using HttpClient http = new(handler);

        MemoryNotebookReadException exception =
            await Assert.ThrowsAsync<MemoryNotebookReadException>(() =>
                CreateClient(http).ListValidRecordsAsync(Now, CancellationToken.None));

        Assert.Contains("pagination cycle", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListingRejectsAnItemRepeatedAcrossPages()
    {
        PagingHandler handler = new(page => page switch
        {
            0 => ListWire([ItemWire("memory-same", "{}", Now, Demo4MemoryContract.Scope, FoundryMemoryItemClient.ItemKind)], true, "cursor-a"),
            1 => ListWire([ItemWire("memory-same", "{}", Now, Demo4MemoryContract.Scope, FoundryMemoryItemClient.ItemKind)], false, "memory-same"),
            _ => throw new InvalidOperationException("Unexpected page.")
        });
        using HttpClient http = new(handler);

        MemoryNotebookReadException exception =
            await Assert.ThrowsAsync<MemoryNotebookReadException>(() =>
                CreateClient(http).ListValidRecordsAsync(Now, CancellationToken.None));

        Assert.Contains("duplicate item", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListingReportsATransportFailureWithoutContent()
    {
        StatusHandler handler = new(HttpStatusCode.Forbidden);
        using HttpClient http = new(handler);

        MemoryNotebookReadException exception =
            await Assert.ThrowsAsync<MemoryNotebookReadException>(() =>
                CreateClient(http).ListValidRecordsAsync(Now, CancellationToken.None));

        Assert.Equal("Memory item list returned HTTP 403.", exception.Message);
    }

    [Fact]
    public async Task ListingRejectsAnOversizedResponse()
    {
        StatusHandler handler = new(
            HttpStatusCode.OK,
            new string('a', 128 * 1024));
        using HttpClient http = new(handler);

        await Assert.ThrowsAsync<MemoryWriteIntegrityException>(() =>
            CreateClient(http).ListValidRecordsAsync(Now, CancellationToken.None));
    }

    private static DateTimeOffset Now => TestData.Now;

    private static FoundryMemoryItemClient CreateClient(HttpClient httpClient) =>
        new(
            httpClient,
            new TokenProvider(),
            new MemoryOptions
            {
                ProjectEndpoint =
                    "https://demo.services.ai.azure.com/api/projects/control-investigations"
            });

    private static NotebookRecordEnvelope Record(string investigationReference)
    {
        ValidatedInvestigationResult seed = TestData.Result(
            InvestigationOutcome.Completed,
            []);
        return NotebookRecordCodec.Create(
            seed with { InvestigationReference = investigationReference },
            Now.AddHours(-6));
    }

    private static object Item(
        string memoryId,
        NotebookRecordEnvelope record,
        DateTimeOffset updatedAt) =>
        ItemWire(
            memoryId,
            NotebookRecordCodec.Serialize(record),
            updatedAt,
            Demo4MemoryContract.Scope,
            FoundryMemoryItemClient.ItemKind);

    private static object ItemWire(
        string memoryId,
        string content,
        DateTimeOffset updatedAt,
        string scope,
        string kind) => new
        {
            memory_id = memoryId,
            updated_at = updatedAt.ToUnixTimeSeconds(),
            scope,
            content,
            kind
        };

    private static string ListWire(object[] items, bool hasMore, string? lastId) =>
        JsonSerializer.Serialize(new
        {
            @object = "list",
            data = items,
            first_id = items.Length == 0 ? null : "first",
            last_id = lastId,
            has_more = hasMore
        });

    private sealed class TokenProvider : IFoundryAccessTokenProvider
    {
        public ValueTask<AccessToken> GetTokenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AccessToken(
                "managed-identity-token",
                DateTimeOffset.UtcNow.AddMinutes(5)));
        }
    }

    private sealed class PagingHandler(Func<int, string> responseFactory) : HttpMessageHandler
    {
        private int _page;

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            string body = responseFactory(_page++);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class StatusHandler(HttpStatusCode status, string? body = null)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    body ?? "{}",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
