using System.Collections.Concurrent;
using Demo2.Web.Configuration;
using Demo2.Web.Domain;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

namespace Demo2.Web.Persistence;

public interface IWelfareCaseStore
{
    Task<WelfareCaseRecord?> GetAsync(
        string caseId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WelfareCaseRecord>> ListAsync(
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        WelfareCaseRecord record,
        CancellationToken cancellationToken = default);
}

public interface IConversationStore
{
    Task<ConversationRecord?> GetAsync(
        string caseId,
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        ConversationRecord record,
        CancellationToken cancellationToken = default);
}

public interface IApprovalStore
{
    Task<WelfareApprovalRecord?> GetAsync(
        string caseId,
        string requestId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WelfareApprovalRecord>> ListForCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WelfareApprovalRecord>> ListPendingAsync(
        CancellationToken cancellationToken = default);

    Task UpsertAsync(
        WelfareApprovalRecord record,
        CancellationToken cancellationToken = default);
}

public interface IInspectorDecisionStore
{
    Task<IReadOnlyList<InspectorDecisionRecord>> ListDecisionsForCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        InspectorDecisionRecord record,
        CancellationToken cancellationToken = default);
}

public sealed class InMemoryDemo2StateStore :
    IWelfareCaseStore,
    IConversationStore,
    IApprovalStore,
    IInspectorDecisionStore
{
    private readonly ConcurrentDictionary<string, WelfareCaseRecord> _cases =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConversationRecord> _conversations =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, WelfareApprovalRecord> _approvals =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, InspectorDecisionRecord> _decisions =
        new(StringComparer.Ordinal);

    public Task<WelfareCaseRecord?> GetAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cases.TryGetValue(caseId, out WelfareCaseRecord? record);
        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<WelfareCaseRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<WelfareCaseRecord> records = _cases.Values
            .OrderByDescending(record => record.UpdatedAt)
            .ToArray();
        return Task.FromResult(records);
    }

    public Task UpsertAsync(
        WelfareCaseRecord record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cases[record.CaseId] = record;
        return Task.CompletedTask;
    }

    Task<ConversationRecord?> IConversationStore.GetAsync(
        string caseId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _conversations.TryGetValue(caseId, out ConversationRecord? record);
        return Task.FromResult(record);
    }

    public Task UpsertAsync(
        ConversationRecord record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _conversations[record.CaseId] = record;
        return Task.CompletedTask;
    }

    public Task<WelfareApprovalRecord?> GetAsync(
        string caseId,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _approvals.TryGetValue(ApprovalKey(caseId, requestId), out WelfareApprovalRecord? record);
        return Task.FromResult(record);
    }

    public Task<IReadOnlyList<WelfareApprovalRecord>> ListForCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<WelfareApprovalRecord> records = _approvals.Values
            .Where(record => string.Equals(record.CaseId, caseId, StringComparison.Ordinal))
            .OrderByDescending(record => record.UpdatedAt)
            .ToArray();
        return Task.FromResult(records);
    }

    public Task<IReadOnlyList<WelfareApprovalRecord>> ListPendingAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<WelfareApprovalRecord> records = _approvals.Values
            .Where(record => record.IsPending)
            .OrderBy(record => record.Request.RequestedAt)
            .ToArray();
        return Task.FromResult(records);
    }

    public Task UpsertAsync(
        WelfareApprovalRecord record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _approvals[ApprovalKey(record.CaseId, record.Request.RequestId)] = record;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<InspectorDecisionRecord>> ListDecisionsForCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<InspectorDecisionRecord> records = _decisions.Values
            .Where(record => string.Equals(record.CaseId, caseId, StringComparison.Ordinal))
            .OrderByDescending(record => record.DecidedAt)
            .ToArray();
        return Task.FromResult(records);
    }

    public Task AddAsync(
        InspectorDecisionRecord record,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_decisions.TryAdd(record.DecisionId, record))
        {
            throw new InvalidOperationException("Operator decision ID already exists.");
        }

        return Task.CompletedTask;
    }

    private static string ApprovalKey(string caseId, string requestId) => $"{caseId}:{requestId}";
}

