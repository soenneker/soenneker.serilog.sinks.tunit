[![](https://img.shields.io/nuget/v/soenneker.serilog.sinks.tunit.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.serilog.sinks.tunit/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.serilog.sinks.tunit/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.serilog.sinks.tunit/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.serilog.sinks.tunit.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.serilog.sinks.tunit/)

# Soenneker.Serilog.Sinks.TUnit

Serilog sink that writes formatted log events to the current TUnit `TestContext` output. Uses `ReusableStringWriter` to avoid per-log allocations.

## Install

```bash
dotnet add package Soenneker.Serilog.Sinks.TUnit
```

## Quick start

```csharp
using Soenneker.Serilog.Sinks.TUnit.Abstract;

ITUnitTestContextSink tUnitTestContextSink = /* resolve from DI */;
tUnitTestContextSink.Emit(/* supply logEvent */ default!);
```

Emits the event unless testOutputHelper is null. In that case, it caches it for later (and then emits them all when it's not) Will NOT cache IMessageSink log events.

## What you get

- `ITUnitTestContextSink` — Serilog sink that writes formatted log events to the current TUnit `TestContext` output. Uses `ReusableStringWriter` to avoid per-log allocations.
- `TUnitTestContextSinkOptions` — Configuration for `TUnitTestContextSink`.

## API at a glance

| API | What it does | Result / important behavior |
| --- | --- | --- |
| `ITUnitTestContextSink.Emit(logEvent)` | Emits the event unless testOutputHelper is null. In that case, it caches it for later (and then emits them all when it's not) Will NOT cache IMessageSink log events. | Returns no value; the requested change is complete when the method returns. |
| `ITUnitTestContextSink.DisposeAsync()` | Flushes buffered test output and releases sink resources. The operation is idempotent; Serilog normally calls it during disposal. | A task that completes after buffered output has been flushed. |
| `TUnitTestContextSinkOptions.EnableImmediateUpdates` | Enables live output updates through TUnit's message bus. This can significantly slow down IDE test runners when a test emits many log lines. | Enables live output updates through TUnit's message bus. This can significantly slow down IDE test runners when a test emits many log lines. |
| `TUnitTestContextSinkOptions.ImmediateUpdateThrottle` | The minimum interval between live output updates for a single test when `EnableImmediateUpdates` is enabled. | The minimum interval between live output updates for a single test when `EnableImmediateUpdates` is enabled. |

## Practical notes

- Dispose instances you own when their scope ends so held resources can be released.
