using System.Collections.Concurrent;
using ClrDebug;
using ClrDebug.DbgEng;
using WinDbgMCP.Server.KernelDebug.Models;

namespace WinDbgMCP.Server.KernelDebug;

/// <summary>
/// Handles debug events from the kernel debug target.
/// Events are queued for consumption by the MCP tools.
/// </summary>
public sealed class DebugEventCallbacks : DebugBaseEventCallbacks
{
    private readonly ConcurrentQueue<DebugEvent> _eventQueue = new();
    private volatile DEBUG_STATUS _lastExecutionStatus = DEBUG_STATUS.NO_DEBUGGEE;
    private volatile bool _hasBreakingEvent;

    public int PendingCount => _eventQueue.Count;
    public DEBUG_STATUS LastExecutionStatus => _lastExecutionStatus;

    /// <summary>
    /// True if a breaking event (breakpoint, exception, system error) occurred
    /// since the last ClearBreakingEventFlag call. Used by the pump to distinguish
    /// real events from yield interrupts.
    /// </summary>
    public bool HasBreakingEvent => _hasBreakingEvent;

    public void ClearBreakingEventFlag() => _hasBreakingEvent = false;

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

        _eventQueue.Enqueue(new DebugEvent
        {
            Type = DebugEventKind.BreakpointHit,
            Details = details,
            Address = address
        });
        return DEBUG_STATUS.BREAK;
    }

    public override DEBUG_STATUS Exception(ref EXCEPTION_RECORD64 exception, int firstChance)
    {
        if (firstChance != 0)
        {
            // First-chance exceptions are routine in a running kernel.
            // Let the kernel handle them — don't break or set the flag.
            // The engine will continue waiting for the next event.
            return DEBUG_STATUS.GO_NOT_HANDLED;
        }

        // Second-chance (unhandled) exception — this is a real crash/BSOD.
        _hasBreakingEvent = true;
        _eventQueue.Enqueue(new DebugEvent
        {
            Type = DebugEventKind.ExceptionSecondChance,
            Details = $"Exception 0x{exception.ExceptionCode:X8} at 0x{exception.ExceptionAddress:X16} " +
                      "(second chance)",
            Address = (ulong)exception.ExceptionAddress
        });

        return DEBUG_STATUS.BREAK;
    }

    public override DEBUG_STATUS LoadModule(
        long imageFileHandle, long baseOffset, int moduleSize,
        string moduleName, string imageName, int checkSum, int timeDateStamp)
    {
        _eventQueue.Enqueue(new DebugEvent
        {
            Type = DebugEventKind.ModuleLoaded,
            Details = $"Module loaded: {moduleName ?? imageName} at 0x{baseOffset:X16}",
            Address = (ulong)baseOffset
        });
        return DEBUG_STATUS.NO_CHANGE;
    }

    public override DEBUG_STATUS UnloadModule(string imageBaseName, long baseOffset)
    {
        _eventQueue.Enqueue(new DebugEvent
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
        _eventQueue.Enqueue(new DebugEvent
        {
            Type = DebugEventKind.ProcessCreated,
            Details = $"Process created: {moduleName ?? imageName}"
        });
        return DEBUG_STATUS.NO_CHANGE;
    }

    public override DEBUG_STATUS ExitProcess(int exitCode)
    {
        _eventQueue.Enqueue(new DebugEvent
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
        _hasBreakingEvent = true;
        _eventQueue.Enqueue(new DebugEvent
        {
            Type = DebugEventKind.SystemError,
            Details = $"System error: 0x{error:X8}, level {level}"
        });
        return DEBUG_STATUS.BREAK;
    }

    public override HRESULT SessionStatus(DEBUG_SESSION status)
    {
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
            events.Add(evt);
        }
        return events;
    }
}
