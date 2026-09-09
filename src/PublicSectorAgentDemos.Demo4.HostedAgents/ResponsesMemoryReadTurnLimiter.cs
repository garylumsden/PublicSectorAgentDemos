using System.Threading;

namespace PublicSectorAgentDemos.Demo4.HostedAgents;

public sealed class ResponsesMemoryReadTurnLimiter
{
    private readonly AsyncLocal<TurnState?> _current = new();

    public IDisposable BeginTurn()
    {
        if (_current.Value is not null)
        {
            throw new InvalidOperationException(
                "A Responses Memory-read turn is already active.");
        }

        _current.Value = new TurnState();
        return new TurnScope(this);
    }

    public bool TryAcquireRead()
    {
        TurnState state = _current.Value
            ?? throw new InvalidOperationException(
                "A Memory read requires an active Responses turn.");
        return Interlocked.CompareExchange(ref state.ReadCount, 1, 0) == 0;
    }

    private sealed class TurnState
    {
        public int ReadCount;
    }

    private sealed class TurnScope(ResponsesMemoryReadTurnLimiter owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner._current.Value = null;
            }
        }
    }
}
