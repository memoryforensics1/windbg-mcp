using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace WinDbgMCP.Server.KernelDebug;

/// <summary>
/// Dedicated thread for ALL DbgEng COM operations.
/// DbgEng has strict thread affinity — all calls must happen on the thread
/// that called DebugCreate. This class marshals work items to that thread.
/// </summary>
public sealed class DbgEngThread : IDisposable
{
    private readonly Thread _thread;
    private readonly BlockingCollection<WorkItem> _workQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger _logger;
    private volatile bool _disposed;

    /// <summary>
    /// Set by DbgEngManager after initialization to allow the thread to pump events.
    /// </summary>
    public Action? PumpEventsAction { get; set; }

    /// <summary>
    /// Whether event pumping is enabled (only when target is running).
    /// </summary>
    public volatile bool PumpEnabled;

    /// <summary>
    /// The work item currently executing on the engine thread, if any. A tool
    /// whose item timed out while *running* keeps running here (dbgeng cannot
    /// cancel a command); later items wait behind it and are told so.
    /// </summary>
    private volatile WorkItem? _current;
    private long _currentStartedTicks;

    /// <summary>Label of the item running past some tool's timeout, with its running time; null when idle.</summary>
    public string? BusyDescription
    {
        get
        {
            var cur = _current;
            if (cur == null || !cur.OutlivedTimeout) return null;
            var secs = (Environment.TickCount64 - Interlocked.Read(ref _currentStartedTicks)) / 1000;
            return $"'{cur.Label}' (running for {secs} s)";
        }
    }

    public DbgEngThread(ILogger logger)
    {
        _logger = logger;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "DbgEng-Thread"
        };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    private void Run()
    {
        _logger.LogInformation("DbgEng thread started (ThreadId={ThreadId})", Environment.CurrentManagedThreadId);

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                // Priority 1: Process any queued tool calls
                if (_workQueue.TryTake(out var work, TimeSpan.FromMilliseconds(0)))
                {
                    Interlocked.Exchange(ref _currentStartedTicks, Environment.TickCount64);
                    _current = work;
                    try
                    {
                        work.Execute();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Work item failed on DbgEng thread");
                        work.SetException(ex);
                    }
                    finally
                    {
                        _current = null;
                    }
                    continue;
                }

                // Priority 2: If no tool calls pending and pump enabled, pump events
                if (PumpEnabled && PumpEventsAction != null)
                {
                    try
                    {
                        PumpEventsAction();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Event pump iteration failed");
                    }
                    continue;
                }

                // Nothing to do — brief sleep to avoid busy-waiting
                Thread.Sleep(50);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }

        _logger.LogInformation("DbgEng thread exiting");
    }

    /// <summary>
    /// Execute a function on the DbgEng thread and return the result.
    /// </summary>
    public Task<T> ExecuteAsync<T>(Func<T> work, TimeSpan timeout, string? label = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(DbgEngThread));

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource(timeout);
        WorkItem? item = null;

        item = new WorkItem(() =>
        {
            if (cts.IsCancellationRequested)
            {
                tcs.TrySetCanceled();
                return;
            }

            try
            {
                var result = work();
                tcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }, label ?? "an engine operation");

        // Timeout: if another item is the one occupying the thread, say so rather
        // than reporting a bare timeout - the caller's work never even started.
        cts.Token.Register(() =>
        {
            var cur = _current;
            if (cur == item)
            {
                cur.OutlivedTimeout = true;
                tcs.TrySetCanceled();
            }
            else if (cur != null)
            {
                cur.OutlivedTimeout = true;
                var secs = (Environment.TickCount64 - Interlocked.Read(ref _currentStartedTicks)) / 1000;
                tcs.TrySetException(new EngineBusyException(cur.Label, secs));
            }
            else
            {
                tcs.TrySetCanceled();
            }
        }, useSynchronizationContext: false);

        _workQueue.Add(item);
        return tcs.Task;
    }

    /// <summary>
    /// Execute a void action on the DbgEng thread.
    /// </summary>
    public Task ExecuteAsync(Action work, TimeSpan timeout, string? label = null)
    {
        return ExecuteAsync<object?>(() => { work(); return null; }, timeout, label);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _workQueue.CompleteAdding();

        if (_thread.IsAlive)
            _thread.Join(TimeSpan.FromSeconds(5));

        _cts.Dispose();
        _workQueue.Dispose();
    }

    private class WorkItem
    {
        private readonly Action _action;
        private Exception? _exception;

        public string Label { get; }
        public volatile bool OutlivedTimeout;

        public WorkItem(Action action, string label)
        {
            _action = action;
            Label = label;
        }

        public void Execute() => _action();

        public void SetException(Exception ex)
        {
            _exception = ex;
        }
    }
}
