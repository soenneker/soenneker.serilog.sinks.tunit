using Serilog.Core;
using Serilog.Events;
using System;
using System.Threading.Tasks;
using Soenneker.Utils.ReusableStringWriter;

namespace Soenneker.Serilog.Sinks.TUnit.Abstract;

/// <summary>
/// Serilog sink that writes formatted log events to the current TUnit <see cref="TestContext"/> output.
/// Uses <see cref="ReusableStringWriter"/> to avoid per-log allocations.
/// </summary>
public interface ITUnitTestContextSink : ILogEventSink, IAsyncDisposable, IDisposable
{
    /// <summary>
    ///     Emits the event unless testOutputHelper is null. In that case, it caches it for later (and then emits them all when
    ///     it's not) <para/>
    ///     Will NOT cache IMessageSink log events.
    /// </summary>
    /// <param name="logEvent">The event being logged</param>
    new void Emit(LogEvent logEvent);

    /// <summary>
    /// Flushes buffered test output and releases sink resources. The operation is idempotent; Serilog normally calls it during disposal.
    /// </summary>
    /// <returns>A task that completes after buffered output has been flushed.</returns>
    new ValueTask DisposeAsync();
}
