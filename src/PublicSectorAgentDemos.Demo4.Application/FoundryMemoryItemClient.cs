using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using PublicSectorAgentDemos.Contracts.Demo4;

namespace PublicSectorAgentDemos.Demo4.Application;

public interface IFoundryAccessTokenProvider
{
    public ValueTask<AccessToken> GetTokenAsync(CancellationToken cancellationToken);
}

public sealed class AzureFoundryAccessTokenProvider(TokenCredential credential)
    : IFoundryAccessTokenProvider
{
    private static readonly TokenRequestContext TokenContext =
        new(["https://ai.azure.com/.default"]);

    public ValueTask<AccessToken> GetTokenAsync(CancellationToken cancellationToken) =>
        credential.GetTokenAsync(TokenContext, cancellationToken);
}

public interface IFoundryMemoryItemClient
{
    public Task CreateAndVerifyAsync(
        NotebookRecordEnvelope record,
        string serializedRecord,
        CancellationToken cancellationToken);

    public Task<MemoryNotebookListing> ListValidRecordsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken);
}

public sealed record MemoryNotebookEntry(
    string MemoryId,
    DateTimeOffset UpdatedAt,
    NotebookRecordEnvelope Record);

public sealed record MemoryNotebookListing(
    IReadOnlyList<MemoryNotebookEntry> Records,
    int InspectedItemCount,
    int RejectedItemCount);

