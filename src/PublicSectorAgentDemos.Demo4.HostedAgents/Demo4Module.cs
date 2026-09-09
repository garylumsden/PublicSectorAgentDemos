using Microsoft.Extensions.DependencyInjection;
using PublicSectorAgentDemos.Contracts;
using PublicSectorAgentDemos.Identity;
using PublicSectorAgentDemos.Observability;

namespace PublicSectorAgentDemos.Demo4.HostedAgents;

public sealed class Demo4Module(DemoDescriptor descriptor)
{
    public static Demo4Module Default { get; } = new(
        new DemoDescriptor(4, "Hosted agents", MaturityStage.Hosted));

    public DemoDescriptor Descriptor { get; } = descriptor;

    public IServiceCollection AddTo(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services
            .AddDemoAzureIdentity()
            .AddDemoObservability(Descriptor.Name);
    }
}
