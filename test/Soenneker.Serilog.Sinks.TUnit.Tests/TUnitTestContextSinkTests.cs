using AwesomeAssertions;
using Serilog;
using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace Soenneker.Serilog.Sinks.TUnit.Tests;

public sealed class TUnitTestContextSinkTests
{
    [Test]
    public async Task Sink_should_emit_messages_every_second()
    {
        const int iterations = 5;
        var stopwatch = Stopwatch.StartNew();

        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new TUnitTestContextSink())
            .CreateLogger();

        for (var i = 1; i <= iterations; i++)
        {
            logger.Information("Heartbeat {Iteration}/{Total} at {ElapsedMs} ms", i, iterations, stopwatch.ElapsedMilliseconds);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(iterations - 1));
    }
}
