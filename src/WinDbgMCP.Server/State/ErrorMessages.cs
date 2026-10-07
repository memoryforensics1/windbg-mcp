namespace WinDbgMCP.Server.State;

/// <summary>
/// LLM-oriented error messages. Every message follows:
/// [WHAT HAPPENED] — [WHY IT HAPPENED] — [WHAT TO DO NEXT]
/// </summary>
public static class ErrorMessages
{
    // === VM State Errors ===
    public const string VmIsOff =
        "VM is powered off. Call vm_start to boot the VM before performing this operation.";

    public const string VmIsPaused =
        "VM is paused (via vm_pause). Call vm_resume to unpause, then retry.";

    public const string VmAlreadyRunning =
        "VM is already running. No action needed — you can proceed with other operations.";

    public const string VmNotOff =
        "VM is not powered off. Call vm_stop first if you need to start fresh.";

    // === Kernel Debug State Errors ===
    public const string KdNotConnected =
        "Kernel debugger is not connected. Call kd_connect to attach to the target VM's kernel.";

    public const string KdAlreadyConnected =
        "Kernel debugger is already connected. Call kd_disconnect first if you need to reconnect.";

    public const string TargetNotBroken =
        "Cannot inspect target — it is currently running freely. " +
        "Memory reads, register dumps, and stack traces require the target to be halted. " +
        "Call kd_break to halt the target, then retry.";

    public const string TargetAlreadyBroken =
        "Target is already halted at a breakpoint. " +
        "You can inspect state with kd_execute (e.g., 'k', 'r', 'db addr'), " +
        "or resume execution (kd_continue).";

    public const string WaitPending =
        "A previous step or continue operation has a pending WaitForEvent. " +
        "Call kd_wait_for_event to check if it completed, or kd_break to interrupt it.";

    // === Guest Operation Errors ===
    public const string GuestFrozenByKd =
        "VM is frozen — kernel debugger is at a breakpoint. " +
        "The entire guest OS is halted, so commands and file transfers will hang. " +
        "Call kd_continue to resume the target, wait 2-3 seconds for VMware Tools " +
        "to recover, then retry this guest operation.";

    public const string ToolsNotResponding =
        "VMware Tools is not responding inside the guest. Possible causes: " +
        "(1) VM is still booting — wait 10-30 seconds and retry. " +
        "(2) Guest OS crashed — check vm_screenshot. " +
        "(3) VMware Tools not installed — cannot execute guest operations without it. " +
        "Call get_system_state for current status.";

    // === Timeout Errors ===
    public static string OperationTimedOut(string operation, double seconds) =>
        $"{operation} timed out after {seconds}s. The operation may still be in progress. " +
        "Call get_system_state to check current status before retrying.";

    // === Connection Errors ===
    public const string KdConnectFailed =
        "Failed to connect kernel debugger. Verify: " +
        "(1) VM is running with debug boot configuration enabled. " +
        "(2) KDNET port/key or serial pipe name is correct. " +
        "(3) No other debugger is already attached to this target.";

    public const string SnapshotRestoredWarning =
        "Snapshot restored successfully. WARNING: All debug sessions have been invalidated. " +
        "Kernel debugger: disconnected. Frida sessions: terminated. dbgsrv: disconnected. " +
        "You must re-establish any debug sessions you need.";

    // === BSOD-Specific Errors ===
    public const string BsodRecoveryOptions =
        "Options: (1) kd_execute('!analyze -v') to analyze the crash. " +
        "(2) kd_continue to let the crash run its course: the kernel writes the dump and " +
        "reboots (some targets break in a second time first — then kd_wait_for_event shows " +
        "another Bugcheck event and kd_continue again); kd_wait_for_event / get_system_state " +
        "then show TARGET REBOOTED at the initial breakpoint, and a final kd_continue boots the OS. " +
        "(3) vm_snapshot_restore to revert to a clean state. " +
        "(4) vm_stop(hard=true) + vm_start if the target never comes back (auto-reboot disabled).";

    /// <summary>
    /// Halted at a second-chance exception. This is where a real driver crash stops
    /// first (e.g. an access violation that becomes bugcheck 0x3B/0x7E/0x1E): the
    /// kernel has not called KeBugCheckEx yet, so .bugcheck still reads zero, but the
    /// OS is already lost. The model must learn that here, not after a timeout.
    /// </summary>
    public static string FatalExceptionPending(string? lastEvent) =>
        $"FATAL EXCEPTION (second chance): {lastEvent ?? "unhandled exception"}. The kernel has no handler for " +
        "it, so the OS is effectively crashed: guest operations will NOT work, and the next kd_continue passes " +
        "the exception back (gn), which bugchecks the machine (then the usual BSOD sequence: dump, reboot, " +
        "initial breakpoint, kd_continue). Analyze NOW while the faulting context is intact: " +
        "kd_execute('!analyze -v') names the bugcheck it will become and the faulting driver; kd_execute('k') " +
        "shows the faulting stack; kd_execute('r') the registers. Or vm_snapshot_restore to revert.";

    public static string BsodGuestOpsUnavailable(string? bugcheckCode) =>
        $"BSOD DETECTED — Bugcheck {bugcheckCode ?? "unknown"}. " +
        "The guest OS has crashed. Guest operations will NOT work because " +
        "the OS is dead (not just paused). " + BsodRecoveryOptions;

    public static string BsodCannotBreak(string? bugcheckCode) =>
        $"BSOD — the target is already halted in the bugcheck handler " +
        $"(Bugcheck {bugcheckCode ?? "unknown"}); there is nothing to break into. " +
        BsodRecoveryOptions;

    public static string BsodContinueWarning(string? bugcheckCode) =>
        $"WARNING: target was halted at a BSOD (Bugcheck {bugcheckCode ?? "unknown"}). " +
        "Expected sequence now: the kernel runs bugcheck callbacks, writes the crash dump " +
        "(typically 30-60 s) and reboots. Call kd_wait_for_event(90): it returns TARGET REBOOTED " +
        "at the initial breakpoint, then kd_continue boots the OS (get_system_state shows the " +
        "state at any point). Some targets break in a SECOND " +
        "time before rebooting (another Bugcheck event, BSOD flagged again — that is normal, not a " +
        "failed continue): just kd_continue once more. If nothing happens for ~2 minutes the VM has " +
        "auto-reboot disabled and is halted for good: use vm_stop(hard=true) + vm_start, " +
        "or vm_snapshot_restore.";
}
