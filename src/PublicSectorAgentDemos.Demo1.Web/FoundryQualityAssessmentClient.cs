using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Azure.Core;

namespace PublicSectorAgentDemos.Demo1.Web;

public sealed record QualityAssessmentSettings(string Model)
{
    public static QualityAssessmentSettings Parse(string? model)
    {
        model = string.IsNullOrWhiteSpace(model) ? "gpt-5-mini" : model;
        if (model.Length > 100 || model.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new InvalidOperationException("DEMO1_ASSESSMENT_MODEL contains an invalid deployment name.");
        }
        return new(model);
    }
}

public interface IQualityAssessmentClient
{
    public Task<QualityAssessment> AssessAsync(ComparisonSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed class FoundryQualityAssessmentClient(
    HttpClient httpClient,
    TokenCredential credential,
    FoundryProjectSettings project,
    QualityAssessmentSettings settings) : IQualityAssessmentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    public async Task<QualityAssessment> AssessAsync(ComparisonSnapshot snapshot, CancellationToken cancellationToken)
    {
        object input = CreateJudgeInput(snapshot);
        byte[] inputBytes = JsonSerializer.SerializeToUtf8Bytes(input, JsonOptions);
        if (inputBytes.Length > DemoLimits.AssessmentInputBytes)
        {
            throw new AssessmentInputTooLargeException();
        }

        AccessToken token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://ai.azure.com/.default"]), cancellationToken);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri(project.Endpoint, "openai/v1/responses"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Content = JsonContent.Create(new
        {
            model = settings.Model,
            instructions = QualityAssessmentRubric.Instructions,
            input = new object[]
            {
                new { role = "user", content = new[] { new { type = "input_text", text = Encoding.UTF8.GetString(inputBytes) } } }
            },
            reasoning = new { effort = "medium" },
            text = new { format = CreateSchema(snapshot.Reference.ReviewRubric.Count) },
            store = false
        });

        Stopwatch stopwatch = Stopwatch.StartNew();
        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > DemoLimits.AssessmentResponseBytes)
        {
            throw new InvalidDataException("The assessment response envelope exceeds the size limit.");
        }
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        byte[] bytes = await DemoLimits.ReadBoundedAsync(stream, DemoLimits.AssessmentResponseBytes, cancellationToken);
        string responseJson = new UTF8Encoding(false, true).GetString(bytes);
        RecordUsage(responseJson);
        string resultJson = ExtractOutputText(responseJson);
        if (Encoding.UTF8.GetByteCount(resultJson) > DemoLimits.AssessmentOutputBytes)
        {
            throw new InvalidDataException("The structured assessment output exceeds the size limit.");
        }
        JudgeAssessment result = JsonSerializer.Deserialize<JudgeAssessment>(resultJson, JsonOptions)
            ?? throw new InvalidDataException("The assessment result is empty.");
        return QualityAssessmentValidator.ValidateAndMap(result, snapshot, snapshot.Reference, settings.Model, stopwatch.ElapsedMilliseconds);
    }

    private static object CreateJudgeInput(ComparisonSnapshot snapshot)
    {
        return new
        {
            rubricVersion = QualityAssessmentRubric.Version,
            prompt = snapshot.Prompt,
            reference = new
            {
                snapshot.Reference.Basis,
                snapshot.Reference.ScenarioId,
                snapshot.Reference.ExpectedRoute,
                snapshot.Reference.ExpectedResponseKind,
                snapshot.Reference.ExpectedSignals,
                snapshot.Reference.ReviewRubric,
                foundation = snapshot.Reference.FoundationEvidence,
                ground = snapshot.Reference.GroundEvidence,
                snapshot.Reference.Limitations
            },
            foundation = snapshot.Foundation.OriginalText,
            ground = snapshot.Ground.OriginalText
        };
    }

    private static object CreateSchema(int referenceCount)
    {
        object answer = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                score = new { type = new[] { "integer", "null" } },
                explanation = new { type = "string" },
                quote = new { type = new[] { "string", "null" } }
            },
            required = new[] { "score", "explanation", "quote" }
        };
        object criterion = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                id = new { type = "string", @enum = QualityCriteria.All },
                foundation = answer,
                ground = answer
            },
            required = new[] { "id", "foundation", "ground" }
        };
        object referenceCheck = new
        {
            type = "object",
            additionalProperties = false,
            properties = new
            {
                reference_index = new { type = "integer" },
                foundation = new { type = "string", @enum = new[] { "aligned", "contradicted", "missing" } },
                ground = new { type = "string", @enum = new[] { "aligned", "contradicted", "missing" } }
            },
            required = new[] { "reference_index", "foundation", "ground" }
        };
        return new
        {
            type = "json_schema",
            name = "quality_assessment",
            strict = true,
            schema = new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    summary = new { type = "string" },
                    criteria = new { type = "array", items = criterion },
                    reference_checks = new
                    {
                        type = "array",
                        items = referenceCheck,
                        minItems = referenceCount,
                        maxItems = referenceCount
                    }
                },
                required = new[] { "summary", "criteria", "reference_checks" }
            }
        };
    }

    private static void RecordUsage(string responseJson)
    {
        using JsonDocument document = JsonDocument.Parse(responseJson);
        if (!document.RootElement.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        if (usage.TryGetProperty("input_tokens", out JsonElement input) && input.TryGetInt64(out long inputTokens))
        {
            Activity.Current?.SetTag("gen_ai.usage.input_tokens", inputTokens);
        }
        if (usage.TryGetProperty("output_tokens", out JsonElement output) && output.TryGetInt64(out long outputTokens))
        {
            Activity.Current?.SetTag("gen_ai.usage.output_tokens", outputTokens);
        }
    }

    private static string ExtractOutputText(string responseJson)
    {
        using JsonDocument document = JsonDocument.Parse(responseJson);
        if (!document.RootElement.TryGetProperty("status", out JsonElement status) ||
            status.ValueKind != JsonValueKind.String || status.GetString() != "completed")
        {
            throw new InvalidDataException("The assessment response did not complete.");
        }
        if (!document.RootElement.TryGetProperty("output", out JsonElement output) || output.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The assessment response contains no output.");
        }
        foreach (JsonElement item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.Array) continue;
            foreach (JsonElement part in content.EnumerateArray())
            {
                if (part.TryGetProperty("type", out JsonElement type) && type.GetString() == "output_text" &&
                    part.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString()!;
                }
            }
        }
        throw new InvalidDataException("The assessment response contains no structured output text.");
    }
}

public sealed class AssessmentInputTooLargeException : Exception;
