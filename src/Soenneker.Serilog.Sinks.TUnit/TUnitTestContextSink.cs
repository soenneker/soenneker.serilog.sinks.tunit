using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Soenneker.Atomics.ValueBools;
using Soenneker.Utils.ReusableStringWriter;
using System;
using System.Threading.Tasks;
using Soenneker.Serilog.Sinks.TUnit.Abstract;

namespace Soenneker.Serilog.Sinks.TUnit;

// ReSharper disable once InconsistentNaming
///<inheritdoc cref="ITUnitTestContextSink"/>
public sealed class TUnitTestContextSink : ITUnitTestContextSink
{
    private const string _defaultTemplate = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{Exception}";

    private readonly ITextFormatter _formatter;
    private readonly ReusableStringWriter _writer = new();

    private ValueAtomicBool _disposed;

    public TUnitTestContextSink() : this(new MessageTemplateTextFormatter(_defaultTemplate, null))
    {
    }

    public TUnitTestContextSink(ITextFormatter formatter)
    {
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
    }

    public void Emit(LogEvent? logEvent)
    {
        if (logEvent is null || _disposed.Value)
            return;

        TestContext? context = TestContext.Current;

        // We'll think on this later
        if (context is null)
            return;

        try
        {
            _writer.Reset();
            _formatter.Format(logEvent, _writer);

            string message = _writer.Finish();

            if (string.IsNullOrWhiteSpace(message))
                return;

            if (logEvent.Level >= LogEventLevel.Error)
                Console.Error.WriteLine(message);
            else
                Console.WriteLine(message);
        }
        catch
        {
            // Never let logging affect test execution.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed.TrySetTrue())
            return;

        try
        {
            await _writer.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (!_disposed.TrySetTrue())
            return;

        try
        {
            _writer.Dispose();
        }
        catch
        {
        }
    }
}
