using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PublicSectorAgentDemos.Contracts.Demo4;
using PublicSectorAgentDemos.Demo4.Application;

namespace PublicSectorAgentDemos.Demo4.Tests;

public sealed class FoundryMemoryItemClientConsistencyTests
{
    [Fact]
    public async Task RetriesAnImmediateReadAfterWriteNotFoundResponse()
    {
        NotebookRecordEnvelope record = NotebookRecordCodec.Create(
            TestData.Result(InvestigationOutcome.Completed, []),
            DateTimeOffset.UtcNow);
        string serialized = NotebookRecordCodec.Serialize(record);
        ConsistencyHandler handler = new(serialized);
        using HttpClient http = new(handler);
        FoundryMemoryItemClient client = new(
            http,
            new TokenProvider(),
            new MemoryOptions
            {
                ProjectEndpoint =
                    "https://demo.services.ai.azure.com/api/projects/control-investigations"
            });

        await client.CreateAndVerifyAsync(
            record,
            serialized,
            CancellationToken.None);

        Assert.Equal(2, handler.ReadCount);
        Assert.Equal(0, handler.DeleteCount);
    }

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

    private sealed class ConsistencyHandler(string serializedRecord) : HttpMessageHandler
    {
        public int ReadCount { get; private set; }

        public int DeleteCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post &&
                path.EndsWith("/items:list", StringComparison.Ordinal))
            {
                return Respond(HttpStatusCode.OK, JsonSerializer.Serialize(new
                {
                    @object = "list",
                    data = Array.Empty<object>(),
                    first_id = (string?)null,
                    last_id = (string?)null,
                    has_more = false
                }));
            }

            if (request.Method == HttpMethod.Post &&
                path.EndsWith("/items", StringComparison.Ordinal))
            {
                return Respond(
                    HttpStatusCode.OK,
                    JsonSerializer.Serialize(ItemWire()));
            }

            if (request.Method == HttpMethod.Get)
            {
                ReadCount++;
                return ReadCount == 1
                    ? Respond(HttpStatusCode.NotFound, string.Empty)
                    : Respond(
                        HttpStatusCode.OK,
                        JsonSerializer.Serialize(ItemWire()));
            }

            if (request.Method == HttpMethod.Delete)
            {
                DeleteCount++;
                return Respond(HttpStatusCode.OK, string.Empty);
            }

            return Respond(HttpStatusCode.NotFound, string.Empty);
        }

        private object ItemWire() => new
        {
            memory_id = "memory-consistency",
            updated_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            scope = Demo4MemoryContract.Scope,
            content = serializedRecord,
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
