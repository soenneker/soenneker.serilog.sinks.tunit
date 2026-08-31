using AwesomeAssertions;
using Serilog;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Serilog.Core;

namespace Soenneker.Serilog.Sinks.TUnit.Tests;

public sealed class TUnitTestContextSinkTests
{
    [Test]
    public async Task Sink_should_emit_messages_every_second()
    {
        const int iterations = 5;
        var stopwatch = Stopwatch.StartNew();

        await using Logger logger = new LoggerConfiguration()
                                    .MinimumLevel.Verbose()
                                    .WriteTo.Sink(new TUnitTestContextSink(new TUnitTestContextSinkOptions
                                    {
                                        EnableImmediateUpdates = true,
                                        ImmediateUpdateThrottle = TimeSpan.FromMilliseconds(250)
                                    }))
                                    .CreateLogger();

        for (var i = 1; i <= iterations; i++)
        {
            logger.Information("Heartbeat {Iteration}/{Total} at {ElapsedMs} ms", i, iterations, stopwatch.ElapsedMilliseconds);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(iterations - 1));
    }

    [Test]
    public void Sink_should_handle_many_messages()
    {
        const int iterations = 20000;

        using Logger logger = new LoggerConfiguration()
                              .MinimumLevel.Verbose()
                              .WriteTo.Sink(new TUnitTestContextSink(new TUnitTestContextSinkOptions
                              {
                                  EnableImmediateUpdates = true,
                                  ImmediateUpdateThrottle = TimeSpan.FromMilliseconds(250)
                              }))
                              .CreateLogger();

        for (var i = 1; i <= iterations; i++)
        {
            logger.Information("Bulk log {Iteration}/{Total}", i, iterations);
        }

        iterations.Should().Be(20000);
    }

    [Test]
    public void Sink_should_write_final_logs_when_test_ends_immediately()
    {
        const string marker = "FINAL_TUNIT_OUTPUT_FLUSH_MARKER";

        using Logger logger = new LoggerConfiguration()
                              .MinimumLevel.Verbose()
                              .WriteTo.Sink(new TUnitTestContextSink(new TUnitTestContextSinkOptions
                              {
                                  EnableImmediateUpdates = true,
                                  ImmediateUpdateThrottle = TimeSpan.FromMinutes(1)
                              }))
                              .CreateLogger();

        logger.Information("{Marker} first line", marker);
        logger.Information("{Marker} second throttled line", marker);

        marker.Should().Be("FINAL_TUNIT_OUTPUT_FLUSH_MARKER");
    }

    [Test]
    public void Sink_should_preserve_both_output_channels()
    {
        const string standardMarker = "STANDARD_OUTPUT_MARKER";
        const string errorMarker = "ERROR_OUTPUT_MARKER";

        using Logger logger = new LoggerConfiguration()
                              .WriteTo.Sink(new TUnitTestContextSink(new TUnitTestContextSinkOptions
                              {
                                  EnableImmediateUpdates = true,
                                  ImmediateUpdateThrottle = TimeSpan.FromMinutes(1)
                              }))
                              .CreateLogger();

        logger.Information("{Marker}", standardMarker);
        logger.Error("{Marker}", errorMarker);

        TestContext.Current!.GetStandardOutput().Should().Contain(standardMarker);
        TestContext.Current.GetErrorOutput().Should().Contain(errorMarker);
    }
}
