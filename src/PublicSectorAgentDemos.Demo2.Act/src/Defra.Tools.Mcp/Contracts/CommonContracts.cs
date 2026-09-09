using System.Text.Json;
using System.Text.Json.Serialization;
using Defra.Contracts.V1;

namespace Defra.Tools.Mcp.Contracts;

public sealed record ProviderResult<T>(
    T Data,
    IReadOnlyList<EvidenceReference> Evidence)
    where T : class;

public sealed record McpToolResponse<T>(
    string SchemaVersion,
    CorrelationId CorrelationId,
    bool IsSuccess,
    T? Data,
    IReadOnlyList<EvidenceReference> Evidence,
    StructuredToolError? Error,
    string DecisionBoundary)
    where T : class;

public sealed record HealthResponse(
    string Status,
    string Service,
    string Version,
    IReadOnlyList<string> McpGroups);

public static class McpJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
            {
                Modifiers =
                {
                    typeInfo =>
                    {
                        if (typeInfo.Type == typeof(Defra.Contracts.FloodSupport.SupportPackageRequest))
                        {
                            typeInfo.Properties.Single(property => property.Name == "reservationGeneration").IsRequired = true;
                        }
                    }
                }
            }
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
