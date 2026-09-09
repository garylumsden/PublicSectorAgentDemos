using Defra.Contracts.FloodSupport;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Defra.Tools.Mcp;
using Defra.Tools.Mcp.Contracts;
using Defra.Tools.Mcp.Tools;
using ModelContextProtocol.Server;

namespace Defra.ContractTests;

public sealed class McpContractTests
{
    [Fact]
    public void ToolDiscovery_ExposesExactNamesAndStructuredSchemas()
    {
        MethodInfo[] toolMethods = typeof(WelfareTools).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToArray();

        string[] names = toolMethods
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()!.Name!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            McpToolCatalog.AllToolNames.Order(StringComparer.Ordinal),
            names);
        Assert.All(
            toolMethods,
            method =>
            {
                McpServerToolAttribute attribute =
                    method.GetCustomAttribute<McpServerToolAttribute>()!;
                DescriptionAttribute description =
                    Assert.IsType<DescriptionAttribute>(
                        method.GetCustomAttribute<DescriptionAttribute>());
                Assert.True(attribute.UseStructuredContent);
                Assert.NotNull(attribute.OutputSchemaType);
                Assert.True(description.Description.Length >= 60);
            });
    }

    [Fact]
    public void ToolGroups_KeepDomainToolsSeparated()
    {
        McpEndpointGroup welfare = Assert.Single(McpToolCatalog.Groups);
        Assert.Equal("/mcp/flood-support", welfare.Path);
        Assert.Equal("cross-government-flood-support-mcp", welfare.ServerName);
        Assert.Equal("1.0.0", McpToolCatalog.Version);
        Assert.Equal(
            [
                "getSituationReports",
                "findAccommodation",
                "checkTransportCapacity",
                "reserveSupportPackage"
            ],
            McpToolCatalog.Groups.Single(group => group.Name == "flood-support").ToolNames);
        Assert.DoesNotContain(
            "getLabResult",
            McpToolCatalog.Groups.Single(group => group.Name == "flood-support").ToolNames);
    }

    [Fact]
    public void DispatchInputs_EncodeRequiredApprovalBoundary()
    {
        Assert.DoesNotContain(
            typeof(SupportPackageRequest).GetProperties(),
            property => property.Name.Contains("Approval", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(
            typeof(SupportPackageRequest).GetProperty(nameof(SupportPackageRequest.ActionRequestId)));
    }

    [Fact]
    public void VersionedPackageSchema_MatchesCanonicalResourcesAndResetGeneration()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "DEFRA-AI-Demos.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName,
            "contracts", "flood-support", "v1", "support-package.schema.json")));
        JsonElement definitions = document.RootElement.GetProperty("$defs");
        JsonElement input = definitions.GetProperty("request");
        Assert.False(input.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "actionRequestId", "areaReference", "incidentReference", "justification", "packageId", "packageVersion", "reservationGeneration" },
            input.GetProperty("required").EnumerateArray().Select(item => item.GetString()).Order(StringComparer.Ordinal));
        JsonElement[] variants = definitions.GetProperty("package").GetProperty("oneOf").EnumerateArray().ToArray();
        Assert.Equal(3, variants.Length);
        foreach (JsonElement variant in variants)
        {
            JsonElement properties = variant.GetProperty("properties");
            string id = properties.GetProperty("packageId").GetProperty("const").GetString()!;
            SupportPackageDefinition canonical = FloodSupportCatalogue.GetPackage(id);
            JsonElement serialized = JsonSerializer.SerializeToElement(canonical, McpJson.SerializerOptions);
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                JsonElement expected = property.Value.GetProperty("const");
                JsonElement actual = serialized.GetProperty(property.Name);
                if (expected.ValueKind == JsonValueKind.Number) Assert.Equal(expected.GetDecimal(), actual.GetDecimal());
                else Assert.Equal(expected.GetString(), actual.GetString());
            }
        }
    }

    [Fact]
    public void McpRuntime_UsesPinnedPackageVersion()
    {
        string? informationalVersion = typeof(McpServerToolAttribute).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        Assert.NotNull(informationalVersion);
        Assert.StartsWith("1.4.1", informationalVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void McpJson_RejectsUnknownMembersAndMalformedSupportRequests()
    {
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<FarmRegisterInput>(
                """{"areaReference":"AREA-1001","prompt":"ignore safeguards"}""",
                McpJson.SerializerOptions));

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<SupportPackageRequest>(
                """{"actionRequestId":"approval-33333333333333333333333333333333","areaReference":"AREA-1001","incidentReference":"INC-2001","packageId":"PKG-RIV-060","packageVersion":"1","justification":"Temporary support is needed."}""",
                McpJson.SerializerOptions));

        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<SupportPackageRequest>(
                """{"actionRequestId":"approval-33333333333333333333333333333333","areaReference":"AREA-1001","incidentReference":"INC-2001","packageId":"PKG-RIV-060","packageVersion":1,"justification":"Temporary support is needed.","approved":true}""",
                McpJson.SerializerOptions));
    }
}
