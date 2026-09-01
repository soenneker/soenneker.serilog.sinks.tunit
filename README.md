[![](https://img.shields.io/nuget/v/soenneker.serilog.sinks.tunit.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.serilog.sinks.tunit/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.serilog.sinks.tunit/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.serilog.sinks.tunit/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.serilog.sinks.tunit.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker/soenneker.serilog.sinks.tunit/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.serilog.sinks.tunit/codeql.yml?label=CodeQL&style=for-the-badge)](https://github.com/soenneker/soenneker.serilog.sinks.tunit/actions/workflows/codeql.yml)

# Soenneker.Serilog.Sinks.TUnit

A Serilog sink that writes each event to the active TUnit test's standard or error output.

## Installation

```bash
dotnet add package Soenneker.Serilog.Sinks.TUnit
```

## Configure Serilog

Create the sink once in your test assembly setup and add it to the logger:

```csharp
using Serilog;
using Soenneker.Serilog.Sinks.TUnit;

var sink = new TUnitTestContextSink(new TUnitTestContextSinkOptions
{
    EnableImmediateUpdates = true,
    ImmediateUpdateThrottle = TimeSpan.FromMilliseconds(250)
});

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Sink(sink)
    .CreateLogger();
```

Serilog events emitted while `TestContext.Current` is available are attached to that test. Error and fatal events go to TUnit's error output; all lower levels go to standard output.

Events emitted without an active `TestContext` are discarded. The sink does not queue them for a later test. This matters for background work that outlives the test or runs outside its execution context—await that work before the test completes if its logs are needed.

The default format is:

```text
[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{Exception}
```

Supply any Serilog `ITextFormatter` when a different representation is needed:

```csharp
var formatter = new MessageTemplateTextFormatter(
    "{Timestamp:O} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

var sink = new TUnitTestContextSink(formatter, options);
```

## Immediate IDE updates

`EnableImmediateUpdates` publishes cumulative snapshots of the current test output through TUnit's message bus so supported runners can display logs while the test is still running. It is enabled by default and throttled to one update per test every 250 ms.

Formatting and TUnit output capture stay on the logging thread. Snapshot creation and message-bus publication run on a single background worker, with at most one queued update per test. Messages received inside the throttle interval are included in a trailing update; error and fatal events wake the publisher immediately.

This feature depends on TUnit engine services discovered at runtime. If they are unavailable, normal test output still works. For high-volume logging or runners that become slow while refreshing output, disable immediate updates:

```csharp
var sink = new TUnitTestContextSink(new TUnitTestContextSinkOptions
{
    EnableImmediateUpdates = false
});
```

Values at or below zero allow every dirty test to be published without a throttle and can be expensive. The configured options are captured when the sink is constructed.

## Teardown

Dispose the logger during test assembly teardown:

```csharp
await Log.CloseAndFlushAsync();
```

The sink's synchronous and asynchronous disposal paths are idempotent. Formatting and TUnit output failures are intentionally swallowed so logging cannot fail a test.
