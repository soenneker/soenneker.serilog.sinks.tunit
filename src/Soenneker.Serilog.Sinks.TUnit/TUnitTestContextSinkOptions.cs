using System;

namespace Soenneker.Serilog.Sinks.TUnit;

/// <summary>
/// Configuration for <see cref="TUnitTestContextSink"/>.
/// </summary>
public sealed class TUnitTestContextSinkOptions
{
    /// <summary>
    /// Enables live output updates through TUnit's message bus.
    /// This can significantly slow down IDE test runners when a test emits many log lines.
    /// </summary>
    public bool EnableImmediateUpdates { get; set; } = true;

    /// <summary>
    /// The minimum interval between live output updates for a single test when <see cref="EnableImmediateUpdates"/> is enabled.
    /// Error and fatal events bypass this throttle so their output is available if the test host terminates unexpectedly.
    /// </summary>
    public TimeSpan ImmediateUpdateThrottle { get; set; } = TimeSpan.FromMilliseconds(250);
}
