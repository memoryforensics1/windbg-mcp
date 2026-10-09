using System.Collections.Concurrent;
using ClrDebug;
using ClrDebug.DbgEng;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WinDbgMCP.Server.KernelDebug.Models;

namespace WinDbgMCP.Server.KernelDebug;

/// <summary>
/// Handles debug events from the kernel debug target.
/// Events are queued for consumption by the MCP tools.
/// </summary>
public sealed class DebugEventCallbacks : DebugBaseEventCallbacks
{
    private readonly ConcurrentQueue<DebugEvent> _eventQueue = new();
    private readonly ILogger _logger;
    private volatile DEBUG_STATUS _lastExecutionStatus = DEBUG_STATUS.NO_DEBUGGEE;
    private volatile bool _hasBreakingEvent;
    private volatile bool _rebootDetected;
    private volatile bool _breakInPending;
    private volatile bool _secondChancePending;
    private long _breakInAddress;

    private const uint StatusBreakpoint = 0x80000003;
    private const uint StatusSingleStep = 0x80000004;
    private const uint StatusWow64Breakpoint = 0x4000001F;
    private const uint StatusWow64SingleStep = 0x4000001E;
    private const int RecentEventCapacity = 20;

    // Timestamps of the last module load/unload events. A burst of them is what a
    // booting kernel looks like from here, and a booting kernel drops break-ins.
    private readonly ConcurrentQueue<long> _moduleEventTicks = new();
    private const int ModuleEventWindowMs = 10_000;
    public const int ModuleFloodThreshold = 5;

    /// <summary>Module load/unload events seen in the last 10 seconds.</summary>
    public int ModuleEventsInLast10s
    {
        get
        {
            var cutoff = Environment.TickCount64 - ModuleEventWindowMs;
            return _moduleEventTicks.Count(t => t >= cutoff);
        }
    }

    /// <summary>True while the kernel is loading modules in bulk (boot, or a big driver load).</summary>
    public bool IsModuleFlood => ModuleEventsInLast10s >= ModuleFloodThreshold;

    private void NoteModuleEvent()
    {
        _moduleEventTicks.Enqueue(Environment.TickCount64);
        while (_moduleEventTicks.Count > 64)
            _moduleEventTicks.TryDequeue(out _);
    }
    private readonly ConcurrentQueue<DebugEvent> _recentEvents = new();

