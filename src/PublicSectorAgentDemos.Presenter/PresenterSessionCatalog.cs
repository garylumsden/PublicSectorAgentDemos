using System.Text.Json;
using System.Text.Json.Serialization;

namespace PublicSectorAgentDemos.Presenter;

public sealed record PresenterSessionCatalog(
    int Version,
    IReadOnlyList<PresenterSession> Sessions,
    IReadOnlyList<PresenterSession>? Extras = null)
{
    [JsonIgnore]
    public IEnumerable<PresenterSession> AllSessions => Sessions.Concat(Extras ?? []);

    private static readonly string[] RequiredOrder =
    [
        "Foundation",
        "Ground",
        "Act",
        "Cross-Government Coordinate",
        "Patriots Coordinate",
        "Hosted"
    ];

    public static PresenterSessionCatalog Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        PresenterSessionCatalog catalog =
            JsonSerializer.Deserialize(stream, PresenterJsonContext.Default.PresenterSessionCatalog)
            ?? throw new InvalidDataException("The presenter session configuration is empty.");
        catalog.Validate();
        return catalog;
    }

    public void Validate()
    {
        if (Version != 1 || Sessions.Count != RequiredOrder.Length)
        {
            throw new InvalidDataException("The presenter session configuration version or count is invalid.");
        }

        string[] titles = Sessions.Select(session => session.Title).ToArray();
        if (!titles.SequenceEqual(RequiredOrder, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The presenter session order is invalid.");
        }

        if (!Sessions[^1].Optional || !Sessions[4].Optional ||
            Sessions.Take(4).Any(session => session.Optional))
        {
            throw new InvalidDataException("Only Patriots and Hosted can be optional sessions.");
        }

        HashSet<string> identifiers = new(StringComparer.Ordinal);
        if ((Extras ?? []).Any(session => session.Id != "tokens-and-credits" || !session.Optional) ||
            Sessions.Any(session => session.Id == "tokens-and-credits"))
        {
            throw new InvalidDataException("Tokens and Credits must be an optional extra, not a main session.");
        }
        foreach (PresenterSession session in AllSessions)
        {
            session.Validate();
            if (!identifiers.Add(session.Id))
            {
                throw new InvalidDataException("Presenter session identifiers must be unique.");
            }
        }
    }
}

public sealed record PresenterSession(
    string Id,
    string Title,
    string Proof,
    string LaunchUrl,
    string Input,
    string TabNote,
    string SavedResultFallback,
    bool Optional,
    PresenterWarmUpTarget WarmUp,
    string LaunchLabel = "Launch session",
    IReadOnlyList<PresenterSessionLink>? Links = null,
    bool Configured = true,
    string? StartupStatus = null)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) ||
            string.IsNullOrWhiteSpace(Title) ||
            string.IsNullOrWhiteSpace(Proof) ||
            Proof.Contains('\n', StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(Input) ||
            string.IsNullOrWhiteSpace(TabNote) ||
            string.IsNullOrWhiteSpace(SavedResultFallback) ||
            string.IsNullOrWhiteSpace(LaunchLabel) ||
            (Configured && (!Uri.TryCreate(LaunchUrl, UriKind.Absolute, out Uri? launchUri) ||
                !IsSafeLaunchUri(launchUri) || WarmUp.Kind == "disabled")) ||
            (!Configured && (Id is not ("patriots-coordinate" or "tokens-and-credits") || !Optional ||
                !string.IsNullOrEmpty(LaunchUrl) || WarmUp.Kind != "disabled")) ||
            (Id == "tokens-and-credits" &&
                (!Optional || StartupStatus is not ("ready" or "failed" or "not-configured") ||
                 Configured != (StartupStatus == "ready") ||
                 (Configured && LaunchUrl != "http://localhost:5041/"))) ||
            (Id != "tokens-and-credits" && StartupStatus is not null))
        {
            throw new InvalidDataException($"Presenter session '{Id}' is invalid.");
        }

        WarmUp.Validate(Id);
        foreach (PresenterSessionLink link in Links ?? [])
        {
            link.Validate(Id);
        }
    }

    private static bool IsSafeLaunchUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps ||
        (uri.Scheme == Uri.UriSchemeHttp &&
         string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
}

