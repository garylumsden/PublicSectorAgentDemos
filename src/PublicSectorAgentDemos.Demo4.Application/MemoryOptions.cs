using PublicSectorAgentDemos.Contracts.Demo4;

namespace PublicSectorAgentDemos.Demo4.Application;

public sealed record MemoryOptions
{
    public string ProjectEndpoint { get; init; } = string.Empty;

    public string StoreName { get; init; } = Demo4MemoryContract.StoreName;

    public string Scope { get; init; } = Demo4MemoryContract.Scope;
}
