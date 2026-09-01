using System.Threading;
using TUnit.Core;

namespace Soenneker.Serilog.Sinks.TUnit.Dtos;

internal sealed class ImmediateUpdateState(TestContext context)
{
    private long _version;
    private int _isQueued;
    private int _isPriority;
    private int _wakeRequested;

    public TestContext Context { get; } = context;

    public long LastPublishedTimestamp;

    public long Version => Volatile.Read(ref _version);

    public void MarkDirty(bool isPriority)
    {
        Interlocked.Increment(ref _version);

        if (isPriority)
            Volatile.Write(ref _isPriority, 1);
    }

    public bool TryQueue() => Interlocked.CompareExchange(ref _isQueued, 1, 0) == 0;

    public bool HasPriority => Volatile.Read(ref _isPriority) != 0;

    public bool TryRequestPriorityWake() => Interlocked.CompareExchange(ref _wakeRequested, 1, 0) == 0;

    public void ConsumePriority()
    {
        Interlocked.Exchange(ref _isPriority, 0);
        Volatile.Write(ref _wakeRequested, 0);
    }

    public bool CompletePublication(long publishedVersion, long timestamp)
    {
        Volatile.Write(ref LastPublishedTimestamp, timestamp);
        Volatile.Write(ref _isQueued, 0);

        return Version != publishedVersion;
    }
}
