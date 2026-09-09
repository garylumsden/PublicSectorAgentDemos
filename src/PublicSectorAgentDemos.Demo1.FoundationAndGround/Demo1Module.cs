using Microsoft.Extensions.DependencyInjection;
using PublicSectorAgentDemos.Contracts;
using PublicSectorAgentDemos.Identity;
using PublicSectorAgentDemos.Observability;

namespace PublicSectorAgentDemos.Demo1.FoundationAndGround;

public sealed class Demo1Module(DemoDescriptor descriptor)
{
    public static Demo1Module Default { get; } = new(
        new DemoDescriptor(1, "Foundation and Ground", MaturityStage.Ground));

    public DemoDescriptor Descriptor { get; } = descriptor;

    public IServiceCollection AddTo(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services
            .AddDemoAzureIdentity()
            .AddDemoObservability(Descriptor.Name);
    }
}
