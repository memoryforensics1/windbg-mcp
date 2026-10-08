namespace WinDbgMCP.Server.KernelDebug.Models;

public sealed class DebugEvent
{
    public DebugEventKind Type { get; set; }
    public string Details { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public ulong? Address { get; set; }
    public uint? ProcessId { get; set; }
    public uint? ThreadId { get; set; }

    /// <summary>
    /// Reboot generation the event belongs to: 0 = the kernel the debugger first
    /// attached to, N = the kernel running after the N-th reboot of this session.
    /// Lets the model tell at a glance which events belong to the current kernel.
    /// </summary>
    public int Generation { get; set; }

    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss.fff}{(Generation > 0 ? $" reboot#{Generation}" : "")}] {Type}: {Details}";
}

public enum DebugEventKind
{
    BreakpointHit,
    ExceptionFirstChance,
    ExceptionSecondChance,
    ModuleLoaded,
    ModuleUnloaded,
    ProcessCreated,
    ProcessExited,
    ThreadCreated,
    ThreadExited,
    BreakIn,
    Bugcheck,
    SystemError,
    SessionEnded,
    TargetRebooted,
    Error
}
