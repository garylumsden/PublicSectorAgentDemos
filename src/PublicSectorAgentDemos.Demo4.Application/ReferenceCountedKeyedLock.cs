using System.Collections.Concurrent;

namespace PublicSectorAgentDemos.Demo4.Application;

internal sealed class ReferenceCountedKeyedLock
{
    private readonly ConcurrentDictionary<string, Entry> _entries =
        new(StringComparer.Ordinal);

    internal int RetainedEntryCount => _entries.Count;

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string key,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Entry entry;
        while (true)
        {
            entry = _entries.GetOrAdd(key, static _ => new());
            lock (entry.SyncRoot)
            {
                if (entry.Removed)
                {
                    continue;
                }

                entry.ReferenceCount++;
                break;
            }
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, key, entry);
        }
        catch
        {
            RemoveReference(key, entry, releaseSemaphore: false);
            throw;
        }
    }

    private void RemoveReference(
        string key,
        Entry entry,
        bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            entry.Semaphore.Release();
        }

        bool dispose = false;
        lock (entry.SyncRoot)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0)
            {
                entry.Removed = true;
                _entries.TryRemove(
                    new KeyValuePair<string, Entry>(key, entry));
                dispose = true;
            }
        }

        if (dispose)
        {
            entry.Semaphore.Dispose();
        }
    }

    private sealed class Entry
    {
        public object SyncRoot { get; } = new();

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }

        public bool Removed { get; set; }
    }

    private sealed class Lease(
        ReferenceCountedKeyedLock owner,
        string key,
        Entry entry)
        : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.RemoveReference(key, entry, releaseSemaphore: true);
            }

            return ValueTask.CompletedTask;
        }
    }
}
