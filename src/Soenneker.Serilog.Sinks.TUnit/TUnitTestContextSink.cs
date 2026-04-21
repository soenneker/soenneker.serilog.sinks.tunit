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
using System.Threading.Tasks;
using Soenneker.Serilog.Sinks.TUnit.Abstract;
using TUnit.Core.Logging;

namespace Soenneker.Serilog.Sinks.TUnit;

// ReSharper disable once InconsistentNaming
///<inheritdoc cref="ITUnitTestContextSink"/>
public sealed class TUnitTestContextSink : ITUnitTestContextSink
{
    private const string _defaultTemplate = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{Exception}";
    private static readonly ConcurrentDictionary<string, StringBuilder> _outputBuffers = new();
    private static readonly ConcurrentDictionary<string, StringBuilder> _errorBuffers = new();

    private static readonly PropertyInfo? _serviceProviderProperty =
        typeof(TestContext).GetProperty("ServiceProvider", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly ConcurrentDictionary<Type, Type?> _messageBusTypes = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo?> _publishMethods = new();

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

            if (TryPublishImmediateUpdate(context, message, logEvent.Level >= LogEventLevel.Error))
                return;

            DefaultLogger logger = context.GetDefaultLogger();
            logger.Log(MapLevel(logEvent.Level), message, logEvent.Exception, static (state, _) => state);
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

    private static bool TryPublishImmediateUpdate(TestContext context, string message, bool isError)
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

            var node = new TestNode
            {
                Uid = new TestNodeUid(testId),
                DisplayName = context.Metadata.DisplayName,
                Properties = CreateProperties(isError ? null : AppendAndSnapshot(_outputBuffers, testId, message),
                    isError ? AppendAndSnapshot(_errorBuffers, testId, message) : null)
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

    private static string AppendAndSnapshot(ConcurrentDictionary<string, StringBuilder> buffers, string testId, string message)
    {
        StringBuilder builder = buffers.GetOrAdd(testId, static _ => new StringBuilder());

        lock (builder)
        {
            if (builder.Length > 0)
                builder.AppendLine();

            builder.Append(message);
            return builder.ToString();
        }
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

    private static LogLevel MapLevel(LogEventLevel level)
    {
        return level switch
        {
            LogEventLevel.Verbose => LogLevel.Trace,
            LogEventLevel.Debug => LogLevel.Debug,
            LogEventLevel.Information => LogLevel.Information,
            LogEventLevel.Warning => LogLevel.Warning,
            LogEventLevel.Error => LogLevel.Error,
            LogEventLevel.Fatal => LogLevel.Critical,
            _ => LogLevel.Information
        };
    }
}