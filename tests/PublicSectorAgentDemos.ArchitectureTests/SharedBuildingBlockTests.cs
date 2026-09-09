using PublicSectorAgentDemos.Demo1.FoundationAndGround;
using PublicSectorAgentDemos.Demo4.HostedAgents;

namespace PublicSectorAgentDemos.ArchitectureTests;

public sealed class SharedBuildingBlockTests
{
    public static TheoryData<Type> DemoModuleTypes { get; } = new()
    {
        typeof(Demo1Module),
        typeof(Demo4Module)
    };

    [Theory]
    [MemberData(nameof(DemoModuleTypes))]
    public void EveryDemoReferencesIdentityAndObservability(Type demoModuleType)
    {
        string[] references = demoModuleType.Assembly
            .GetReferencedAssemblies()
            .Select(static reference => reference.Name)
            .OfType<string>()
            .ToArray();

        Assert.Contains("PublicSectorAgentDemos.Identity", references);
        Assert.Contains("PublicSectorAgentDemos.Observability", references);
    }
}