    public DebugEventCallbacks(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public int PendingCount => _eventQueue.Count;
    /// <summary>Queued module/process/thread events: noise to the model, never a reason to stop.</summary>
    public int PendingInformationalCount => _eventQueue.Count(e => IsInformational(e.Type));
    /// <summary>Queued events that stop the target or change the session (bugcheck, breakpoint, reboot...).</summary>
    public int PendingImportantCount => _eventQueue.Count(e => !IsInformational(e.Type));
    public DEBUG_STATUS LastExecutionStatus => _lastExecutionStatus;

    /// <summary>
    /// True if a breaking event (breakpoint, exception, system error) occurred
    /// since the last ClearBreakingEventFlag call. Used by the pump to distinguish
    /// real events from yield interrupts.
    /// </summary>
    public bool HasBreakingEvent => _hasBreakingEvent;
    public bool RebootDetected => _rebootDetected;

    // Incremented on every REBOOT; stamped on every event from then on.
    private int _rebootGeneration;
    public int RebootGeneration => _rebootGeneration;
    public void ResetRebootGeneration() => Interlocked.Exchange(ref _rebootGeneration, 0);

    /// <summary>True while the target sits at an unhandled (second-chance) exception.</summary>
    public bool SecondChancePending => _secondChancePending;
    public void ClearSecondChancePending() => _secondChancePending = false;

    public void ClearBreakingEventFlag() => _hasBreakingEvent = false;
    public void ClearRebootFlag() => _rebootDetected = false;
    // The recent-events ring deliberately survives a reconnect: the
    // SessionEnded/TargetRebooted history is exactly what is worth re-reading.
    public void ClearEvents() => _eventQueue.Clear();

    /// <summary>
    /// Invoked on the DbgEng thread for every queued event (push notifications).
    /// Must never throw or block.
    /// </summary>
    public Action<DebugEvent>? EventRaised { get; set; }

    private void Enqueue(DebugEvent evt)
    {
        evt.Generation = _rebootGeneration;
        _eventQueue.Enqueue(evt);
        try { EventRaised?.Invoke(evt); }
        catch (Exception ex) { _logger.LogDebug(ex, "EventRaised handler failed"); }
    }

    public void EnqueueError(string details) =>
        Enqueue(new DebugEvent { Type = DebugEventKind.Error, Details = details });

    /// <summary>
    /// Records a breaking event classified outside the callback (see
    /// DbgEngManager.ClassifyPendingBreakIn).
    /// </summary>
    public void RecordBreakingEvent(DebugEvent evt)
    {
        _hasBreakingEvent = true;
        Enqueue(evt);
    }

    /// <summary>
    /// Consumes the address of the most recent first-chance break instruction
    /// (int 3) that has not been classified yet.
    /// </summary>
    public bool TryTakePendingBreakIn(out ulong address)
    {
        address = (ulong)Interlocked.Read(ref _breakInAddress);
        if (!_breakInPending)
            return false;
        _breakInPending = false;
        return true;
    }

    public override HRESULT GetInterestMask(out DEBUG_EVENT_TYPE mask)
    {
        mask = DEBUG_EVENT_TYPE.BREAKPOINT
             | DEBUG_EVENT_TYPE.EXCEPTION
             | DEBUG_EVENT_TYPE.LOAD_MODULE
             | DEBUG_EVENT_TYPE.UNLOAD_MODULE
             | DEBUG_EVENT_TYPE.CREATE_PROCESS
             | DEBUG_EVENT_TYPE.EXIT_PROCESS
             | DEBUG_EVENT_TYPE.SYSTEM_ERROR
             | DEBUG_EVENT_TYPE.SESSION_STATUS
             | DEBUG_EVENT_TYPE.CHANGE_ENGINE_STATE;
        return HRESULT.S_OK;
    }

    public override DEBUG_STATUS Breakpoint(IntPtr bp)
    {
        _hasBreakingEvent = true;
        _secondChancePending = false;

        string details = "Breakpoint hit";
        ulong? address = null;
        try
        {
            var breakpoint = new DebugBreakpoint(bp);
            var id = breakpoint.Id;
            var offset = breakpoint.Offset;
            details = $"Breakpoint {id} hit at 0x{offset:X16}";
            address = (ulong)offset;
        }
        catch { }

        _logger.LogInformation("DbgEng event: {Details}", details);
        Enqueue(new DebugEvent
        {
            Type = DebugEventKind.BreakpointHit,
            Details = details,
            Address = address
        });
        return DEBUG_STATUS.BREAK;
    }

    // A managed exception escaping a COM callback is swallowed by the CCW and the
    // event is silently lost, so every callback body is guarded.
    public override unsafe DEBUG_STATUS Exception(ref EXCEPTION_RECORD64 exception, int firstChance)
    {
        try
        {
            return ExceptionCore(ref exception, firstChance);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception callback failed");
            _hasBreakingEvent = true;
            Enqueue(new DebugEvent
            {
                Type = DebugEventKind.Error,
                Details = $"Exception callback failed ({ex.GetType().Name}: {ex.Message}); target halted for inspection"
            });
            return DEBUG_STATUS.BREAK;
        }
    }

    private unsafe DEBUG_STATUS ExceptionCore(ref EXCEPTION_RECORD64 exception, int firstChance)
    {
        var code = (uint)exception.ExceptionCode;
        _logger.LogInformation(
            "DbgEng event: Exception code=0x{Code:X8} firstChance={First} params={Params} info0=0x{Info0:X} at 0x{Addr:X16}",
            code, firstChance, exception.NumberParameters,
            exception.NumberParameters > 0 ? exception.ExceptionInformation[0] : 0,
            exception.ExceptionAddress);

        if (firstChance != 0)
        {
            _secondChancePending = false;
            if ((uint)exception.ExceptionCode == StatusBreakpoint)
            {
                // Every kernel break-in (kd_break, the pump's yield, a BSOD, a
                // driver's DbgBreakPoint) is a first-chance int 3. The reason is
                // not in the exception record — it is in RCX when the address is
                // nt!RtlpBreakWithStatusInstruction — so the manager classifies it
                // once WaitForEvent has returned. NO_CHANGE lets the engine's default
                // break-instruction handling halt the target.
                Interlocked.Exchange(ref _breakInAddress, exception.ExceptionAddress);
                _breakInPending = true;
                return DEBUG_STATUS.NO_CHANGE;
            }

            // The kernel only forwards first-chance exceptions to KD for a few
            // codes (NT_ASSERT 0xC0000420, WoW64 breakpoints/single-steps) unless
            // `!gflag +soe` is set; an unhandled driver fault arrives second-chance.
            // Pass them to the kernel (don't break, don't flag) but queue them as
            // informational so an NT_ASSERT — or, with +soe, any fault — is visible.
            // Single-step traps are the engine's own stepping.
            if (code != StatusSingleStep && code != StatusWow64Breakpoint && code != StatusWow64SingleStep)
            {
                Enqueue(new DebugEvent
                {
                    Type = DebugEventKind.ExceptionFirstChance,
                    Details = $"First-chance exception 0x{code:X8} at " +
                              $"0x{exception.ExceptionAddress:X16} (informational, kernel is handling it)",
                    Address = (ulong)exception.ExceptionAddress
                });
            }
            return DEBUG_STATUS.GO_NOT_HANDLED;
        }

        // Second-chance (unhandled) exception: the kernel found no handler. The
        // next kd_continue must pass it back unhandled (gn) so the kernel proceeds
        // to KeBugCheckEx; a plain GO re-executes the faulting instruction forever.
        _hasBreakingEvent = true;
        _secondChancePending = true;
        Enqueue(new DebugEvent
        {
            Type = DebugEventKind.ExceptionSecondChance,
            Details = $"Unhandled exception 0x{code:X8} at 0x{exception.ExceptionAddress:X16} (second chance). " +
                      "The kernel has no handler for it: kd_execute('k') to see the faulting stack; " +
                      "kd_continue passes it back unhandled, which bugchecks the OS",
            Address = (ulong)exception.ExceptionAddress
        });

        return DEBUG_STATUS.BREAK;
    }

    public override DEBUG_STATUS LoadModule(
        long imageFileHandle, long baseOffset, int moduleSize,
        string moduleName, string imageName, int checkSum, int timeDateStamp)
    {
        NoteModuleEvent();
        Enqueue(new DebugEvent
        {
            Type = DebugEventKind.ModuleLoaded,
            Details = $"Module loaded: {moduleName ?? imageName} at 0x{baseOffset:X16}",
            Address = (ulong)baseOffset
        });
        return DEBUG_STATUS.NO_CHANGE;
    }

    public override DEBUG_STATUS UnloadModule(string imageBaseName, long baseOffset)
    {
        NoteModuleEvent();
        Enqueue(new DebugEvent
        {
            Type = DebugEventKind.ModuleUnloaded,
            Details = $"Module unloaded: {imageBaseName} from 0x{baseOffset:X16}",
            Address = (ulong)baseOffset
        });
        return DEBUG_STATUS.NO_CHANGE;
    }

    public override DEBUG_STATUS CreateProcess(
        long imageFileHandle, long handle, long baseOffset, int moduleSize,
        string moduleName, string imageName, int checkSum, int timeDateStamp,
        long initialThreadHandle, long threadDataOffset, long startOffset)
    {
        Enqueue(new DebugEvent
        {
            Type = DebugEventKind.ProcessCreated,
            Details = $"Process created: {moduleName ?? imageName}"
        });
        return DEBUG_STATUS.NO_CHANGE;
    }

    public override DEBUG_STATUS ExitProcess(int exitCode)
    {
        Enqueue(new DebugEvent
        {
            Type = DebugEventKind.ProcessExited,
            Details = $"Process exited with code {exitCode}"
        });
        return DEBUG_STATUS.NO_CHANGE;
    }

    public override DEBUG_STATUS CreateThread(long handle, long dataOffset, long startOffset)
    {
        return DEBUG_STATUS.NO_CHANGE;
    }

    public override DEBUG_STATUS ExitThread(int exitCode)
    {
        return DEBUG_STATUS.NO_CHANGE;
    }

    public override DEBUG_STATUS SystemError(int error, int level)
    {
        _logger.LogInformation("DbgEng event: SystemError error=0x{Error:X8} level={Level}", error, level);
        _hasBreakingEvent = true;
        Enqueue(new DebugEvent
        {
            Type = DebugEventKind.SystemError,
            Details = $"System error: 0x{error:X8}, level {level}"
        });
        return DEBUG_STATUS.BREAK;
    }

    public override HRESULT SessionStatus(DEBUG_SESSION status)
    {
        _logger.LogInformation("DbgEng event: SessionStatus {Status}", status);
        if (status == DEBUG_SESSION.END || status == DEBUG_SESSION.FAILURE)
        {
            Enqueue(new DebugEvent
            {
                Type = DebugEventKind.SessionEnded,
                Details = status == DEBUG_SESSION.FAILURE
                    ? "Debug session failed — connection to the target was lost"
                    : "Debug session ended"
            });
        }
        else if (status == DEBUG_SESSION.REBOOT)
        {
            // The engine reconnects on its own after a kernel reboot and halts at
            // the initial breakpoint. Mark it as a breaking event so the pump stops.
            _rebootDetected = true;
            _hasBreakingEvent = true;
            var generation = Interlocked.Increment(ref _rebootGeneration);
            // Module/process/thread events of the kernel that just died are worthless
            // and would only bury the events that matter; keep the important ones.
            var discarded = DiscardQueuedInformationalEvents();
            Enqueue(new DebugEvent
            {
                Type = DebugEventKind.TargetRebooted,
                Details = $"Target rebooted (reboot #{generation} of this session; events from now on are tagged reboot#{generation}). " +
                          "Previous kernel state is gone; debugger reconnects at the initial breakpoint." +
                          (discarded > 0 ? $" ({discarded} informational module/process/thread events of the old kernel discarded.)" : "")
            });
        }
        return HRESULT.S_OK;
    }

    public override HRESULT ChangeDebuggeeState(DEBUG_CDS flags, long argument)
    {
        return HRESULT.S_OK;
    }

    public override HRESULT ChangeSymbolState(DEBUG_CSS flags, long argument)
    {
        return HRESULT.S_OK;
    }

    public override HRESULT ChangeEngineState(DEBUG_CES flags, long argument)
    {
        if ((flags & DEBUG_CES.EXECUTION_STATUS) != 0)
        {
            _lastExecutionStatus = (DEBUG_STATUS)argument;
            _logger.LogInformation("DbgEng event: ExecutionStatus -> {Status} (raw=0x{Raw:X})",
                _lastExecutionStatus, argument);
        }
        return HRESULT.S_OK;
    }

    /// <summary>
    /// Drain queued events (up to maxCount).
    /// </summary>
    public List<DebugEvent> DrainEvents(int maxCount = 50)
    {
        var events = new List<DebugEvent>();
        while (events.Count < maxCount && _eventQueue.TryDequeue(out var evt))
        {
            // The ring is the model's memory of what mattered; a post-boot flood of
            // module loads must not evict the bugcheck or reboot that preceded it.
            if (!IsInformational(evt.Type))
            {
                _recentEvents.Enqueue(evt);
                while (_recentEvents.Count > RecentEventCapacity)
                    _recentEvents.TryDequeue(out _);
            }
            events.Add(evt);
        }
        return events;
    }

    private int DiscardQueuedInformationalEvents()
    {
        var kept = new List<DebugEvent>();
        var dropped = 0;
        while (_eventQueue.TryDequeue(out var e))
        {
            if (IsInformational(e.Type)) dropped++;
            else kept.Add(e);
        }
        foreach (var e in kept)
            _eventQueue.Enqueue(e);
        return dropped;
    }

    public static bool IsInformational(DebugEventKind kind) => kind is
        DebugEventKind.ModuleLoaded or DebugEventKind.ModuleUnloaded or
        DebugEventKind.ProcessCreated or DebugEventKind.ProcessExited or
        DebugEventKind.ThreadCreated or DebugEventKind.ThreadExited;

    /// <summary>Last important events handed out by DrainEvents, oldest first.</summary>
    public List<DebugEvent> RecentEvents => _recentEvents.ToList();
}
