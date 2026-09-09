namespace PublicSectorAgentDemos.Contracts;

public sealed record DemoDescriptor(
    int Number,
    string Name,
    MaturityStage Stage);
