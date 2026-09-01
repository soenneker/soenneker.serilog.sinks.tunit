using Serilog.Core;
using Serilog.Events;
using System;
using System.Threading.Tasks;

namespace Soenneker.Serilog.Sinks.TUnit.Abstract;

/// <summary>
/// A Serilog sink that writes formatted events to the active TUnit <see cref="TestContext"/>.
/// </summary>
public interface ITUnitTestContextSink : ILogEventSink, IAsyncDisposable, IDisposable
{
    /// <summary>
    /// Writes the event to the current test's standard output. All levels use the same stream so events retain their emission order.
    /// Events are ignored when there is no active <see cref="TestContext"/>.
    /// </summary>
    /// <param name="logEvent">The event being logged</param>
    new void Emit(LogEvent logEvent);

    /// <summary>
    /// Flushes buffered test output and releases sink resources. The operation is idempotent; Serilog normally calls it during disposal.
    /// </summary>
    /// <returns>A task that completes after buffered output has been flushed.</returns>
    new ValueTask DisposeAsync();
}
