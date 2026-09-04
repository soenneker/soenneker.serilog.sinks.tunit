using Soenneker.Extensions.ValueTask;
using Soenneker.Extensions.Task;
using Microsoft.Testing.Platform.Extensions.Messages;
using Serilog.Debugging;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;
using Soenneker.Atomics.ValueBools;
using Soenneker.Serilog.Sinks.TUnit.Abstract;
using Soenneker.Serilog.Sinks.TUnit.Dtos;
using Soenneker.Utils.ReusableStringWriter;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Soenneker.Serilog.Sinks.TUnit;

// ReSharper disable once InconsistentNaming
/// <inheritdoc cref="ITUnitTestContextSink" />
public sealed class TUnitTestContextSink : ITUnitTestContextSink
{
    private const string _defaultTemplate = "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}";
    private static readonly long _priorityThrottleTimestampTicks = Math.Max(1, Stopwatch.Frequency / 20);

    private static readonly PropertyInfo? _serviceProviderProperty = typeof(TestContext).GetProperty("ServiceProvider",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private readonly ITextFormatter _formatter;
    private readonly bool _appendException;
    private readonly bool _immediateUpdatesEnabled;
    private readonly long _throttleTimestampTicks;
    private readonly ConcurrentBag<ReusableStringWriter> _writers = new();
    private readonly ConditionalWeakTable<TestContext, ImmediateUpdateState> _states = new();
    private readonly ConcurrentQueue<ImmediateUpdateState> _pendingUpdates = new();
    private readonly SemaphoreSlim? _publisherSignal;
    private readonly Task? _publisherTask;

    private ImmediateUpdatePublisher? _immediateUpdatePublisher;
    private ValueAtomicBool _disposed;
    private int _activeEmits;
    private int _publisherFailureReported;

    public TUnitTestContextSink() : this(new MessageTemplateTextFormatter(_defaultTemplate, null),
        new TUnitTestContextSinkOptions(), true)
    {
    }

    public TUnitTestContextSink(ITextFormatter formatter) : this(formatter, new TUnitTestContextSinkOptions(), false)
    {
    }

    public TUnitTestContextSink(TUnitTestContextSinkOptions options) : this(
        new MessageTemplateTextFormatter(_defaultTemplate, null), options, true)
    {
    }

    public TUnitTestContextSink(ITextFormatter formatter, TUnitTestContextSinkOptions options) : this(formatter, options, false)
    {
    }

    private TUnitTestContextSink(ITextFormatter formatter, TUnitTestContextSinkOptions options, bool appendException)
    {
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        ArgumentNullException.ThrowIfNull(options);

        _appendException = appendException;
        _immediateUpdatesEnabled = options.EnableImmediateUpdates;
        _throttleTimestampTicks = GetThrottleTimestampTicks(options.ImmediateUpdateThrottle);

        if (_immediateUpdatesEnabled)
        {
            _publisherSignal = new SemaphoreSlim(0);
            _publisherTask = Task.Run(PublishUpdatesAsync);
        }
    }

    public void Emit(LogEvent? logEvent)
    {
        if (logEvent is null)
            return;

        Interlocked.Increment(ref _activeEmits);

        try
        {
            if (_disposed.Value)
                return;

            TestContext? context = TestContext.Current;

            if (context is null)
                return;

            string message = Format(logEvent);
            int messageLength = GetMessageLengthWithoutTrailingNewLines(message);

            if (messageLength == 0)
                return;

            if (messageLength != message.Length)
                message = message[..messageLength];

            bool isPriority = logEvent.Level >= LogEventLevel.Error;
            WriteToTestOutput(context, message);

            if (_immediateUpdatesEnabled)
                QueueImmediateUpdate(context, isPriority);
        }
        catch
        {
            // Never let logging affect test execution.
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeEmits) == 0 && _disposed.Value)
                SignalPublisher();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed.TrySetTrue())
            return;

