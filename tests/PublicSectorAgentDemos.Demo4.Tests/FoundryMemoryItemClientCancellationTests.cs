using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PublicSectorAgentDemos.Contracts.Demo4;
using PublicSectorAgentDemos.Demo4.Application;

namespace PublicSectorAgentDemos.Demo4.Tests;

public sealed class FoundryMemoryItemClientCancellationTests
{
    [Fact]
    public async Task CompensatesACommitWhenCancellationOccursDuringCreate()
    {
        NotebookRecordEnvelope record = CreateRecord();
        string serialized = NotebookRecordCodec.Serialize(record);
        using CancellationTokenSource callerCancellation = new();
        CancellationHandler handler = new(
            serialized,
            callerCancellation,
            cancelDuringCreate: true);
        using HttpClient http = new(handler);
        FoundryMemoryItemClient client = CreateClient(http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CreateAndVerifyAsync(
                record,
                serialized,
                callerCancellation.Token));

        Assert.False(handler.ItemExists);
        Assert.Equal(1, handler.DeleteCount);
        Assert.True(handler.CleanupUsedIndependentToken);
    }

    [Fact]
    public async Task CompensatesACommitWhenCancellationOccursDuringVerification()
    {
        NotebookRecordEnvelope record = CreateRecord();
        string serialized = NotebookRecordCodec.Serialize(record);
        using CancellationTokenSource callerCancellation = new();
        CancellationHandler handler = new(
            serialized,
            callerCancellation,
            cancelDuringCreate: false);
        using HttpClient http = new(handler);
        FoundryMemoryItemClient client = CreateClient(http);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.CreateAndVerifyAsync(
                record,
                serialized,
                callerCancellation.Token));

        Assert.False(handler.ItemExists);
        Assert.Equal(1, handler.DeleteCount);
        Assert.True(handler.CleanupUsedIndependentToken);
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

    private sealed class CancellationHandler(
        string serializedRecord,
        CancellationTokenSource callerCancellation,
        bool cancelDuringCreate)
        : HttpMessageHandler
    {
        private bool _verificationCancellationIssued;

        public bool ItemExists { get; private set; }

        public int DeleteCount { get; private set; }

        public bool CleanupUsedIndependentToken { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (callerCancellation.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                CleanupUsedIndependentToken = true;
            }

            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post &&
                path.EndsWith("/items:list", StringComparison.Ordinal))
            {
                object[] items = ItemExists ? [ItemWire(serializedRecord)] : [];
                return Respond(
                    HttpStatusCode.OK,
                    JsonSerializer.Serialize(new
                    {
                        @object = "list",
                        data = items,
                        first_id = ItemExists ? "memory-target" : null,
                        last_id = ItemExists ? "memory-target" : null,
                        has_more = false
                    }));
            }

            if (request.Method == HttpMethod.Post &&
                path.EndsWith("/items", StringComparison.Ordinal))
            {
                ItemExists = true;
                if (cancelDuringCreate)
                {
                    callerCancellation.Cancel();
                    return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
                }

                return Respond(
                    HttpStatusCode.Created,
                    JsonSerializer.Serialize(ItemWire(serializedRecord)));
            }

            if (request.Method == HttpMethod.Get && ItemExists)
            {
                if (!_verificationCancellationIssued)
                {
                    _verificationCancellationIssued = true;
                    callerCancellation.Cancel();
                    return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
                }

                return Respond(
                    HttpStatusCode.OK,
                    JsonSerializer.Serialize(ItemWire(serializedRecord)));
            }

            if (request.Method == HttpMethod.Delete)
            {
                ItemExists = false;
                DeleteCount++;
                return Respond(HttpStatusCode.NoContent, string.Empty);
            }

            return Respond(HttpStatusCode.NotFound, string.Empty);
        }

        private static object ItemWire(string content) => new
        {
            memory_id = "memory-target",
            updated_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            scope = Demo4MemoryContract.Scope,
            content,
            kind = FoundryMemoryItemClient.ItemKind
        };

        private static Task<HttpResponseMessage> Respond(
            HttpStatusCode status,
            string body) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
    }
}
