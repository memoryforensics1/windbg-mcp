namespace WinDbgMCP.Server.KernelDebug.Models;

public sealed class DebugEvent
{
    public DebugEventKind Type { get; set; }
    public string Details { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public ulong? Address { get; set; }
    public uint? ProcessId { get; set; }
    public uint? ThreadId { get; set; }

    public override string ToString() =>
        $"[{Timestamp:HH:mm:ss.fff}] {Type}: {Details}";
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
