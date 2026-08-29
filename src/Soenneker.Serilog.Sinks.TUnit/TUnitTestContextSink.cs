using Microsoft.Testing.Platform.Extensions.Messages;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Soenneker.Atomics.ValueBools;
using Soenneker.Utils.ReusableStringWriter;
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Serilog.Sinks.TUnit.Abstract;
using Soenneker.Serilog.Sinks.TUnit.Dtos;

namespace Soenneker.Serilog.Sinks.TUnit;

// ReSharper disable once InconsistentNaming
///<inheritdoc cref="ITUnitTestContextSink"/>
public sealed class TUnitTestContextSink : ITUnitTestContextSink
{
    private const string _defaultTemplate = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{Exception}";
    private const string _immediateUpdateStateKey = "Soenneker.Serilog.Sinks.TUnit.ImmediateUpdateState";

    private static readonly PropertyInfo? _serviceProviderProperty = typeof(TestContext).GetProperty("ServiceProvider",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private readonly ITextFormatter _formatter;
    private readonly TUnitTestContextSinkOptions _options;
    private readonly ReusableStringWriter _writer = new();

    private ImmediateUpdatePublisher? _immediateUpdatePublisher;
    private ValueAtomicBool _disposed;

    public TUnitTestContextSink() : this(new MessageTemplateTextFormatter(_defaultTemplate, null),
        new TUnitTestContextSinkOptions())
    {
    }

    public TUnitTestContextSink(ITextFormatter formatter) : this(formatter, new TUnitTestContextSinkOptions())
    {
    }

    public TUnitTestContextSink(TUnitTestContextSinkOptions options) : this(
        new MessageTemplateTextFormatter(_defaultTemplate, null), options)
    {
    }

    public TUnitTestContextSink(ITextFormatter formatter, TUnitTestContextSinkOptions options)
    {
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Emit on the T Unit Test Context Sink.
    /// </summary>
    /// <param name="logEvent">Log Event for the emit operation.</param>
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
            string message;

            lock (_writer)
            {
                _writer.Reset();
                _formatter.Format(logEvent, _writer);
                message = _writer.Finish();
            }

            int messageLength = GetMessageLengthWithoutTrailingNewLines(message);

            if (messageLength == 0)
                return;

            bool isError = logEvent.Level >= LogEventLevel.Error;
            WriteToTestOutput(context, message, messageLength, isError);

            if (_options.EnableImmediateUpdates)
                TryPublishImmediateUpdate(context, isError, _options.ImmediateUpdateThrottle);
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

    /// <summary>
    /// Releases resources used by the current instance.
    /// </summary>
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

    private bool TryPublishImmediateUpdate(TestContext context, bool isError, TimeSpan throttle)
    {
        try
        {
            if (!ShouldPublishImmediateUpdate(context, throttle))
                return true;

            IServiceProvider? serviceProvider = GetServiceProvider(context);

            if (serviceProvider is null)
                return false;

            Type serviceProviderType = serviceProvider.GetType();
            ImmediateUpdatePublisher? publisher = Volatile.Read(ref _immediateUpdatePublisher);

            if (publisher is null || publisher.ServiceProviderType != serviceProviderType)
            {
                publisher = CreateImmediateUpdatePublisher(serviceProviderType);

                if (publisher is null)
                    return false;

                Volatile.Write(ref _immediateUpdatePublisher, publisher);
            }

            object? messageBus = serviceProvider.GetService(publisher.MessageBusType);

            if (messageBus is null)
                return false;

            string snapshot = isError ? context.GetErrorOutput() : context.GetStandardOutput();
            PublishImmediateUpdate(context, publisher.PublishInvoker, messageBus, snapshot, isError);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static ImmediateUpdatePublisher? CreateImmediateUpdatePublisher(Type serviceProviderType)
    {
        Type? messageBusType = serviceProviderType.Assembly.GetType("TUnit.Engine.TUnitMessageBus");

        if (messageBusType is null)
            return null;

        MethodInfo? publishMethod =
            messageBusType.GetMethod("PublishOutputUpdate", BindingFlags.Instance | BindingFlags.Public);

        return publishMethod is null
            ? null
            : new ImmediateUpdatePublisher(serviceProviderType, messageBusType, MethodInvoker.Create(publishMethod));
    }

    private static void PublishImmediateUpdate(TestContext context, MethodInvoker publishInvoker, object messageBus,
        string snapshot, bool isError)
    {
        var node = new TestNode
        {
            Uid = new TestNodeUid(context.Metadata.TestDetails.TestId),
            DisplayName = context.Metadata.DisplayName,
            Properties = CreateProperties(isError ? null : snapshot, isError ? snapshot : null)
        };

        _ = publishInvoker.Invoke(messageBus, node);
    }

    private static IServiceProvider? GetServiceProvider(TestContext context)
    {
        return _serviceProviderProperty?.GetValue(context) as IServiceProvider;
    }

    private static bool ShouldPublishImmediateUpdate(TestContext context, TimeSpan throttle)
    {
        if (throttle <= TimeSpan.Zero)
            return true;

        ImmediateUpdateState state =
            context.StateBag.GetOrAdd(_immediateUpdateStateKey, static _ => new ImmediateUpdateState());
        long now = DateTime.UtcNow.Ticks;

        while (true)
        {
            long previous = Volatile.Read(ref state.LastUpdateTicks);

            if (previous != 0 && now - previous < throttle.Ticks)
                return false;

            if (Interlocked.CompareExchange(ref state.LastUpdateTicks, now, previous) == previous)
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
