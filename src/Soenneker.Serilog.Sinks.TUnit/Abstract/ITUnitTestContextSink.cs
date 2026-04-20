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
    /// This is idempotent... but you should avoid calling it explicitly because it'll get disposed from Serilog if it's been registered.  
    /// </summary>
    /// <returns></returns>
    new ValueTask DisposeAsync();
}
