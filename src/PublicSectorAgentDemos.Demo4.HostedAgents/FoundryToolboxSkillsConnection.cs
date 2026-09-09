using System.Net.Http.Headers;
using Azure.Core;
using ModelContextProtocol.Client;

namespace PublicSectorAgentDemos.Demo4.HostedAgents;

internal sealed class FoundryToolboxSkillsConnection : IAsyncDisposable
{
    private const string FoundryScope = "https://ai.azure.com/.default";
    private static readonly string[] RequiredSkillNames =
    [
        "case-pattern-guidance",
        "case-context-guidance"
    ];
    private readonly HttpClient _httpClient;

    private FoundryToolboxSkillsConnection(McpClient client, HttpClient httpClient)
    {
        Client = client;
        _httpClient = httpClient;
    }

    public McpClient Client { get; }

    public static async Task<FoundryToolboxSkillsConnection> ConnectAsync(
        Uri projectEndpoint,
        string toolboxName,
        TokenCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectEndpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolboxName);
        ArgumentNullException.ThrowIfNull(credential);
        const string projectPathPrefix = "/api/projects/";
        if (!projectEndpoint.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            projectEndpoint.Port != 443 ||
            !projectEndpoint.IdnHost.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase) ||
            !projectEndpoint.AbsolutePath.StartsWith(projectPathPrefix, StringComparison.Ordinal) ||
            projectEndpoint.AbsolutePath.Length == projectPathPrefix.Length ||
            projectEndpoint.AbsolutePath[projectPathPrefix.Length..].Contains('/', StringComparison.Ordinal) ||
            toolboxName.Length > 63 ||
            !char.IsAsciiLetter(toolboxName[0]) ||
            toolboxName[^1] == '-' ||
            !string.IsNullOrEmpty(projectEndpoint.UserInfo) ||
            !string.IsNullOrEmpty(projectEndpoint.Query) ||
            !string.IsNullOrEmpty(projectEndpoint.Fragment) ||
            toolboxName.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new InvalidOperationException(
                "The Foundry project endpoint or Toolbox name is invalid.");
        }

        Uri endpoint = new(
            $"{projectEndpoint.AbsoluteUri.TrimEnd('/')}/toolboxes/{Uri.EscapeDataString(toolboxName)}/mcp?api-version=v1");
        HttpClient httpClient = new(new FoundryBearerTokenHandler(credential, FoundryScope)
        {
            CheckCertificateRevocationList = true
        });
        try
        {
            McpClient client = await McpClient.CreateAsync(
                new HttpClientTransport(
                    new HttpClientTransportOptions
                    {
                        Endpoint = endpoint,
                        Name = toolboxName,
                        TransportMode = HttpTransportMode.StreamableHttp
                    },
                    httpClient),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            try
            {
                var resources = await client.ListResourcesAsync(
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                foreach (string skillName in RequiredSkillNames)
                {
                    int count = resources.Count(resource =>
                        string.Equals(resource.Name, skillName, StringComparison.Ordinal) ||
                        resource.Uri.Contains(skillName, StringComparison.Ordinal));
                    if (count != 1)
                    {
                        throw new InvalidOperationException(
                            $"Toolbox discovery did not return exactly one '{skillName}' resource.");
                    }
                }

                return new(client, httpClient);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch
        {
            httpClient.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync().ConfigureAwait(false);
        _httpClient.Dispose();
    }
}

internal sealed class FoundryBearerTokenHandler(TokenCredential credential, string scope)
    : HttpClientHandler
{
    private readonly TokenRequestContext _tokenRequestContext = new([scope]);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        AccessToken token = await credential.GetTokenAsync(
            _tokenRequestContext,
            cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