// A small, optional collection of auxiliary links on a session (for example, the Hosted
// session's agent playground and portable VS Code source links). Kind selects the validation
// rule: "playground" requires an HTTPS ai.azure.com URL, "vscode" requires a
// vscode://file/<absolute-drive-path> URI, and any other kind requires a plain HTTPS URL.
// This keeps PresenterPage rendering generic instead of adding session-specific HTML.
public sealed partial record PresenterSessionLink(
    string Label,
    string Url,
    string Kind)
{
    public void Validate(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(Label) ||
            string.IsNullOrWhiteSpace(Kind) ||
            !Uri.TryCreate(Url, UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidDataException($"Presenter session '{sessionId}' has an invalid link.");
        }

        bool valid = Kind switch
        {
            "vscode" => IsSafeVsCodeUri(uri),
            "playground" => IsSafePlaygroundUri(uri),
            _ => IsSafeHttpsUri(uri)
        };
        if (!valid)
        {
            throw new InvalidDataException($"Presenter session '{sessionId}' link '{Label}' is invalid.");
        }
    }

    private static bool IsSafeHttpsUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo);

    private static bool IsSafePlaygroundUri(Uri uri) =>
        IsSafeHttpsUri(uri) &&
        string.Equals(uri.Host, "ai.azure.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsSafeVsCodeUri(Uri uri) =>
        uri.Scheme == "vscode" &&
        string.Equals(uri.Host, "file", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) &&
        WindowsDrivePathPattern().IsMatch(uri.AbsolutePath);

    [System.Text.RegularExpressions.GeneratedRegex(@"^/[A-Za-z]:/")]
    private static partial System.Text.RegularExpressions.Regex WindowsDrivePathPattern();
}

public sealed record PresenterWarmUpTarget(
    string Kind,
    string Endpoint,
    string? ModelDeployment)
{
    public void Validate(string sessionId)
    {
        if (Kind == "disabled" && sessionId is "patriots-coordinate" or "tokens-and-credits" &&
            string.IsNullOrEmpty(Endpoint) && string.IsNullOrEmpty(ModelDeployment))
        {
            return;
        }

        if (sessionId == "tokens-and-credits")
        {
            if (Kind == "tokens-local" && Endpoint == "http://localhost:5041/api/embeddings/manifest" &&
                string.IsNullOrEmpty(ModelDeployment))
            {
                return;
            }
            throw new InvalidDataException("Tokens and Credits warm-up must use its local embedding manifest.");
        }

        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpoint) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidDataException($"The warm-up endpoint for '{sessionId}' is invalid.");
        }

        switch (Kind)
        {
            case "foundry" when IsFoundryEndpoint(endpoint) &&
                                !string.IsNullOrWhiteSpace(ModelDeployment):
                return;
            case "local-app" when IsAppEndpoint(endpoint) &&
                                  endpoint.AbsolutePath is "/" or "/health":
                return;
            case "demo4" when IsAppEndpoint(endpoint) &&
                              endpoint.AbsolutePath is "/" or "/health" or "/warm":
                return;
            default:
                throw new InvalidDataException($"The warm-up target for '{sessionId}' is invalid.");
        }
    }

    private static bool IsFoundryEndpoint(Uri endpoint) =>
        endpoint.Scheme == Uri.UriSchemeHttps &&
        endpoint.Port == 443 &&
        (endpoint.IdnHost.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) ||
         endpoint.IdnHost.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)) &&
        endpoint.AbsolutePath == "/openai/v1/responses" &&
        string.IsNullOrEmpty(endpoint.Query);

    private static bool IsAppEndpoint(Uri endpoint) =>
        IsLocalHttp(endpoint) ||
        (endpoint.Scheme == Uri.UriSchemeHttps && endpoint.Port == 443);

    private static bool IsLocalHttp(Uri endpoint) =>
        endpoint.Scheme == Uri.UriSchemeHttp &&
        string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PresenterSessionCatalog))]
internal sealed partial class PresenterJsonContext : JsonSerializerContext;