public sealed class FoundryMemoryItemClient : IFoundryMemoryItemClient
{
    public const string ApiVersion = "2025-11-15-preview";
    public const string PreviewFeature = "MemoryStores=V1Preview";
    public const string ItemKind = "chat_summary";
    private const int MaximumResponseBytes = 64 * 1024;
    private const int MaximumReadAfterWriteAttempts = 8;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadAfterWriteDelay = TimeSpan.FromMilliseconds(250);
    internal const int MaximumReconciliationPages = 16;
    internal const int MaximumReconciliationRecords = 256;
    private static readonly PageFailures ReconciliationFailures = new(
        message => new InvalidOperationException(message),
        message => new MemoryWriteIndeterminateException(message));
    private static readonly PageFailures ListingFailures = new(
        message => new MemoryNotebookReadException(message),
        message => new MemoryNotebookReadException(message));
    private readonly HttpClient _httpClient;
    private readonly IFoundryAccessTokenProvider _tokenProvider;
    private readonly MemoryOptions _options;
    private readonly Uri _itemsEndpoint;
    private readonly ReferenceCountedKeyedLock _locks = new();
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false
        };

    public FoundryMemoryItemClient(
        HttpClient httpClient,
        IFoundryAccessTokenProvider tokenProvider,
        MemoryOptions options)
    {
        _httpClient = httpClient;
        _tokenProvider = tokenProvider;
        _options = options;
        if (!string.Equals(options.StoreName, Demo4MemoryContract.StoreName, StringComparison.Ordinal) ||
            !string.Equals(options.Scope, Demo4MemoryContract.Scope, StringComparison.Ordinal) ||
            !Uri.TryCreate(options.ProjectEndpoint, UriKind.Absolute, out Uri? projectEndpoint) ||
            projectEndpoint.Scheme != Uri.UriSchemeHttps ||
            !projectEndpoint.IdnHost.EndsWith(
                ".services.ai.azure.com",
                StringComparison.OrdinalIgnoreCase) ||
            !projectEndpoint.AbsolutePath.StartsWith("/api/projects/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(projectEndpoint.Query) ||
            !string.IsNullOrEmpty(projectEndpoint.Fragment))
        {
            throw new InvalidOperationException(
                "The Memory options must use the fixed store, scope, and a valid Foundry project endpoint.");
        }

        _itemsEndpoint = new Uri(
            $"{projectEndpoint.AbsoluteUri.TrimEnd('/')}/memory_stores/{Uri.EscapeDataString(options.StoreName)}/items?api-version={ApiVersion}");
    }

    public async Task CreateAndVerifyAsync(
        NotebookRecordEnvelope record,
        string serializedRecord,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedRecord);
        string canonical = NotebookRecordCodec.Serialize(record);
        if (!Encoding.UTF8.GetBytes(canonical).SequenceEqual(Encoding.UTF8.GetBytes(serializedRecord)))
        {
            throw new MemoryWriteIntegrityException(
                "The supplied Memory JSON was not the exact canonical record.");
        }

        await using (await _locks.AcquireAsync(record.RecordId, cancellationToken)
            .ConfigureAwait(false))
        {
            IReadOnlyList<MemoryItemResponse> existing = await FindExactAsync(
                record.RecordId,
                serializedRecord,
                cancellationToken).ConfigureAwait(false);
            if (existing.Count == 1)
            {
                return;
            }

            if (existing.Count > 1)
            {
                throw new MemoryWriteIntegrityException(
                    "Memory contains duplicate records for one application record identifier.");
            }

            MemoryItemResponse? created = null;
            try
            {
                created = await CreateAsync(serializedRecord, cancellationToken).ConfigureAwait(false);
                ValidateExact(created, serializedRecord);
                MemoryItemResponse readBack = await GetAsync(
                    created.MemoryId,
                    cancellationToken).ConfigureAwait(false);
                ValidateExact(readBack, serializedRecord);
            }
            catch (OperationCanceledException cancellation)
            {
                await RecoverCanceledCreateAsync(
                    record.RecordId,
                    serializedRecord,
                    created,
                    cancellation).ConfigureAwait(false);
                throw;
            }
            catch (Exception exception) when (created is null)
            {
                try
                {
                    using CancellationTokenSource reconciliation = new(CleanupTimeout);
                    IReadOnlyList<MemoryItemResponse> reconciled = await FindExactAsync(
                        record.RecordId,
                        serializedRecord,
                        reconciliation.Token).ConfigureAwait(false);
                    if (reconciled.Count == 1)
                    {
                        return;
                    }
                }
                catch (Exception reconciliationFailure)
                {
                    throw new MemoryWriteIndeterminateException(
                        "Memory create failed and could not be reconciled safely.",
                        new AggregateException(exception, reconciliationFailure));
                }

                throw new MemoryWriteIndeterminateException(
                    "Memory create failed and could not be reconciled safely.",
                    exception);
            }
            catch (Exception exception)
            {
                await DeleteAndVerifyAsync(created!.MemoryId).ConfigureAwait(false);
                throw new MemoryWriteCompensatedException(
                    "Memory verification failed. The application removed and verified removal of the new item.",
                    exception);
            }
        }
    }

    private async Task RecoverCanceledCreateAsync(
        string recordId,
        string serializedRecord,
        MemoryItemResponse? created,
        OperationCanceledException cancellation)
    {
        using CancellationTokenSource cleanup = new(CleanupTimeout);
        try
        {
            if (created is not null)
            {
                ValidateExact(created, serializedRecord);
                await DeleteAndVerifyAsync(created.MemoryId, cleanup.Token).ConfigureAwait(false);
                return;
            }

            IReadOnlyList<MemoryItemResponse> reconciled = await FindExactAsync(
                recordId,
                serializedRecord,
                cleanup.Token).ConfigureAwait(false);
            foreach (MemoryItemResponse item in reconciled)
            {
                await DeleteAndVerifyAsync(item.MemoryId, cleanup.Token).ConfigureAwait(false);
            }
        }
        catch (Exception cleanupFailure)
        {
            throw new MemoryWriteIndeterminateException(
                "Memory cancellation cleanup could not determine or restore the write state.",
                new AggregateException(cancellation, cleanupFailure));
        }
    }

    private async Task<MemoryItemResponse> CreateAsync(
        string serializedRecord,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendAsync(
            HttpMethod.Post,
            _itemsEndpoint,
            new CreateMemoryItemRequest(_options.Scope, serializedRecord, ItemKind),
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(response, "create");
        return await ReadItemAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<MemoryItemResponse> GetAsync(
        string memoryId,
        CancellationToken cancellationToken)
    {
        ValidateIdentifier(memoryId);
        for (int attempt = 1; attempt <= MaximumReadAfterWriteAttempts; attempt++)
        {
            using HttpResponseMessage response = await SendAsync(
                HttpMethod.Get,
                ItemEndpoint(memoryId),
                null,
                cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound &&
                attempt < MaximumReadAfterWriteAttempts)
            {
                await Task.Delay(ReadAfterWriteDelay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            EnsureSuccess(response, "read");
            return await ReadItemAsync(response, cancellationToken).ConfigureAwait(false);
        }

        throw new UnreachableException();
    }

    private async Task<IReadOnlyList<MemoryItemResponse>> FindExactAsync(
        string recordId,
        string serializedRecord,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MemoryItemResponse> items = await ListScopeItemsAsync(
            "reconcile",
            ReconciliationFailures,
            cancellationToken).ConfigureAwait(false);
        List<MemoryItemResponse> matches = [];
        foreach (MemoryItemResponse item in items)
        {
            if (!string.Equals(item.Scope, _options.Scope, StringComparison.Ordinal) ||
                !string.Equals(item.Kind, ItemKind, StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                NotebookRecordEnvelope candidate = NotebookRecordCodec.DeserializeAndValidate(
                    item.Content,
                    DateTimeOffset.UtcNow,
                    Demo4MemoryContract.RequiredTtlSeconds);
                if (candidate.RecordId == recordId)
                {
                    ValidateExact(item, serializedRecord);
                    matches.Add(item);
                }
            }
            catch (UnsafeMemoryReferenceException)
            {
                // Unrelated untrusted records do not participate in idempotent reconciliation.
            }
        }

        return matches;
    }

    public async Task<MemoryNotebookListing> ListValidRecordsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<MemoryItemResponse> items = await ListScopeItemsAsync(
            "list",
            ListingFailures,
            cancellationToken).ConfigureAwait(false);
        List<MemoryNotebookEntry> entries = [];
        HashSet<string> recordIds = new(StringComparer.Ordinal);
        int rejected = 0;
        foreach (MemoryItemResponse item in items)
        {
            if (!string.Equals(item.Scope, _options.Scope, StringComparison.Ordinal) ||
                !string.Equals(item.Kind, ItemKind, StringComparison.Ordinal) ||
                item.UpdatedAt < now.AddSeconds(-Demo4MemoryContract.RequiredTtlSeconds) ||
                item.UpdatedAt > now.AddMinutes(1))
            {
                rejected++;
                continue;
            }

            NotebookRecordEnvelope record;
            try
            {
                record = NotebookRecordCodec.DeserializeAndValidate(
                    item.Content,
                    now,
                    Demo4MemoryContract.RequiredTtlSeconds);
            }
            catch (UnsafeMemoryReferenceException)
            {
                rejected++;
                continue;
            }

            if (!recordIds.Add(record.RecordId))
            {
                rejected++;
                continue;
            }

            entries.Add(new(item.MemoryId, item.UpdatedAt, record));
        }

        return new(
            entries
                .OrderByDescending(entry => entry.UpdatedAt)
                .ThenBy(entry => entry.Record.RecordId, StringComparer.Ordinal)
                .ToArray(),
            items.Count,
            rejected);
    }

    private async Task<IReadOnlyList<MemoryItemResponse>> ListScopeItemsAsync(
        string operation,
        PageFailures failures,
        CancellationToken cancellationToken)
    {
        List<MemoryItemResponse> items = [];
        HashSet<string> itemIds = new(StringComparer.Ordinal);
        HashSet<string> cursors = new(StringComparer.Ordinal);
        string? after = null;
        for (int pageNumber = 1; pageNumber <= MaximumReconciliationPages; pageNumber++)
        {
            using HttpResponseMessage response = await SendAsync(
                HttpMethod.Post,
                ListEndpoint(after),
                new ListMemoryItemsRequest(_options.Scope),
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw failures.Transport(
                    $"Memory item {operation} returned HTTP {(int)response.StatusCode}.");
            }

            ListMemoryItemsResponse list = await ReadListAsync(
                response,
                cancellationToken).ConfigureAwait(false);
            if (items.Count + list.Data.Count > MaximumReconciliationRecords)
            {
                throw failures.Integrity(
                    "Memory paging exceeded its bounded record limit.");
            }

            foreach (MemoryItemResponse item in list.Data)
            {
                ValidateIdentifier(item.MemoryId);
                if (!itemIds.Add(item.MemoryId))
                {
                    throw failures.Integrity("Memory paging returned a duplicate item.");
                }

                items.Add(item);
            }

            if (!list.HasMore)
            {
                return items;
            }

            if (string.IsNullOrWhiteSpace(list.LastId))
            {
                throw failures.Integrity(
                    "Memory paging returned an invalid pagination cursor.");
            }

            ValidateIdentifier(list.LastId);
            if (!cursors.Add(list.LastId))
            {
                throw failures.Integrity("Memory paging returned a pagination cycle.");
            }

            after = list.LastId;
        }

        throw failures.Integrity("Memory paging exceeded its bounded page limit.");
    }

    private async Task DeleteAndVerifyAsync(string memoryId)
    {
        using CancellationTokenSource cleanup = new(CleanupTimeout);
        await DeleteAndVerifyAsync(memoryId, cleanup.Token).ConfigureAwait(false);
    }

    private async Task DeleteAndVerifyAsync(
        string memoryId,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage delete = await SendAsync(
            HttpMethod.Delete,
            ItemEndpoint(memoryId),
            null,
            cancellationToken).ConfigureAwait(false);
        if (delete.StatusCode != HttpStatusCode.NotFound)
        {
            EnsureSuccess(delete, "delete compensation");
        }

        using HttpResponseMessage read = await SendAsync(
            HttpMethod.Get,
            ItemEndpoint(memoryId),
            null,
            cancellationToken).ConfigureAwait(false);
        if (read.StatusCode != HttpStatusCode.NotFound)
        {
            throw new MemoryWriteIndeterminateException(
                "Memory still returned an item after compensation.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        Uri endpoint,
        object? body,
        CancellationToken cancellationToken)
    {
        AccessToken token = await _tokenProvider.GetTokenAsync(cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token.Token) || token.ExpiresOn <= DateTimeOffset.UtcNow)
        {
            throw new InvalidOperationException("The Foundry access token was empty or expired.");
        }

        using HttpRequestMessage request = new(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("Foundry-Features", PreviewFeature);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, SerializerOptions),
                Encoding.UTF8,
                "application/json");
        }

        return await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<MemoryItemResponse> ReadItemAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        MemoryItemWireResponse wire = await ReadBoundedAsync<MemoryItemWireResponse>(
            response,
            cancellationToken).ConfigureAwait(false);
        return wire.ToDomain();
    }

    private static async Task<ListMemoryItemsResponse> ReadListAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        ListMemoryItemsWireResponse wire = await ReadBoundedAsync<ListMemoryItemsWireResponse>(
            response,
            cancellationToken).ConfigureAwait(false);
        return new(
            wire.Data?.Select(item => item.ToDomain()).ToArray() ?? [],
            wire.LastId,
            wire.HasMore);
    }

    private static async Task<T> ReadBoundedAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
        {
            throw new MemoryWriteIntegrityException("Memory returned an oversized response.");
        }

        byte[] payload = await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
        if (payload.Length > MaximumResponseBytes)
        {
            throw new MemoryWriteIntegrityException("Memory returned an oversized response.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payload, SerializerOptions)
                ?? throw new MemoryWriteIntegrityException("Memory returned an empty response.");
        }
        catch (JsonException exception)
        {
            throw new MemoryWriteIntegrityException(
                "Memory returned an invalid response.",
                exception);
        }
    }

    private static void ValidateExact(MemoryItemResponse item, string expectedContent)
    {
        ValidateIdentifier(item.MemoryId);
        if (item.Scope != Demo4MemoryContract.Scope ||
            item.Kind != ItemKind ||
            item.Content != expectedContent)
        {
            throw new MemoryWriteIntegrityException(
                "Memory returned an item that did not match the canonical write.");
        }
    }

    private static void ValidateIdentifier(string? memoryId)
    {
        if (string.IsNullOrWhiteSpace(memoryId) ||
            memoryId.Length > 128 ||
            memoryId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not '-' and not '_' and not '.' and not ':'))
        {
            throw new MemoryWriteIntegrityException("Memory returned an invalid item identifier.");
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, string operation)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Memory item {operation} returned HTTP {(int)response.StatusCode}.");
        }
    }

    private Uri ItemEndpoint(string memoryId) =>
        new($"{_itemsEndpoint.GetLeftPart(UriPartial.Path).TrimEnd('/')}/{Uri.EscapeDataString(memoryId)}?api-version={ApiVersion}");

    private Uri ListEndpoint(string? after)
    {
        string afterQuery = after is null
            ? string.Empty
            : $"&after={Uri.EscapeDataString(after)}";
        return new(
            $"{_itemsEndpoint.GetLeftPart(UriPartial.Path)}:list?limit=32&order=desc{afterQuery}&api-version={ApiVersion}");
    }

    private sealed record CreateMemoryItemRequest(
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("kind")] string Kind);

    private sealed record ListMemoryItemsRequest(
        [property: JsonPropertyName("scope")] string Scope);

    private sealed record MemoryItemWireResponse(
        [property: JsonPropertyName("memory_id")] string MemoryId,
        [property: JsonPropertyName("updated_at")] long UpdatedAtUnixSeconds,
        [property: JsonPropertyName("scope")] string Scope,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("kind")] string Kind)
    {
        public MemoryItemResponse ToDomain() => new(
            MemoryId,
            DateTimeOffset.FromUnixTimeSeconds(UpdatedAtUnixSeconds),
            Scope,
            Content,
            Kind);
    }

    private sealed record ListMemoryItemsWireResponse(
        [property: JsonPropertyName("object")] string? Object,
        [property: JsonPropertyName("data")] IReadOnlyList<MemoryItemWireResponse>? Data,
        [property: JsonPropertyName("first_id")] string? FirstId,
        [property: JsonPropertyName("last_id")] string? LastId,
        [property: JsonPropertyName("has_more")] bool HasMore);

    private sealed record ListMemoryItemsResponse(
        IReadOnlyList<MemoryItemResponse> Data,
        string? LastId,
        bool HasMore);

    private sealed record MemoryItemResponse(
        string MemoryId,
        DateTimeOffset UpdatedAt,
        string Scope,
        string Content,
        string Kind);

    private sealed record PageFailures(
        Func<string, Exception> Transport,
        Func<string, Exception> Integrity);
}

public sealed class MemoryNotebookReadException : InvalidOperationException
{
    public MemoryNotebookReadException(string message)
        : base(message)
    {
    }

    public MemoryNotebookReadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class MemoryWriteIntegrityException : InvalidOperationException
{
    public MemoryWriteIntegrityException(string message)
        : base(message)
    {
    }

    public MemoryWriteIntegrityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class MemoryWriteCompensatedException : MemoryWriteIntegrityException
{
    public MemoryWriteCompensatedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class MemoryWriteIndeterminateException : InvalidOperationException
{
    public MemoryWriteIndeterminateException(string message)
        : base(message)
    {
    }

    public MemoryWriteIndeterminateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
