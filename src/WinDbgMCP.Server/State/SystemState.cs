namespace WinDbgMCP.Server.State;

/// <summary>
/// The authoritative system state model.
/// Maintained by StateCoordinator. Queried by every tool via precondition checks.
/// </summary>
public sealed class SystemState
{
    // === VM Layer ===
    public VmPowerState VmPower { get; set; } = VmPowerState.Unknown;
    public VmToolsState VmTools { get; set; } = VmToolsState.Unknown;
    public string? VmIpAddress { get; set; }
    public string VmxPath { get; set; } = string.Empty;

    // === Kernel Debug Layer ===
    public bool KdConnected { get; set; }
    public KdTransport KdTransportType { get; set; } = KdTransport.None;
    public DebugExecutionStatus KdExecStatus { get; set; } = DebugExecutionStatus.Uninitialized;
    public string? KdBreakReason { get; set; }
    public bool KdWaitPending { get; set; }
    public int PendingEventCount { get; set; }
    // Of PendingEventCount, how many are module/process/thread notifications
    // (informational, never a reason the target stopped).
    public int PendingInformationalEventCount { get; set; }
    public int PendingImportantEventCount => Math.Max(0, PendingEventCount - PendingInformationalEventCount);

    // Module load/unload events in the last 10 s. A burst (>= 5) means the kernel
    // is booting or loading drivers in bulk, and break-ins are dropped meanwhile.
    public int KdModuleEventsLast10s { get; set; }
    public bool KdModuleFlood => KdModuleEventsLast10s >= 5;

    // BSOD detection
    public bool IsBugcheck { get; set; }
    public string? BugcheckCode { get; set; }

    // Halted at a second-chance (unhandled) exception: the kernel has not entered
    // KeBugCheckEx yet (so IsBugcheck is false) but the next resume bugchecks the OS.
    public bool KdFatalExceptionPending { get; set; }

    // A kd_execute that outlived its timeout is still running on the engine thread
    // (e.g. "!analyze -v, running for 95 s"); every other kernel tool waits behind it.
    public string? KdEngineBusyWith { get; set; }

    // Target rebooted since the last kd_continue; engine reconnected at the initial breakpoint
    public bool KdRebootDetected { get; set; }
    // Reboots since kd_connect; debug events are tagged "reboot#N" with the generation they belong to
    public int KdRebootGeneration { get; set; }

    // === Guest Exec Layer ===
    /// <summary>
    /// Derived: VmPower==Running AND VmTools==Running AND KdExecStatus!=Break
    /// </summary>
    public bool GuestOpsAvailable { get; set; }
    public int ActiveTransfers { get; set; }

    // === User-Mode Debug Layer ===
    public FridaSessionState? FridaState { get; set; }
    public DbgsrvSessionState? DbgsrvState { get; set; }
    public List<ActiveDebugSession> UserDebugSessions { get; set; } = new();
}

public enum VmPowerState
{
    Off,
    Running,
    Paused,
    Suspended,
    Unknown
}

public enum VmToolsState
{
    NotInstalled,
    Running,
    NotResponding,
    Unknown
}

public enum KdTransport
{
    None,
    KDNET,
    Serial
}

/// <summary>
/// Maps directly to DEBUG_STATUS_* constants from dbgeng.h (verified against
/// the engine: NO_DEBUGGEE is 7, not 0).
/// </summary>
public enum DebugExecutionStatus
{
    NoChange = 0,         // DEBUG_STATUS_NO_CHANGE
    Go = 1,               // DEBUG_STATUS_GO
    GoHandled = 2,        // DEBUG_STATUS_GO_HANDLED
    GoNotHandled = 3,     // DEBUG_STATUS_GO_NOT_HANDLED
    StepOver = 4,         // DEBUG_STATUS_STEP_OVER
    StepInto = 5,         // DEBUG_STATUS_STEP_INTO
    Break = 6,            // DEBUG_STATUS_BREAK
    NoDebuggee = 7,       // DEBUG_STATUS_NO_DEBUGGEE
    StepBranch = 8,       // DEBUG_STATUS_STEP_BRANCH
    IgnoreEvent = 9,      // DEBUG_STATUS_IGNORE_EVENT
    RestartRequested = 10,// DEBUG_STATUS_RESTART_REQUESTED
    Uninitialized = -1    // Our own: DbgEng not loaded yet
}

public sealed class FridaSessionState
{
    public bool Connected { get; set; }
    public int? AttachedPid { get; set; }
    public string? ProcessName { get; set; }

    public override string ToString() =>
        Connected ? $"Attached to PID {AttachedPid} ({ProcessName})" : "Disconnected";
}

public sealed class DbgsrvSessionState
{
    public bool Connected { get; set; }
    public int? AttachedPid { get; set; }

    public override string ToString() =>
        Connected ? $"Connected, attached to PID {AttachedPid}" : "Disconnected";
}

public sealed class ActiveDebugSession
{
    public string Type { get; set; } = string.Empty; // "frida", "dbgsrv", "x64dbg"
    public int Pid { get; set; }
    public string ProcessName { get; set; } = string.Empty;
}
