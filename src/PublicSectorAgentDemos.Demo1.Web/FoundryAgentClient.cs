using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;
using PublicSectorAgentDemos.Demo1.FoundationAndGround;

namespace PublicSectorAgentDemos.Demo1.Web;

public sealed record FoundryProjectSettings(Uri Endpoint)
{
    public static FoundryProjectSettings Parse(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint) ||
            endpoint.Scheme != "https" || !endpoint.IsDefaultPort ||
            !endpoint.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase) ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
            endpoint.AbsolutePath.TrimEnd('/').Split('/') is not ["", "api", "projects", { Length: > 0 }])
        {
            throw new InvalidOperationException(
                "AZURE_AI_FOUNDRY_ENDPOINT must be an HTTPS Foundry project endpoint: https://<resource>.services.ai.azure.com/api/projects/<project>.");
        }

        return new(new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/"));
    }
}

public interface IAgentClient
{
    public Task<AgentAnswer> InvokeAsync(string agentName, string prompt, CancellationToken cancellationToken);
}

public sealed class FoundryAgentClient(
    HttpClient httpClient,
    TokenCredential credential,
    FoundryProjectSettings settings) : IAgentClient
{
    public async Task<AgentAnswer> InvokeAsync(string agentName, string prompt, CancellationToken cancellationToken)
    {
        if (agentName is not (Demo1AgentCatalog.FoundationAgentName or Demo1AgentCatalog.GroundAgentName))
        {
            throw new ArgumentOutOfRangeException(nameof(agentName));
        }

        AccessToken token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://ai.azure.com/.default"]), cancellationToken);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(settings.Endpoint,
            $"agents/{Uri.EscapeDataString(agentName)}/endpoint/protocols/openai/responses?api-version=v1"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = JsonContent.Create(new { input = new[] { new { role = "user", content = prompt } } });
        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > DemoLimits.ResponseBytes)
        {
            throw new InvalidDataException("The agent response exceeds the size limit.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] bytes = await DemoLimits.ReadBoundedAsync(stream, DemoLimits.ResponseBytes, cancellationToken);
        string original = new UTF8Encoding(false, true).GetString(bytes);
        return AgentAnswer.Parse(original);
    }
}

public sealed record CitationDisplay(string Type, string Title, string Reference);

public sealed record AgentAnswer(
    string OriginalText,
    string OriginalResponse,
    string RenderedHtml,
    string ResponseStatus,
    IReadOnlyList<CitationDisplay> Citations)
{
    public static AgentAnswer Parse(string original)
    {
        using JsonDocument document = JsonDocument.Parse(original);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The agent response is not an object.");
        }

        List<string> texts = [];
        List<CitationDisplay> citations = [];
        if (root.TryGetProperty("output", out JsonElement output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in output.EnumerateArray())
            {
                if (ReadString(item, "type") != "message" ||
                    !item.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (JsonElement part in content.EnumerateArray())
                {
                    string type = ReadString(part, "type");
                    if (type is "output_text" or "refusal")
                    {
                        texts.Add(ReadString(part, type == "refusal" ? "refusal" : "text"));
                    }

                    if (part.ValueKind == JsonValueKind.Object && part.TryGetProperty("annotations", out JsonElement annotations) &&
                        annotations.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement annotation in annotations.EnumerateArray())
                        {
                            string annotationType = ReadString(annotation, "type");
                            if (annotationType is "url_citation" or "file_citation")
                            {
                                citations.Add(new(annotationType, ReadString(annotation, "title"),
                                    ReadString(annotation, annotationType == "url_citation" ? "url" : "file_id")));
                            }
                        }
                    }
                }
            }
        }

        string text = string.Join("\n", texts);
        return new(text, original, SafeMarkdown.Render(text), ReadString(root, "status"), citations);
    }

    private static string ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
}
