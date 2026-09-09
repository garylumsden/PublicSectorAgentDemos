using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PublicSectorAgentDemos.Contracts.Demo4;
using PublicSectorAgentDemos.Demo4.Application;

namespace PublicSectorAgentDemos.Demo4.Tests;

public sealed class FoundryMemoryItemClientPaginationTests
{
    [Fact]
    public async Task ReconciliationFindsAnExistingRecordAfterMoreThanThirtyTwoItems()
    {
        NotebookRecordEnvelope record = CreateRecord();
        string serialized = NotebookRecordCodec.Serialize(record);
        object[] firstPage = Enumerable.Range(0, 32)
            .Select(index => ItemWire($"memory-{index}", "{}"))
            .ToArray();
        PagingHandler handler = new(page => page switch
        {
            0 => ListWire(firstPage, true, "memory-31"),
            1 => ListWire([ItemWire("memory-target", serialized)], false, "memory-target"),
            _ => throw new InvalidOperationException("Unexpected page.")
        });
        using HttpClient http = new(handler);
        FoundryMemoryItemClient client = CreateClient(http);

        await client.CreateAndVerifyAsync(record, serialized, CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        Assert.DoesNotContain("after=", handler.Requests[0].Query);
        Assert.Contains("after=memory-31", handler.Requests[1].Query);
    }

    [Fact]
    public async Task ReconciliationRejectsAPaginationCycle()
    {
        PagingHandler handler = new(page => page switch
        {
            0 => ListWire([ItemWire("memory-1", "{}")], true, "cursor-a"),
            1 => ListWire([ItemWire("memory-2", "{}")], true, "cursor-b"),
            2 => ListWire([ItemWire("memory-3", "{}")], true, "cursor-a"),
            _ => throw new InvalidOperationException("Unexpected page.")
        });
        using HttpClient http = new(handler);
        FoundryMemoryItemClient client = CreateClient(http);
        NotebookRecordEnvelope record = CreateRecord();

        MemoryWriteIndeterminateException exception =
            await Assert.ThrowsAsync<MemoryWriteIndeterminateException>(() =>
                client.CreateAndVerifyAsync(
                    record,
                    NotebookRecordCodec.Serialize(record),
                    CancellationToken.None));

        Assert.Contains("pagination cycle", exception.Message, StringComparison.Ordinal);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task ReconciliationRejectsAnItemRepeatedAcrossPages()
    {
        PagingHandler handler = new(page => page switch
        {
            0 => ListWire([ItemWire("memory-duplicate", "{}")], true, "cursor-a"),
            1 => ListWire([ItemWire("memory-duplicate", "{}")], false, "memory-duplicate"),
            _ => throw new InvalidOperationException("Unexpected page.")
        });
        using HttpClient http = new(handler);
        FoundryMemoryItemClient client = CreateClient(http);
        NotebookRecordEnvelope record = CreateRecord();

        MemoryWriteIndeterminateException exception =
            await Assert.ThrowsAsync<MemoryWriteIndeterminateException>(() =>
                client.CreateAndVerifyAsync(
                    record,
                    NotebookRecordCodec.Serialize(record),
                    CancellationToken.None));

        Assert.Contains("duplicate item", exception.Message, StringComparison.Ordinal);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task ReconciliationStopsAtThePageBound()
    {
        PagingHandler handler = new(page => ListWire(
            [ItemWire($"memory-{page}", "{}")],
            true,
            $"cursor-{page}"));
        using HttpClient http = new(handler);
        FoundryMemoryItemClient client = CreateClient(http);
        NotebookRecordEnvelope record = CreateRecord();

        MemoryWriteIndeterminateException exception =
            await Assert.ThrowsAsync<MemoryWriteIndeterminateException>(() =>
                client.CreateAndVerifyAsync(
                    record,
                    NotebookRecordCodec.Serialize(record),
                    CancellationToken.None));

        Assert.Contains("page limit", exception.Message, StringComparison.Ordinal);
        Assert.Equal(
            FoundryMemoryItemClient.MaximumReconciliationPages,
            handler.Requests.Count);
    }

    [Fact]
    public async Task ReconciliationStopsAtTheRecordBound()
    {
        PagingHandler handler = new(page =>
        {
            int count = page < 8 ? 32 : 1;
            object[] items = Enumerable.Range(0, count)
                .Select(index => ItemWire($"memory-{page}-{index}", "{}"))
                .ToArray();
            return ListWire(items, page < 8, $"cursor-{page}");
        });
        using HttpClient http = new(handler);
        FoundryMemoryItemClient client = CreateClient(http);
        NotebookRecordEnvelope record = CreateRecord();

        MemoryWriteIndeterminateException exception =
            await Assert.ThrowsAsync<MemoryWriteIndeterminateException>(() =>
                client.CreateAndVerifyAsync(
                    record,
                    NotebookRecordCodec.Serialize(record),
                    CancellationToken.None));

        Assert.Contains("record limit", exception.Message, StringComparison.Ordinal);
        Assert.Equal(9, handler.Requests.Count);
    }

    private static FoundryMemoryItemClient CreateClient(HttpClient httpClient) =>
        new(
            httpClient,
            new TokenProvider(),
            new MemoryOptions
            {
                ProjectEndpoint =
                    "https://demo.services.ai.azure.com/api/projects/control-investigations"
            });

    private static NotebookRecordEnvelope CreateRecord() =>
        NotebookRecordCodec.Create(
            TestData.Result(InvestigationOutcome.Completed, []),
            DateTimeOffset.UtcNow);

    private static object ItemWire(string memoryId, string content) => new
    {
        memory_id = memoryId,
        updated_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        scope = Demo4MemoryContract.Scope,
        content,
        kind = FoundryMemoryItemClient.ItemKind
    };

    private static string ListWire(
        object[] items,
        bool hasMore,
        string? lastId) =>
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
}