        await StopPublisherAsync().NoSync();
        DisposeWriters();
    }

    public void Dispose()
    {
        if (!_disposed.TrySetTrue())
            return;

        StopPublisher();
        DisposeWriters();
    }

    private string Format(LogEvent logEvent)
    {
        if (!_writers.TryTake(out ReusableStringWriter? writer))
            writer = new ReusableStringWriter();

        try
        {
            writer.Reset();
            _formatter.Format(logEvent, writer);
            string message = writer.Finish();

            return _appendException && logEvent.Exception is not null
                ? string.Concat(message, Environment.NewLine, logEvent.Exception.ToString())
                : message;
        }
        finally
        {
            if (_disposed.Value)
                writer.Dispose();
            else
                _writers.Add(writer);
        }
    }

    private void QueueImmediateUpdate(TestContext context, bool isPriority)
    {
        // TUnit's final node contains the authoritative captured output. Publishing another
        // in-progress node after that point can corrupt a stateful IDE runner's test state.
        if (context.Execution.Result is not null)
            return;

        ImmediateUpdateState state = _states.GetValue(context, static current => new ImmediateUpdateState(current));
        state.MarkDirty(isPriority);

        bool queued = state.TryQueue();

        if (queued)
        {
            _pendingUpdates.Enqueue(state);
            SignalPublisher();
        }
        else if (isPriority && state.TryRequestPriorityWake())
            SignalPublisher();
    }

    private async Task PublishUpdatesAsync()
    {
        var scheduled = new List<ImmediateUpdateState>();

        try
        {
            while (true)
            {
                DrainPendingUpdates(scheduled);

                if (_disposed.Value)
                {
                    for (int i = scheduled.Count - 1; i >= 0; i--)
                    {
                        await PublishStateAsync(scheduled[i]).NoSync();
                        scheduled.RemoveAt(i);
                    }

                    DrainPendingUpdates(scheduled);

                    if (scheduled.Count == 0)
                    {
                        if (Volatile.Read(ref _activeEmits) == 0)
                            break;

                        await _publisherSignal!.WaitAsync().NoSync();
                    }

                    continue;
                }

                long now = Stopwatch.GetTimestamp();

                for (int i = scheduled.Count - 1; i >= 0; i--)
                {
                    ImmediateUpdateState state = scheduled[i];

                    if (!state.HasPriority && !IsDue(state, now))
                        continue;

                    await PublishStateAsync(state).NoSync();
                    scheduled.RemoveAt(i);
                }

                TimeSpan delay = GetNextDelay(scheduled, Stopwatch.GetTimestamp());
                await _publisherSignal!.WaitAsync(delay).NoSync();
            }
        }
        catch (Exception exception)
        {
            ReportPublisherFailure(exception);
        }
    }

    private void DrainPendingUpdates(List<ImmediateUpdateState> scheduled)
    {
        while (_pendingUpdates.TryDequeue(out ImmediateUpdateState? state))
            scheduled.Add(state);
    }

    private async ValueTask PublishStateAsync(ImmediateUpdateState state)
    {
        state.ConsumePriority();
        long publishedVersion = state.Version;

        try
        {
            TestContext context = state.Context;

            if (context.Execution.Result is not null)
                return;

            string output = context.GetStandardOutput();
            string error = context.GetErrorOutput();

            if (!string.IsNullOrEmpty(output) || !string.IsNullOrEmpty(error))
                await PublishImmediateUpdateAsync(context, output, error).NoSync();
        }
        catch (Exception exception)
        {
            ReportPublisherFailure(exception);
        }
        finally
        {
            bool changed = state.CompletePublication(publishedVersion, Stopwatch.GetTimestamp());

            if (changed && state.TryQueue())
            {
                _pendingUpdates.Enqueue(state);
                SignalPublisher();
            }
        }
    }

    private async ValueTask PublishImmediateUpdateAsync(TestContext context, string output, string error)
    {
        IServiceProvider? serviceProvider = GetServiceProvider(context);

        if (serviceProvider is null)
            throw new InvalidOperationException("TUnit's service provider is unavailable.");

        Type serviceProviderType = serviceProvider.GetType();
        ImmediateUpdatePublisher? publisher = Volatile.Read(ref _immediateUpdatePublisher);

        if (publisher is null || publisher.ServiceProviderType != serviceProviderType)
        {
            publisher = CreateImmediateUpdatePublisher(serviceProviderType);

            if (publisher is null)
                throw new InvalidOperationException("TUnit's live output publisher is unavailable.");

            Volatile.Write(ref _immediateUpdatePublisher, publisher);
        }

        object? messageBus = serviceProvider.GetService(publisher.MessageBusType);

        if (messageBus is null)
            throw new InvalidOperationException("TUnit's message bus is unavailable.");

        var node = new TestNode
        {
            Uid = new TestNodeUid(context.Metadata.TestDetails.TestId),
            DisplayName = context.Metadata.DisplayName,
            Properties = CreateProperties(output, error)
        };

        if (context.Execution.Result is not null)
            return;

        object? invocationResult = publisher.PublishInvoker.Invoke(messageBus, node);

        switch (invocationResult)
        {
            case ValueTask valueTask:
                await valueTask.NoSync();
                break;
            case Task task:
                await task.NoSync();
                break;
        }
    }

    private bool IsDue(ImmediateUpdateState state, long now)
    {
        long interval = GetPublishInterval(state);

        if (interval == 0)
            return true;

        long lastPublished = Volatile.Read(ref state.LastPublishedTimestamp);
        return lastPublished == 0 || now - lastPublished >= interval;
    }

    private TimeSpan GetNextDelay(List<ImmediateUpdateState> scheduled, long now)
    {
        if (scheduled.Count == 0)
            return Timeout.InfiniteTimeSpan;

        long minimumRemaining = long.MaxValue;

        foreach (ImmediateUpdateState state in scheduled)
        {
            if (state.HasPriority)
                return TimeSpan.Zero;

            long lastPublished = Volatile.Read(ref state.LastPublishedTimestamp);
            long interval = GetPublishInterval(state);

            if (lastPublished == 0 || interval == 0)
                return TimeSpan.Zero;

            long remaining = interval - (now - lastPublished);

            if (remaining <= 0)
                return TimeSpan.Zero;

            if (remaining < minimumRemaining)
                minimumRemaining = remaining;
        }

        double delayMilliseconds = minimumRemaining * 1000d / Stopwatch.Frequency;
        return TimeSpan.FromMilliseconds(Math.Min(delayMilliseconds, int.MaxValue));
    }

    private long GetPublishInterval(ImmediateUpdateState state)
    {
        if (!state.HasPriority || _throttleTimestampTicks == 0)
            return _throttleTimestampTicks;

        return Math.Min(_throttleTimestampTicks, _priorityThrottleTimestampTicks);
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

    private static IServiceProvider? GetServiceProvider(TestContext context)
    {
        return _serviceProviderProperty?.GetValue(context) as IServiceProvider;
    }

    private void SignalPublisher()
    {
        if (_publisherSignal is null)
            return;

        try
        {
            _publisherSignal.Release();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private async ValueTask StopPublisherAsync()
    {
        if (_publisherTask is null)
        {
            while (Volatile.Read(ref _activeEmits) != 0)
                await Task.Yield();

            return;
        }

        SignalPublisher();
        await _publisherTask.NoSync();
        _publisherSignal!.Dispose();
    }

    private void StopPublisher()
    {
        if (_publisherTask is null)
        {
            var spinner = new SpinWait();

            while (Volatile.Read(ref _activeEmits) != 0)
                spinner.SpinOnce();

            return;
        }

        SignalPublisher();
        _publisherTask.GetAwaiter().GetResult();
        _publisherSignal!.Dispose();
    }

    private void DisposeWriters()
    {
        while (_writers.TryTake(out ReusableStringWriter? writer))
            writer.Dispose();
    }

    private void ReportPublisherFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _publisherFailureReported, 1) == 0)
            SelfLog.WriteLine("TUnit live output publication failed: {0}", exception);
    }

    private static long GetThrottleTimestampTicks(TimeSpan throttle)
    {
        if (throttle <= TimeSpan.Zero)
            return 0;

        double timestampTicks = throttle.TotalSeconds * Stopwatch.Frequency;
        return timestampTicks >= long.MaxValue ? long.MaxValue : Math.Max(1, (long) timestampTicks);
    }

    private static void WriteToTestOutput(TestContext context, string message) => context.Output.WriteLine(message);

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
