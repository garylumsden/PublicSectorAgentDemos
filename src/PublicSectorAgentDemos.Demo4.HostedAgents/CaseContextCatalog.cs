using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PublicSectorAgentDemos.Contracts;

namespace PublicSectorAgentDemos.Demo4.HostedAgents;

public sealed record CaseContextObservation(
    string Category,
    string Note);

public sealed record CaseContextFixture(
    string ContextId,
    string Topic,
    IReadOnlyList<CaseContextObservation> Observations);

internal sealed record CaseContextDocument(
    string ContractVersion,
    string DataVersion,
    bool Preview,
    IReadOnlyList<CaseContextFixture> Contexts);

public sealed partial class CaseContextCatalog
{
    private const int MaximumCharacters = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowDuplicateProperties = false
        };
    private readonly IReadOnlyDictionary<string, CaseContextFixture> _contexts;

    public CaseContextCatalog(string path)
        : this(LoadFile(path))
    {
    }

    private CaseContextCatalog(IReadOnlyList<CaseContextFixture> contexts)
    {
        Contexts = contexts;
        _contexts = contexts.ToDictionary(item => item.ContextId, StringComparer.Ordinal);
    }

    public IReadOnlyList<CaseContextFixture> Contexts { get; }

    public CaseContextFixture GetRequired(string contextId) =>
        _contexts.TryGetValue(contextId, out CaseContextFixture? context)
            ? context
            : throw new KeyNotFoundException("The case context fixture was not found.");

    public static CaseContextCatalog FromJson(string json) =>
        new(LoadJson(json));

    private static IReadOnlyList<CaseContextFixture> LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        FileInfo file = new(Path.GetFullPath(path));
        if (!file.Exists || file.Length is <= 0 or > MaximumCharacters)
        {
            throw new InvalidOperationException(
                "The case context fixture is missing or invalid.");
        }

        return LoadJson(File.ReadAllText(file.FullName));
    }

    private static IReadOnlyList<CaseContextFixture> LoadJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        if (json.Length > MaximumCharacters)
        {
            throw new InvalidOperationException(
                "The case context fixture exceeds its size limit.");
        }

        CaseContextDocument document;
        try
        {
            document = JsonSerializer.Deserialize<CaseContextDocument>(
                json,
                SerializerOptions) ?? throw InvalidFixture();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "The case context fixture is invalid.",
                exception);
        }

        if (document.ContractVersion != "2.0.0" ||
            document.DataVersion != "v1" ||
            !document.Preview ||
            document.Contexts is not { Count: 5 })
        {
            throw InvalidFixture();
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (CaseContextFixture context in document.Contexts)
        {
            if (!ContextIdPattern().IsMatch(context.ContextId) ||
                !ids.Add(context.ContextId) ||
                !IsBoundedText(context.Topic, 120) ||
                context.Observations is not { Count: > 0 and <= 4 } ||
                context.Observations.Any(observation =>
                    !CategoryPattern().IsMatch(observation.Category) ||
                    !IsBoundedText(observation.Note, 240)))
            {
                throw InvalidFixture();
            }
        }

        return Array.AsReadOnly(document.Contexts.ToArray());
    }

    private static bool IsBoundedText(string? value, int maximumCharacters) =>
        !string.IsNullOrWhiteSpace(value) &&
        InputGuardPrimitives.HasBoundedLengthWithoutControlCharacters(
            value,
            1,
            maximumCharacters);

    private static InvalidOperationException InvalidFixture() =>
        new("The case context fixture contract is invalid.");

    [GeneratedRegex(@"\ACONTEXT-[A-Z0-9-]{3,80}\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex ContextIdPattern();

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{1,40}\z", RegexOptions.CultureInvariant, 100)]
    private static partial Regex CategoryPattern();
}
