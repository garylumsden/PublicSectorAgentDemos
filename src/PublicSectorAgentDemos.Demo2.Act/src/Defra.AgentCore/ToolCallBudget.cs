namespace Defra.AgentCore;

public sealed class ToolCallBudget
{
    private readonly int _maximumCalls;
    private int _consumedCalls;

    public ToolCallBudget(int maximumCalls)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCalls);
        _maximumCalls = maximumCalls;
    }

    public int MaximumCalls => _maximumCalls;

    public int ConsumedCalls => Math.Min(Volatile.Read(ref _consumedCalls), _maximumCalls);

    public int RemainingCalls => _maximumCalls - ConsumedCalls;

    public bool TryConsume()
    {
        while (true)
        {
            int current = Volatile.Read(ref _consumedCalls);
            if (current >= _maximumCalls)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _consumedCalls, current + 1, current) == current)
            {
                return true;
            }
        }
    }
}
