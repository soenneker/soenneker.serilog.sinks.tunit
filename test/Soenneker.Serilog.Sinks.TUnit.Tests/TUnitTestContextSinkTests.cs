using AwesomeAssertions;
using Serilog;
using System;
using System.Diagnostics;
using System.Linq;
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
    public void Sink_should_preserve_log_order_in_one_output_channel()
    {
        const string firstMarker = "FIRST_OUTPUT_MARKER";
        const string errorMarker = "ERROR_OUTPUT_MARKER";
        const string lastMarker = "LAST_OUTPUT_MARKER";

        using Logger logger = new LoggerConfiguration()
                              .WriteTo.Sink(new TUnitTestContextSink(new TUnitTestContextSinkOptions
                              {
                                  EnableImmediateUpdates = true,
                                  ImmediateUpdateThrottle = TimeSpan.FromMinutes(1)
                              }))
                              .CreateLogger();

        logger.Information("{Marker}", firstMarker);
        logger.Error("{Marker}", errorMarker);
        logger.Information("{Marker}", lastMarker);

        string output = TestContext.Current!.GetStandardOutput();

        output.Should().Contain(firstMarker);
        output.Should().Contain(errorMarker);
        output.Should().Contain(lastMarker);
        output.IndexOf(firstMarker, StringComparison.Ordinal).Should().BeLessThan(output.IndexOf(errorMarker, StringComparison.Ordinal));
        output.IndexOf(errorMarker, StringComparison.Ordinal).Should().BeLessThan(output.IndexOf(lastMarker, StringComparison.Ordinal));
        TestContext.Current.GetErrorOutput().Should().NotContain(errorMarker);
    }

    [Test]
    public void Sink_should_write_concurrent_events_as_atomic_lines()
    {
        const int iterations = 4000;
        const string marker = "ATOMIC_TUNIT_LINE";

        using Logger logger = new LoggerConfiguration()
                              .WriteTo.Sink(new TUnitTestContextSink(new TUnitTestContextSinkOptions
                              {
                                  EnableImmediateUpdates = false
                              }))
                              .CreateLogger();

        Parallel.For(0, iterations, i => logger.Information("{Marker}:{Index:D5}", marker, i));

        string[] lines = TestContext.Current!.GetStandardOutput()
                                            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                                            .Where(line => line.Contains(marker, StringComparison.Ordinal))
                                            .ToArray();

        lines.Should().HaveCount(iterations);
        lines.Select(line => line[(line.IndexOf(marker, StringComparison.Ordinal) + marker.Length + 1)..])
             .Distinct(StringComparer.Ordinal)
             .Should()
             .HaveCount(iterations);
    }

    [Test]
    public void Sink_should_put_exceptions_on_the_next_line()
    {
        using Logger logger = new LoggerConfiguration()
                              .WriteTo.Sink(new TUnitTestContextSink(new TUnitTestContextSinkOptions
                              {
                                  EnableImmediateUpdates = false
                              }))
                              .CreateLogger();

        logger.Error(new InvalidOperationException("EXPECTED_EXCEPTION_TEXT"), "EXPECTED_FAILURE_MESSAGE");

        TestContext.Current!.GetStandardOutput()
                   .Should()
                   .Contain($"EXPECTED_FAILURE_MESSAGE{Environment.NewLine}System.InvalidOperationException: EXPECTED_EXCEPTION_TEXT");
    }
}