public sealed class CosmosDemo2StateStore :
    IWelfareCaseStore,
    IConversationStore,
    IApprovalStore,
    IInspectorDecisionStore
{
    private const string CaseDocumentType = "case";
    private const string ConversationDocumentType = "conversation";
    private const string ApprovalDocumentType = "approval";
    private const string InspectorDecisionDocumentType = "inspector-decision";
    private readonly Container _container;

    public CosmosDemo2StateStore(
        CosmosClient cosmosClient,
        IOptions<Demo2Options> options)
    {
        Demo2PersistenceOptions persistence = options.Value.Persistence;
        _container = cosmosClient.GetContainer(
            persistence.DatabaseName,
            persistence.ContainerName);
    }

    public async Task<WelfareCaseRecord?> GetAsync(
        string caseId,
        CancellationToken cancellationToken = default)
    {
        CosmosDocument<WelfareCaseRecord>? document = await ReadAsync<WelfareCaseRecord>(
            CaseId(caseId),
            caseId,
            cancellationToken);
        return document?.Payload;
    }

    public async Task<IReadOnlyList<WelfareCaseRecord>> ListAsync(
        CancellationToken cancellationToken = default) =>
        (await QueryByTypeAsync<WelfareCaseRecord>(
            CaseDocumentType,
            cancellationToken))
        .Select(document => document.Payload)
        .OrderByDescending(record => record.UpdatedAt)
        .ToArray();

    public Task UpsertAsync(
        WelfareCaseRecord record,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(
            new CosmosDocument<WelfareCaseRecord>(
                CaseId(record.CaseId),
                record.CaseId,
                CaseDocumentType,
                record,
                record.UpdatedAt),
            cancellationToken);

    async Task<ConversationRecord?> IConversationStore.GetAsync(
        string caseId,
        CancellationToken cancellationToken)
    {
        CosmosDocument<ConversationRecord>? document = await ReadAsync<ConversationRecord>(
            ConversationId(caseId),
            caseId,
            cancellationToken);
        return document?.Payload;
    }

    public Task UpsertAsync(
        ConversationRecord record,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(
            new CosmosDocument<ConversationRecord>(
                ConversationId(record.CaseId),
                record.CaseId,
                ConversationDocumentType,
                record,
                record.UpdatedAt),
            cancellationToken);

    public async Task<WelfareApprovalRecord?> GetAsync(
        string caseId,
        string requestId,
        CancellationToken cancellationToken = default)
    {
        CosmosDocument<WelfareApprovalRecord>? document = await ReadAsync<WelfareApprovalRecord>(
            ApprovalId(requestId),
            caseId,
            cancellationToken);
        return document?.Payload;
    }

    public async Task<IReadOnlyList<WelfareApprovalRecord>> ListForCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default) =>
        (await QueryByTypeAsync<WelfareApprovalRecord>(
            ApprovalDocumentType,
            cancellationToken,
            caseId))
        .Select(document => document.Payload)
        .OrderByDescending(record => record.UpdatedAt)
        .ToArray();

    public async Task<IReadOnlyList<WelfareApprovalRecord>> ListPendingAsync(
        CancellationToken cancellationToken = default) =>
        (await QueryByTypeAsync<WelfareApprovalRecord>(
            ApprovalDocumentType,
            cancellationToken))
        .Select(document => document.Payload)
        .Where(record => record.IsPending)
        .OrderBy(record => record.Request.RequestedAt)
        .ToArray();

    public Task UpsertAsync(
        WelfareApprovalRecord record,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(
            new CosmosDocument<WelfareApprovalRecord>(
                ApprovalId(record.Request.RequestId),
                record.CaseId,
                ApprovalDocumentType,
                record,
                record.UpdatedAt),
            cancellationToken);

    public async Task<IReadOnlyList<InspectorDecisionRecord>> ListDecisionsForCaseAsync(
        string caseId,
        CancellationToken cancellationToken = default) =>
        (await QueryByTypeAsync<InspectorDecisionRecord>(
            InspectorDecisionDocumentType,
            cancellationToken,
            caseId))
        .Select(document => document.Payload)
        .OrderByDescending(record => record.DecidedAt)
        .ToArray();

    public Task AddAsync(
        InspectorDecisionRecord record,
        CancellationToken cancellationToken = default) =>
        UpsertAsync(
            new CosmosDocument<InspectorDecisionRecord>(
                InspectorDecisionId(record.DecisionId),
                record.CaseId,
                InspectorDecisionDocumentType,
                record,
                record.DecidedAt),
            cancellationToken);

    private async Task<CosmosDocument<T>?> ReadAsync<T>(
        string id,
        string caseId,
        CancellationToken cancellationToken)
    {
        try
        {
            ItemResponse<CosmosDocument<T>> response =
                await _container.ReadItemAsync<CosmosDocument<T>>(
                    id,
                    new PartitionKey(caseId),
                    cancellationToken: cancellationToken);
            return response.Resource;
        }
        catch (CosmosException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<CosmosDocument<T>>> QueryByTypeAsync<T>(
        string documentType,
        CancellationToken cancellationToken,
        string? caseId = null)
    {
        QueryDefinition query = new QueryDefinition(
                "SELECT * FROM c WHERE c.documentType = @documentType")
            .WithParameter("@documentType", documentType);
        QueryRequestOptions requestOptions = new();
        if (caseId is not null)
        {
            query = new QueryDefinition(
                    "SELECT * FROM c WHERE c.documentType = @documentType AND c.caseId = @caseId")
                .WithParameter("@documentType", documentType)
                .WithParameter("@caseId", caseId);
            requestOptions.PartitionKey = new PartitionKey(caseId);
        }

        using FeedIterator<CosmosDocument<T>> iterator =
            _container.GetItemQueryIterator<CosmosDocument<T>>(
                query,
                requestOptions: requestOptions);
        List<CosmosDocument<T>> documents = [];
        while (iterator.HasMoreResults)
        {
            FeedResponse<CosmosDocument<T>> page = await iterator.ReadNextAsync(cancellationToken);
            documents.AddRange(page);
        }

        return documents;
    }

    private async Task UpsertAsync<T>(
        CosmosDocument<T> document,
        CancellationToken cancellationToken)
    {
        _ = await _container.UpsertItemAsync(
            document,
            new PartitionKey(document.CaseId),
            cancellationToken: cancellationToken);
    }

    private static string CaseId(string caseId) => $"case:{caseId}";

    private static string ConversationId(string caseId) => $"conversation:{caseId}";

    private static string ApprovalId(string requestId) => $"approval:{requestId}";

    private static string InspectorDecisionId(string decisionId) => $"decision:{decisionId}";

    private sealed record CosmosDocument<T>(
        string Id,
        string CaseId,
        string DocumentType,
        T Payload,
        DateTimeOffset UpdatedAt);
}
