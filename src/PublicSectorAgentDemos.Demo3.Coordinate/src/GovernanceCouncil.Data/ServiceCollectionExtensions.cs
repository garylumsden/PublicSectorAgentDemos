namespace GovernanceCouncil.Data;

using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Identity;
using Azure.Storage.Blobs;
using GovernanceCouncil.Core.Interfaces;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public static class ServiceCollectionExtensions
{
    private const string DatabaseName = "governance-council";

    public static IServiceCollection AddGovernanceCouncilData(
        this IServiceCollection services,
        string cosmosEndpoint,
        string storageAccountName,
        Azure.Core.TokenCredential credential)
    {
        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() },
        };

        // Lazy Cosmos client — defers connection until first use so the app starts
        // even if RBAC roles haven't propagated yet
        services.AddSingleton<CosmosClient>(sp =>
        {
            return new CosmosClient(cosmosEndpoint, credential, new CosmosClientOptions
            {
                UseSystemTextJsonSerializerWithOptions = jsonOptions,
            });
        });

        services.AddSingleton<IAssessmentStore>(sp =>
            new CosmosAssessmentStore(
                sp.GetRequiredService<CosmosClient>().GetDatabase(DatabaseName).GetContainer("assessments"),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<CosmosAssessmentStore>()));
        services.AddSingleton<INexusStore>(sp =>
            new CosmosNexusStore(
                sp.GetRequiredService<CosmosClient>().GetDatabase(DatabaseName).GetContainer("nexuses"),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<CosmosNexusStore>()));
        services.AddSingleton<IDossierStore>(sp =>
            new CosmosDossierStore(
                sp.GetRequiredService<CosmosClient>().GetDatabase(DatabaseName).GetContainer("dossiers"),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<CosmosDossierStore>()));
        services.AddSingleton<IDeliberationStore>(sp =>
            new CosmosDeliberationStore(
                sp.GetRequiredService<CosmosClient>().GetDatabase(DatabaseName).GetContainer("deliberations"),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<CosmosDeliberationStore>()));

        var blobServiceClient = new BlobServiceClient(
            new Uri($"https://{storageAccountName}.blob.core.windows.net"),
            credential);

        services.AddSingleton<IDossierBlobService>(sp =>
            new BlobDossierService(
                blobServiceClient,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<BlobDossierService>()));

        return services;
    }
}
