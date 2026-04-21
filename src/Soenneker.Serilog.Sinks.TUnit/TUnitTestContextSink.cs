using Microsoft.Testing.Platform.Extensions.Messages;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Soenneker.Atomics.ValueBools;
using Soenneker.Utils.ReusableStringWriter;
using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Serilog.Sinks.TUnit.Abstract;

namespace Soenneker.Serilog.Sinks.TUnit;

// ReSharper disable once InconsistentNaming
///<inheritdoc cref="ITUnitTestContextSink"/>
public sealed class TUnitTestContextSink : ITUnitTestContextSink
{
    private const string _defaultTemplate = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{Exception}";
    private static readonly ConcurrentDictionary<string, StringBuilder> _outputBuffers = new();
    private static readonly ConcurrentDictionary<string, StringBuilder> _errorBuffers = new();
    private static readonly ConcurrentDictionary<string, long> _lastImmediateUpdateTicks = new();

    private static readonly PropertyInfo? _serviceProviderProperty =
        typeof(TestContext).GetProperty("ServiceProvider", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly ConcurrentDictionary<Type, Type?> _messageBusTypes = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo?> _publishMethods = new();

    private readonly ITextFormatter _formatter;
    private readonly TUnitTestContextSinkOptions _options;
    private readonly ReusableStringWriter _writer = new();

    private ValueAtomicBool _disposed;

    public TUnitTestContextSink() : this(new MessageTemplateTextFormatter(_defaultTemplate, null), new TUnitTestContextSinkOptions())
    {
    }

    public TUnitTestContextSink(ITextFormatter formatter) : this(formatter, new TUnitTestContextSinkOptions())
    {
    }

    public TUnitTestContextSink(TUnitTestContextSinkOptions options) : this(new MessageTemplateTextFormatter(_defaultTemplate, null), options)
    {
    }

    public TUnitTestContextSink(ITextFormatter formatter, TUnitTestContextSinkOptions options)
    {
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
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

            int messageLength = GetMessageLengthWithoutTrailingNewLines(message);

            if (messageLength == 0)
                return;

            if (_options.EnableImmediateUpdates && TryPublishImmediateUpdate(context, message, messageLength, logEvent.Level >= LogEventLevel.Error, _options.ImmediateUpdateThrottle))
                return;

            WriteToTestOutput(context, message, messageLength, logEvent.Level >= LogEventLevel.Error);
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

    private static bool TryPublishImmediateUpdate(TestContext context, string message, int messageLength, bool isError, TimeSpan throttle)
    {
        try
        {
            IServiceProvider? serviceProvider = GetServiceProvider(context);

            if (serviceProvider is null)
                return false;

            Type? messageBusType = _messageBusTypes.GetOrAdd(serviceProvider.GetType(), static type => type.Assembly.GetType("TUnit.Engine.TUnitMessageBus"));

            if (messageBusType is null)
                return false;

            object? messageBus = serviceProvider.GetService(messageBusType);

            if (messageBus is null)
                return false;

            MethodInfo? publishMethod = _publishMethods.GetOrAdd(messageBusType,
                static type => type.GetMethod("PublishOutputUpdate", BindingFlags.Instance | BindingFlags.Public));

            if (publishMethod is null)
                return false;

            string testId = context.Metadata.TestDetails.TestId;

            if (!ShouldPublishImmediateUpdate(testId, throttle))
                return false;

            var node = new TestNode
            {
                Uid = new TestNodeUid(testId),
                DisplayName = context.Metadata.DisplayName,
                Properties = CreateProperties(isError ? null : AppendAndSnapshot(_outputBuffers, testId, message, messageLength),
                    isError ? AppendAndSnapshot(_errorBuffers, testId, message, messageLength) : null)
            };

            _ = publishMethod.Invoke(messageBus, [node]);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IServiceProvider? GetServiceProvider(TestContext context)
    {
        return _serviceProviderProperty?.GetValue(context) as IServiceProvider;
    }

    private static bool ShouldPublishImmediateUpdate(string testId, TimeSpan throttle)
    {
        if (throttle <= TimeSpan.Zero)
            return true;

        long now = DateTime.UtcNow.Ticks;

        while (true)
        {
            long previous = _lastImmediateUpdateTicks.GetOrAdd(testId, static _ => 0);

            if (previous != 0 && now - previous < throttle.Ticks)
                return false;

            if (_lastImmediateUpdateTicks.TryUpdate(testId, now, previous))
                return true;

            if (previous == 0 && _lastImmediateUpdateTicks.TryAdd(testId, now))
                return true;

            Thread.SpinWait(1);
        }
    }

    private static void WriteToTestOutput(TestContext context, string message, int messageLength, bool isError)
    {
        if (isError)
        {
            context.Output.ErrorOutput.Write(message.AsSpan(0, messageLength));
            context.Output.ErrorOutput.WriteLine();
            return;
        }

        context.Output.StandardOutput.Write(message.AsSpan(0, messageLength));
        context.Output.StandardOutput.WriteLine();
    }

    private static string AppendAndSnapshot(ConcurrentDictionary<string, StringBuilder> buffers, string testId, string message, int messageLength)
    {
        StringBuilder builder = buffers.GetOrAdd(testId, static _ => new StringBuilder());

        lock (builder)
        {
            if (builder.Length > 0)
                builder.AppendLine();

            builder.Append(message.AsSpan(0, messageLength));
            return builder.ToString();
        }
    }

    private static int GetMessageLengthWithoutTrailingNewLines(string message)
    {
        int length = message.Length;

        while (length > 0)
        {
            char c = message[length - 1];

            if (c is '\r' or '\n')
            {
                length--;
                continue;
            }

            break;
        }

        return length;
    }

    private static PropertyBag CreateProperties(string? output, string? error)
    {
        var properties = new PropertyBag(InProgressTestNodeStateProperty.CachedInstance);

        if (!string.IsNullOrEmpty(output))
#pragma warning disable TPEXP
            properties.Add(new StandardOutputProperty(output));
#pragma warning restore TPEXP

        if (!string.IsNullOrEmpty(error))
#pragma warning disable TPEXP
            properties.Add(new StandardErrorProperty(error));
#pragma warning restore TPEXP

        return properties;
    }

}
