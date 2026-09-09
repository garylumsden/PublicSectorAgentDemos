using Azure;
using Defra.AgentCore;
using Defra.Bootstrap;
using System.ClientModel;

if (args.Length is < 1 or > 2 || (args.Length == 2 && args[1] != "--validate-only"))
{
    Console.Error.WriteLine("Usage: Demo2.Bootstrap <demo2-project-root> [--validate-only]");
    return 1;
}

try
{
    BootstrapConfiguration configuration = BootstrapConfiguration.FromEnvironment(args[0]);
    AgentSpecification specification = AgentCatalogLoader.Load(configuration).Single();
    if (args.Length == 2)
    {
        Console.WriteLine($"Demo2 bootstrap contract valid: {specification.Name}; hash={AgentDefinitionHasher.Compute(specification)}");
        return 0;
    }

    FoundryAgentVersionStore store = new(
        configuration.ProjectEndpoint,
        DefaultAzureCredentialFactory.Create(configuration.TenantId));
    AgentProvisioningResult result = await new AgentProvisioner(store).EnsureAsync(
        specification, CancellationToken.None);
    Console.WriteLine($"Demo2 agent {result.Action}: {result.Name}; version={result.Version}");
    return 0;
}
catch (BootstrapConfigurationException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (ClientResultException exception)
{
    Console.Error.WriteLine($"Demo2 Foundry bootstrap failed with HTTP {exception.Status}.");
    return 1;
}
catch (RequestFailedException exception)
{
    Console.Error.WriteLine($"Demo2 Azure bootstrap failed with HTTP {exception.Status}.");
    return 1;
}
catch (Azure.Identity.AuthenticationFailedException)
{
    Console.Error.WriteLine("Demo2 bootstrap authentication failed. Sign Azure CLI in to the selected tenant and principal.");
    return 1;
}
catch (System.Text.Json.JsonException)
{
    Console.Error.WriteLine("Demo2 bootstrap catalog contains invalid JSON or unsupported properties.");
    return 1;
}
catch (IOException)
{
    Console.Error.WriteLine("Demo2 bootstrap could not read the bundled catalog or prompt.");
    return 1;
}
