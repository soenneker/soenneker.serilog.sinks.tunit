[![](https://img.shields.io/nuget/v/soenneker.serilog.sinks.tunit.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.serilog.sinks.tunit/)
[![](https://img.shields.io/github/actions/workflow/status/soenneker/soenneker.serilog.sinks.tunit/publish-package.yml?style=for-the-badge)](https://github.com/soenneker/soenneker.serilog.sinks.tunit/actions/workflows/publish-package.yml)
[![](https://img.shields.io/nuget/dt/soenneker.serilog.sinks.tunit.svg?style=for-the-badge)](https://www.nuget.org/packages/soenneker.serilog.sinks.tunit/)

# ![](https://user-images.githubusercontent.com/4441470/224455560-91ed3ee7-f510-4041-a8d2-3fc093025112.png) Soenneker.Serilog.Sinks.TUnit
### A Serilog sink for logging within TUnit

## Installation

```
dotnet add package Soenneker.Serilog.Sinks.TUnit
```

## Usage

Default usage enables live in-progress updates with a built-in `250ms` throttle:

```csharp
using var logger = new LoggerConfiguration()
    .WriteTo.Sink(new TUnitTestContextSink())
    .CreateLogger();
```

If you want to customize the live update behavior, pass options explicitly:

```csharp
using var logger = new LoggerConfiguration()
    .WriteTo.Sink(new TUnitTestContextSink(new TUnitTestContextSinkOptions
    {
        EnableImmediateUpdates = true,
        ImmediateUpdateThrottle = TimeSpan.FromMilliseconds(250)
    }))
    .CreateLogger();
```
