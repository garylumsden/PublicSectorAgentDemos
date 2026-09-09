using PublicSectorAgentDemos.Demo4.Application;

namespace PublicSectorAgentDemos.Demo4.Tests;

public sealed class ReferenceCountedKeyedLockTests
{
    [Fact]
    public async Task SameKeyIsSerializedAndRemovedAfterUse()
    {
        ReferenceCountedKeyedLock keyedLock = new();
        IAsyncDisposable first = await keyedLock.AcquireAsync(
            "same",
            CancellationToken.None);
        Task<IAsyncDisposable> secondTask = keyedLock
            .AcquireAsync("same", CancellationToken.None)
            .AsTask();

        await Task.Delay(25);
        Assert.False(secondTask.IsCompleted);

        await first.DisposeAsync();
        IAsyncDisposable second = await secondTask.WaitAsync(TimeSpan.FromSeconds(1));
        await second.DisposeAsync();

        Assert.Equal(0, keyedLock.RetainedEntryCount);
    }

    [Fact]
    public async Task DifferentKeysCanBeHeldTogether()
    {
        ReferenceCountedKeyedLock keyedLock = new();
        IAsyncDisposable first = await keyedLock.AcquireAsync(
            "first",
            CancellationToken.None);
        IAsyncDisposable second = await keyedLock.AcquireAsync(
            "second",
            CancellationToken.None);

        Assert.Equal(2, keyedLock.RetainedEntryCount);

        await second.DisposeAsync();
        await first.DisposeAsync();
        Assert.Equal(0, keyedLock.RetainedEntryCount);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotRetainAnEntry()
    {
        ReferenceCountedKeyedLock keyedLock = new();
        IAsyncDisposable holder = await keyedLock.AcquireAsync(
            "cancel",
            CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        Task<IAsyncDisposable> waiter = keyedLock
            .AcquireAsync("cancel", cancellation.Token)
            .AsTask();

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.Equal(1, keyedLock.RetainedEntryCount);

        await holder.DisposeAsync();
        Assert.Equal(0, keyedLock.RetainedEntryCount);
    }

    [Fact]
    public async Task DisposingALeaseTwiceIsSafe()
    {
        ReferenceCountedKeyedLock keyedLock = new();
        IAsyncDisposable lease = await keyedLock.AcquireAsync(
            "double-dispose",
            CancellationToken.None);

        await lease.DisposeAsync();
        await lease.DisposeAsync();

        Assert.Equal(0, keyedLock.RetainedEntryCount);
    }
}
