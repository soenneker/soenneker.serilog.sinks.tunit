using System;

namespace Soenneker.Serilog.Sinks.TUnit;

/// <summary>
/// Configuration for <see cref="TUnitTestContextSink"/>.
/// </summary>
public sealed class TUnitTestContextSinkOptions
{
    /// <summary>
    /// Enables coalesced live output updates through TUnit's message bus.
    /// Publication occurs on a background worker and does not block the logging thread.
    /// </summary>
    public bool EnableImmediateUpdates { get; set; } = true;

    /// <summary>
    /// The minimum interval between live output updates for a single test when <see cref="EnableImmediateUpdates"/> is enabled.
    /// A trailing update publishes messages received during the interval. Error and fatal events wake the publisher immediately.
    /// </summary>
    public TimeSpan ImmediateUpdateThrottle { get; set; } = TimeSpan.FromMilliseconds(250);
}
