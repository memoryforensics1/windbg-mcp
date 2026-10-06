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

    // BSOD detection
    public bool IsBugcheck { get; set; }
    public string? BugcheckCode { get; set; }

    // Target rebooted since the last kd_continue; engine reconnected at the initial breakpoint
    public bool KdRebootDetected { get; set; }

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
/// Maps directly to DEBUG_STATUS_* constants from dbgeng.h.
/// </summary>
public enum DebugExecutionStatus
{
    NoDebuggee = 0,       // DEBUG_STATUS_NO_DEBUGGEE
    Go = 1,               // DEBUG_STATUS_GO
    StepInto = 2,         // DEBUG_STATUS_STEP_INTO
    StepOver = 3,         // DEBUG_STATUS_STEP_OVER
    StepBranch = 4,       // DEBUG_STATUS_STEP_BRANCH
    Break = 6,            // DEBUG_STATUS_BREAK
    GoHandled = 7,        // DEBUG_STATUS_GO_HANDLED
    GoNotHandled = 8,     // DEBUG_STATUS_GO_NOT_HANDLED
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
