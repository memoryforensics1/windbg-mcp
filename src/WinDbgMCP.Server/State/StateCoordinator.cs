using System.Text;
using Microsoft.Extensions.Logging;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.KernelDebug.Models;

namespace WinDbgMCP.Server.State;

/// <summary>
/// The heart of the system. Maintains authoritative system state,
/// validates preconditions for EVERY tool call, and returns LLM-friendly errors.
/// </summary>
public sealed class StateCoordinator
{
    private readonly ServerConfig _config;
    private readonly ILogger<StateCoordinator> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private SystemState _state = new();

    // Refresh throttling
    private DateTime _lastVmStateRefresh = DateTime.MinValue;
    private DateTime _lastToolsRefresh = DateTime.MinValue;

    // BSOD detection — only check once per break-in, not every refresh
    private bool _bsodCheckedForCurrentBreak;

    // State transitions observed since the last tool result was produced.
    // Flushed into the banner RunToolAsync prepends to every tool result.
    private readonly List<string> _alerts = new();
    private readonly List<DebugEvent> _pendingEvents = new();

    // Public read-only accessor for state
    public SystemState State => _state;

    public Func<List<DebugEvent>>? DrainDebugEvents { get; set; }
    public Func<List<DebugEvent>>? GetRecentDebugEvents { get; set; }

    // These will be set when the managers are created
    // Using Func<> delegates to avoid circular dependencies during construction
    public Func<Task<VmPowerState>>? GetVmPowerStateAsync { get; set; }
    public Func<TimeSpan, Task<bool>>? AreToolsRunningAsync { get; set; }
    public Func<DebugExecutionStatus>? GetDbgEngExecutionStatus { get; set; }
    public Func<bool>? IsDbgEngConnected { get; set; }
    /// <summary>True while the engine thread is parked in a target-less wait that did not answer a probe.</summary>
    public Func<bool>? IsEngineParked { get; set; }
    /// <summary>Describes the engine command still running past its tool timeout, or null when idle.</summary>
    public Func<string?>? GetEngineBusy { get; set; }
    public Func<int>? GetPendingEventCount { get; set; }
    public Func<int>? GetPendingInformationalEventCount { get; set; }
    public Func<int>? GetModuleEventsLast10s { get; set; }
    public Func<int>? GetRebootGeneration { get; set; }
    public Func<Task<(bool IsBugcheck, string? BugcheckCode, string? BugcheckArgs, string? LastEvent)>>? DetectBugcheckAsync { get; set; }
    /// <summary>True while the current halt is a second-chance exception (the next resume bugchecks).</summary>
    public Func<bool>? IsSecondChancePending { get; set; }
    public Func<bool>? IsRebootDetected { get; set; }

    // User-mode debug state delegates
    public Func<bool>? IsFridaAttached { get; set; }
    public Func<string?>? GetFridaTargetName { get; set; }
    public Func<bool>? IsDbgsrvConnected { get; set; }
    public Func<uint?>? GetDbgsrvAttachedPid { get; set; }

    // Cleanup delegates for snapshot restore
    public Action? CleanupKdSession { get; set; }
    public Action? CleanupFridaSession { get; set; }
    public Action? CleanupDbgsrvSession { get; set; }

    public StateCoordinator(ServerConfig config, ILogger<StateCoordinator> logger)
    {
        _config = config;
        _logger = logger;
        _state.VmxPath = config.Vm.VmxPath;
    }

    /// <summary>
    /// Runs a tool: precondition check, body, then a post-call refresh so every
    /// result carries a "since your last call" banner with state transitions and
    /// debug events that happened in between. This is the only channel through
    /// which the LLM learns about asynchronous target events, so no tool bypasses it.
    /// </summary>
    public async Task<string> RunToolAsync(string toolName, Func<Task<string>> body)
    {
        var precheck = await ValidatePreconditionsAsync(toolName);

        // Drain now as well as after the body: snapshot restore / target switch /
        // reconnect clear the engine queue, and events queued before them must
        // still reach the banner.
        await CollectDebugEventsAsync();

        string result;
        var executed = precheck == null || precheck.IsSuccess;
        if (!executed)
        {
            result = $"NOT EXECUTED: '{toolName}' was refused by the state gate. {precheck!.Message}";
        }
        else
        {
            try
            {
                result = await body();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Tool {Tool} threw", toolName);
                result = $"{toolName} failed: {ex.GetType().Name}: {ex.Message}";
            }
            // A precheck warning ("...Proceeding.") must not prefix a body that
            // decided not to proceed after all.
            if (precheck != null && !result.StartsWith("NOT EXECUTED", StringComparison.Ordinal))
                result = precheck.Message + " " + result;
        }

        return await PrependAlertsAsync(result, executed);
    }

    private async Task CollectDebugEventsAsync()
    {
        await _lock.WaitAsync();
        try
        {
            DrainIntoPending();
        }
        finally
        {
            _lock.Release();
        }
    }

    // Must hold _lock
    private void DrainIntoPending()
    {
        try
        {
            var drained = DrainDebugEvents?.Invoke();
            if (drained != null)
                _pendingEvents.AddRange(drained);
            _state.PendingEventCount = GetPendingEventCount?.Invoke() ?? 0;
            _state.PendingInformationalEventCount = GetPendingInformationalEventCount?.Invoke() ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Draining debug events failed");
        }
    }

    private async Task<string> PrependAlertsAsync(string result, bool executed)
    {
        await _lock.WaitAsync();
        try
        {
            try
            {
                await RefreshStateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Post-tool state refresh failed");
            }

            DrainIntoPending();
            var banner = BuildAlertBanner(executed);
            return banner == null ? result : banner + "\n" + result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static readonly HashSet<DebugEventKind> ImportantEventKinds = new()
    {
        DebugEventKind.Bugcheck,
        DebugEventKind.BreakpointHit,
        DebugEventKind.BreakIn,
        DebugEventKind.ExceptionSecondChance,
        DebugEventKind.SystemError,
        DebugEventKind.TargetRebooted,
        DebugEventKind.SessionEnded,
        DebugEventKind.Error,
    };

    // Must hold _lock
    private string? BuildAlertBanner(bool executed)
    {
        var events = _pendingEvents.ToList();
        _pendingEvents.Clear();

        var important = events.Where(e => ImportantEventKinds.Contains(e.Type)).ToList();
        var firstChance = events.Where(e => e.Type == DebugEventKind.ExceptionFirstChance).ToList();
        var other = events.Count - important.Count - firstChance.Count;

        if (_alerts.Count == 0 && important.Count == 0 && firstChance.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.AppendLine("!!! SINCE YOUR LAST CALL !!!");
        foreach (var alert in _alerts)
            sb.AppendLine($"- {alert}");
        foreach (var evt in important.Take(10))
            sb.AppendLine($"- Event {evt}");
        if (important.Count > 10)
            sb.AppendLine($"- ... and {important.Count - 10} more events");
        foreach (var evt in firstChance.Take(3))
            sb.AppendLine($"- {evt}");
        if (firstChance.Count > 3)
            sb.AppendLine($"- ... and {firstChance.Count - 3} more first-chance exceptions (informational)");
        if (other > 0)
            sb.AppendLine($"- {other} module/process/thread events (informational)");
        var relevantChange = _alerts.Count > 0 || important.Count > 0;
        sb.AppendLine((executed, relevantChange) switch
        {
            (true, _) => "- The call below ran after or during the above; read its result in that light.",
            (false, true) => "- The call below was NOT executed, most likely because the state changed before it ran and its preconditions no longer hold — see the reason in its result.",
            (false, false) => "- The call below was NOT executed; see the reason in its result (unrelated to the informational events above).",
        });
        sb.Append("!!! END !!!");

        _alerts.Clear();
        return sb.ToString();
    }

    /// <summary>
    /// Called BEFORE every MCP tool execution.
    /// Returns null if preconditions are met, or a ToolResult with an error message if not.
    /// </summary>
    public async Task<ToolResult?> ValidatePreconditionsAsync(string toolName)
    {
        await _lock.WaitAsync();
        try
        {
            await RefreshStateAsync();

            return toolName switch
            {
                // --- VM tools ---
                "vm_start" => RequireVmOff(),
                "vm_stop" => RequireVmNotOff(warnIfKdAttached: true),
                "vm_pause" => RequireVmRunning(warnIfKdAttached: true),
                "vm_resume" => RequireVmPaused(),
                "vm_snapshot_restore" => null, // Always allowed (but resets everything)
                "vm_set_target" => null,       // Always allowed (resets everything)
                "vm_screenshot" => RequireVmNotOff(),
                "vm_snapshot_list" => null, // Always allowed

                // --- Kernel debug tools ---
                "kd_connect" => RequireVmRunning_KdNotConnected(),
                "kd_disconnect" => RequireKdConnected(),
                "kd_break" => RequireKdConnected_TargetRunning(),
                "kd_continue" => RequireKdConnected_TargetBroken_CanResume(),
                "kd_step" => RequireKdConnected_TargetBroken_NoWaitPending(),
                "kd_execute" => RequireKdConnected_TargetBroken(),
                "kd_wait_for_event" => RequireKdConnected_TargetReachable(),

                // --- Guest tools ---
                "guest_run_command" => RequireGuestOpsAvailable(),
                "guest_transfer_to_vm" => RequireGuestOpsAvailable(),
                "guest_transfer_from_vm" => RequireGuestOpsAvailable(),
                "guest_list_processes" => RequireGuestOpsAvailable(),
                "guest_kill_process" => RequireGuestOpsAvailable(),

                // --- User-mode debug tools ---
                "umd_frida_attach" => RequireGuestOpsAvailable(),
                "umd_frida" => RequireGuestOpsAvailable(),
                "umd_dbgsrv_connect" => RequireGuestOpsAvailable(),
                "umd_dbgsrv_execute" => RequireDbgsrvConnected(),
                "umd_ttd" => RequireGuestOpsAvailable(),
                "umd_ttd_query" => null, // Operates on host-side trace files

                // --- Meta tools ---
                "get_system_state" => null, // ALWAYS allowed

                _ => null // Unknown tools pass through
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Refresh state from underlying systems. Must be FAST.
    /// Called before every precondition check.
    /// </summary>
    public async Task RefreshStateAsync()
    {
        // 1. DbgEng execution status — single COM call, ~microseconds
        if (_state.KdConnected && IsDbgEngConnected?.Invoke() == true)
        {
            var status = GetDbgEngExecutionStatus?.Invoke() ?? DebugExecutionStatus.Uninitialized;
            _state.KdExecStatus = status;
            var rebooted = IsRebootDetected?.Invoke() ?? false;

            // If DbgEng reports NoDebuggee but we thought we were connected,
            // the connection was lost (snapshot restored, session ended, etc.).
            // During a reboot the engine is reconnecting on its own, so don't
            // mark the session lost — kd_connect would orphan the live client.
            if (status == DebugExecutionStatus.NoDebuggee && !rebooted)
            {
                _logger.LogWarning("Kernel debugger connection lost (NoDebuggee detected)");
                _alerts.Add("KERNEL DEBUGGER CONNECTION LOST: the engine reports no debuggee. " +
                            "Call kd_connect to reattach.");
                _state.KdConnected = false;
                _state.KdBreakReason = null;
                _state.IsBugcheck = false;
                _state.BugcheckCode = null;
            }

            // 1.5 Reboot detection — the old kernel (and any BSOD it was in) is gone,
            // so the new break must be re-evaluated from scratch.
            if (rebooted && !_state.KdRebootDetected)
            {
                _logger.LogWarning("Target reboot detected; kernel state reset");
                _alerts.Add("TARGET REBOOTED: the kernel restarted; previous state (and any BSOD) is gone. " +
                            "get_system_state shows whether it is at the initial breakpoint (then kd_continue).");
                _state.IsBugcheck = false;
                _state.BugcheckCode = null;
                _state.KdBreakReason = "Target rebooted";
                _bsodCheckedForCurrentBreak = false;
            }
            else if (!rebooted && _state.KdRebootDetected)
            {
                _state.KdBreakReason = null;
            }
            _state.KdRebootDetected = rebooted;
            _state.KdRebootGeneration = GetRebootGeneration?.Invoke() ?? 0;
        }
        else if (_state.KdConnected && IsDbgEngConnected?.Invoke() == false)
        {
            // The engine dropped its client after the tool that was detaching gave
            // up (its work item finished past the deadline). Reconcile, and say so.
            _logger.LogWarning("Kernel debugger client is gone while the state said connected; reconciling");
            _alerts.Add("KERNEL DEBUGGER DETACHED: the engine released its session after the last call " +
                        "reported a timeout. Call kd_connect if you need the debugger.");
            _state.KdConnected = false;
            _state.KdExecStatus = DebugExecutionStatus.NoDebuggee;
            _state.KdBreakReason = null;
            _state.IsBugcheck = false;
            _state.BugcheckCode = null;
            _state.KdRebootDetected = false;
        }

        // 2. Event queue count
        _state.PendingEventCount = GetPendingEventCount?.Invoke() ?? 0;
        _state.PendingInformationalEventCount = GetPendingInformationalEventCount?.Invoke() ?? 0;
        _state.KdModuleEventsLast10s = _state.KdConnected ? GetModuleEventsLast10s?.Invoke() ?? 0 : 0;
        _state.KdEngineBusyWith = _state.KdConnected ? GetEngineBusy?.Invoke() : null;

        // 2.5 BSOD detection — check once when transitioning INTO break state.
        // Skipped if a tool already flagged the bugcheck. A failed check (e.g. the
        // DbgEng thread was busy and the 5s timeout hit) is retried on the next refresh.
        if (_state.KdConnected && _state.KdExecStatus == DebugExecutionStatus.Break
            && !_bsodCheckedForCurrentBreak && !_state.IsBugcheck)
        {
            if (DetectBugcheckAsync == null)
            {
                _bsodCheckedForCurrentBreak = true;
            }
            else
            {
                try
                {
                    var (isBugcheck, bugcheckCode, bugcheckArgs, lastEvent) = await DetectBugcheckAsync();
                    _bsodCheckedForCurrentBreak = true;
                    if (lastEvent != null && !_state.KdRebootDetected)
                        _state.KdBreakReason = lastEvent;

                    var fatal = IsSecondChancePending?.Invoke() ?? false;
                    _state.KdFatalExceptionPending = fatal && !isBugcheck;

                    if (isBugcheck)
                    {
                        _state.IsBugcheck = true;
                        _state.BugcheckCode = bugcheckCode;
                        _state.BugcheckArgs = bugcheckArgs;
                        _logger.LogWarning("BSOD detected during state refresh: {Code} {Args}", bugcheckCode, bugcheckArgs);
                        _alerts.Add($"BSOD DETECTED (bugcheck {bugcheckCode}" +
                                    (bugcheckArgs != null ? $", arguments {bugcheckArgs}" : "") +
                                    "): the guest OS has crashed and is halted in the debugger. Guest operations will not work. " +
                                    ErrorMessages.BsodRecoveryOptions);
                    }
                    else if (_state.KdFatalExceptionPending)
                    {
                        _logger.LogWarning("Fatal (second-chance) exception pending: {Event}", lastEvent);
                        _alerts.Add(ErrorMessages.FatalExceptionPending(lastEvent));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "BSOD detection failed during state refresh; will retry");
                }
            }
        }
        else if (_state.KdExecStatus != DebugExecutionStatus.Break)
        {
            _state.IsBugcheck = false;
            _state.BugcheckCode = null;
            _state.KdFatalExceptionPending = false;
            if (!_state.KdRebootDetected)
                _state.KdBreakReason = null;
            _bsodCheckedForCurrentBreak = false;
        }

        // 2.7 User-mode debug state
        if (IsFridaAttached?.Invoke() == true)
        {
            _state.FridaState = new FridaSessionState
            {
                Connected = true,
                AttachedPid = null, // Frida tracks by name primarily
                ProcessName = GetFridaTargetName?.Invoke()
            };
        }
        else
        {
            _state.FridaState = null;
        }

        if (IsDbgsrvConnected?.Invoke() == true)
        {
            var pid = GetDbgsrvAttachedPid?.Invoke();
            _state.DbgsrvState = new DbgsrvSessionState
            {
                Connected = true,
                AttachedPid = pid.HasValue ? (int)pid.Value : null
            };
        }
        else
        {
            _state.DbgsrvState = null;
        }

        // 3. VM power state — only refresh if stale (>2 seconds old)
        // Skip refresh if state is Paused — vmrun list can't distinguish paused
        // from running, so we'd overwrite the manually-tracked Paused state.
        if (_state.VmPower != VmPowerState.Paused &&
            DateTime.UtcNow - _lastVmStateRefresh > TimeSpan.FromSeconds(2))
        {
            if (GetVmPowerStateAsync != null)
            {
                try
                {
                    var previous = _state.VmPower;
                    _state.VmPower = await GetVmPowerStateAsync();
                    if (previous != VmPowerState.Unknown && previous != _state.VmPower)
                        _alerts.Add($"VM POWER STATE CHANGED: {previous} -> {_state.VmPower}.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to refresh VM power state");
                    // Keep last known state
                }
            }
            _lastVmStateRefresh = DateTime.UtcNow;
        }

        // 4. Tools status — only if VM is running and not kernel-broken
        if (_state.VmPower == VmPowerState.Running &&
            _state.KdExecStatus != DebugExecutionStatus.Break &&
            DateTime.UtcNow - _lastToolsRefresh > TimeSpan.FromSeconds(5))
        {
            if (AreToolsRunningAsync != null)
            {
                try
                {
                    var toolsTimeout = TimeSpan.FromSeconds(_config.Timeouts.VmToolsCheckSeconds);
                    var previousTools = _state.VmTools;
                    _state.VmTools = await AreToolsRunningAsync(toolsTimeout)
                        ? VmToolsState.Running
                        : VmToolsState.NotResponding;
                    if (previousTools == VmToolsState.Running && _state.VmTools == VmToolsState.NotResponding)
                        _alerts.Add("VMWARE TOOLS STOPPED RESPONDING: the guest may be rebooting, hung, or crashed. " +
                                    "Guest operations will fail until it recovers; check get_system_state / vm_screenshot.");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to check VMware Tools status");
                    _state.VmTools = VmToolsState.NotResponding;
                }
            }
            _lastToolsRefresh = DateTime.UtcNow;
        }

        // 5. Derive compound states
        _state.GuestOpsAvailable =
            _state.VmPower == VmPowerState.Running &&
            _state.VmTools == VmToolsState.Running &&
            (!_state.KdConnected || _state.KdExecStatus != DebugExecutionStatus.Break);
    }

    /// <summary>
    /// Force a full state reset (e.g., after snapshot restore or VM target switch).
    /// </summary>
    /// <param name="vmPowerState">Actual VM power state after the reset. Defaults to Running.</param>
    /// <param name="vmxPath">New VMX path if the target VM changed. Defaults to config value.</param>
    public void ResetAllState(VmPowerState vmPowerState = VmPowerState.Running, string? vmxPath = null)
    {
        // Clean up active sessions before resetting state
        try { CleanupKdSession?.Invoke(); } catch { }
        try { CleanupFridaSession?.Invoke(); } catch { }
        try { CleanupDbgsrvSession?.Invoke(); } catch { }

        _state = new SystemState
        {
            VmxPath = vmxPath ?? _config.Vm.VmxPath,
            VmPower = vmPowerState,
            VmTools = VmToolsState.Unknown, // Need to re-probe
            KdConnected = false,
            KdExecStatus = DebugExecutionStatus.NoDebuggee,
            KdWaitPending = false,
            PendingEventCount = 0,
            FridaState = null,
            DbgsrvState = null
        };
        _bsodCheckedForCurrentBreak = false;
        _lastVmStateRefresh = DateTime.MinValue;
        _lastToolsRefresh = DateTime.MinValue;
    }

    /// <summary>
    /// Update KD connection state after successful connect.
    /// </summary>
    public void SetKdConnected(KdTransport transport)
    {
        _state.KdConnected = true;
        _state.KdTransportType = transport;
        _state.KdExecStatus = DebugExecutionStatus.Break; // After connect, target is at initial breakpoint
    }

    /// <summary>
    /// Update KD connection state after disconnect.
    /// </summary>
    public void SetKdDisconnected()
    {
        _state.KdConnected = false;
        _state.KdTransportType = KdTransport.None;
        _state.KdExecStatus = DebugExecutionStatus.NoDebuggee;
        _state.KdBreakReason = null;
        _state.KdWaitPending = false;
        _state.IsBugcheck = false;
        _state.BugcheckCode = null;
        _state.KdRebootDetected = false;
        // A reconnect to a still-crashed target must probe again
        _bsodCheckedForCurrentBreak = false;
    }

    /// <summary>
    /// Record a power transition the tool itself caused (vm_start / vm_stop) so the
    /// post-call refresh doesn't report it as an unexpected change, and forget the
    /// VMware Tools status, which is meaningless across a power cycle.
    /// </summary>
    public void SetVmPowerChangedByTool(VmPowerState newState)
    {
        _state.VmPower = newState;
        _state.VmTools = VmToolsState.Unknown;
        _lastVmStateRefresh = DateTime.UtcNow;
        _lastToolsRefresh = DateTime.MinValue;
    }

    /// <summary>
    /// Mark the current break as already probed for a bugcheck (by kd_break /
    /// kd_wait_for_event) so the post-call refresh doesn't probe again.
    /// </summary>
    public void SetBsodProbed() => _bsodCheckedForCurrentBreak = true;

    /// <summary>
    /// A tool that ran the break probe itself (kd_break, kd_wait_for_event) hands
    /// over what it learned, so the refresh (which it just pre-empted) still has
    /// the Break Reason and the fatal-exception flag to show.
    /// </summary>
    public void SetBsodProbed(string? lastEvent, bool fatalExceptionPending)
    {
        _bsodCheckedForCurrentBreak = true;
        if (lastEvent != null && !_state.KdRebootDetected)
            _state.KdBreakReason = lastEvent;
        _state.KdFatalExceptionPending = fatalExceptionPending;
        if (fatalExceptionPending)
            _alerts.Add(ErrorMessages.FatalExceptionPending(lastEvent));
    }

    /// <summary>
    /// Update VM power state after a successful pause.
    /// </summary>
    public void SetVmPaused()
    {
        _state.VmPower = VmPowerState.Paused;
        _state.GuestOpsAvailable = false;
        _lastVmStateRefresh = DateTime.UtcNow; // Prevent immediate overwrite by refresh
    }

    /// <summary>
    /// Update VM power state after a successful resume from pause.
    /// </summary>
    public void SetVmResumed()
    {
        _state.VmPower = VmPowerState.Running;
        _lastVmStateRefresh = DateTime.UtcNow; // Prevent immediate overwrite by refresh
    }

    /// <summary>
    /// Mark that a BSOD/bugcheck was detected.
    /// </summary>
    public void SetBsodDetected(string? bugcheckCode, string? bugcheckArgs = null)
    {
        _state.IsBugcheck = true;
        _state.BugcheckCode = bugcheckCode;
        _state.BugcheckArgs = bugcheckArgs;
    }

    // ═══════════════════════════════════════════════════════════════
    //  PRECONDITION CHECK IMPLEMENTATIONS
    // ═══════════════════════════════════════════════════════════════

    private ToolResult? RequireVmOff()
    {
        return _state.VmPower switch
        {
            VmPowerState.Off => null,
            VmPowerState.Running => ToolResult.Error(ErrorMessages.VmAlreadyRunning),
            VmPowerState.Paused => ToolResult.Error("VM is Paused. Call vm_resume to continue it (or vm_stop then vm_start)."),
            VmPowerState.Unknown => ToolResult.Error(ErrorMessages.VmPowerUnknown),
            _ => ToolResult.Error($"VM is {_state.VmPower}. Call vm_stop first, then vm_start."),
        };
    }

    private ToolResult? RequireVmNotOff(bool warnIfKdAttached = false)
    {
        if (_state.VmPower == VmPowerState.Off)
            return ToolResult.Error(ErrorMessages.VmIsOff);
        if (warnIfKdAttached && _state.KdConnected)
            return ToolResult.Success(
                "NOTE: the kernel debugger is attached; it is detached first (the target is resumed), then the VM is stopped. Proceeding.");
        return null;
    }

    /// <summary>The VM power state in words the caller can act on.</summary>
    private ToolResult? VmNotRunning()
    {
        return _state.VmPower switch
        {
            VmPowerState.Running => null,
            VmPowerState.Paused => ToolResult.Error(ErrorMessages.VmIsPaused),
            VmPowerState.Off => ToolResult.Error(ErrorMessages.VmIsOff),
            VmPowerState.Unknown => ToolResult.Error(ErrorMessages.VmPowerUnknown),
            _ => ToolResult.Error($"VM is {_state.VmPower}. Start the VM first with vm_start."),
        };
    }

    private ToolResult? RequireVmRunning(bool warnIfKdAttached = false)
    {
        var notRunning = VmNotRunning();
        if (notRunning != null)
            return notRunning;
        if (warnIfKdAttached && _state.KdConnected)
            return ToolResult.Success(
                "NOTE: the kernel debugger stays attached to a paused VM, but a paused kernel cannot answer it: " +
                "every kd_* call will time out until vm_resume. Proceeding.");
        return null;
    }

    /// <summary>
    /// For every tool that needs the kernel to answer: the VM must be running and
    /// the engine must have a debuggee. Returns the reason it cannot, else null.
    /// </summary>
    private ToolResult? KdTargetUnreachable()
    {
        if (_state.VmPower == VmPowerState.Paused)
            return ToolResult.Error(ErrorMessages.VmPausedKdAttached);
        if (_state.VmPower == VmPowerState.Off)
            return ToolResult.Error(ErrorMessages.VmOffKdAttached);
        if (_state.KdExecStatus is DebugExecutionStatus.NoDebuggee or DebugExecutionStatus.Uninitialized)
            return ToolResult.Error(ErrorMessages.TargetRebooting);
        return null;
    }

    private ToolResult? RequireKdConnected_TargetReachable()
    {
        if (!_state.KdConnected)
            return ToolResult.Error(ErrorMessages.KdNotConnected);
        return KdTargetUnreachable();
    }

    private ToolResult? RequireVmPaused()
    {
        if (_state.VmPower != VmPowerState.Paused)
            return ToolResult.Error(
                $"VM is {_state.VmPower}, not paused. Call vm_pause first.");
        return null;
    }

    private ToolResult? RequireVmNotOff()
    {
        if (_state.VmPower == VmPowerState.Off)
            return ToolResult.Error(ErrorMessages.VmIsOff);
        return null;
    }

    private ToolResult? RequireVmRunning_KdNotConnected()
    {
        var notRunning = VmNotRunning();
        if (notRunning != null)
            return notRunning;
        if (_state.KdConnected)
        {
            // "Call kd_disconnect first" would send the model in a circle when the
            // engine is parked: kd_disconnect is what just failed.
            if (IsEngineParked?.Invoke() == true)
                return ToolResult.Error(
                    "Kernel debugger is still connected and the engine is parked. " +
                    KernelDebug.DbgEngManager.EngineWedgedMessage);
            return ToolResult.Error(ErrorMessages.KdAlreadyConnected);
        }
        return null;
    }

    private ToolResult? RequireKdConnected()
    {
        if (!_state.KdConnected)
            return ToolResult.Error(ErrorMessages.KdNotConnected);
        return null;
    }

    private ToolResult? RequireKdConnected_TargetBroken()
    {
        if (!_state.KdConnected)
            return ToolResult.Error(ErrorMessages.KdNotConnected);

        var unreachable = KdTargetUnreachable();
        if (unreachable != null)
            return unreachable;

        if (_state.KdExecStatus != DebugExecutionStatus.Break)
            return ToolResult.Error(
                $"Target is in '{_state.KdExecStatus}' state — cannot read memory or execute " +
                "commands while the target is running. Call kd_break to halt the target first" +
                (_state.KdModuleFlood ? " (the kernel is loading modules in bulk right now, so kd_break may need up to 60 s)." : "."));

        if (_state.KdWaitPending)
            return ToolResult.Error(ErrorMessages.WaitPending);

        return null;
    }

    private ToolResult? RequireKdConnected_TargetRunning()
    {
        if (!_state.KdConnected)
            return ToolResult.Error(ErrorMessages.KdNotConnected);

        var unreachable = KdTargetUnreachable();
        if (unreachable != null)
            return unreachable;

        if (_state.KdExecStatus == DebugExecutionStatus.Break)
        {
            if (_state.IsBugcheck)
                return ToolResult.Error(ErrorMessages.BsodCannotBreak(_state.BugcheckCode));
            if (_state.KdFatalExceptionPending)
                return ToolResult.Error(ErrorMessages.TargetHaltedAtFatalException(_state.KdBreakReason));

            return ToolResult.Error(ErrorMessages.TargetAlreadyHalted(_state.KdBreakReason));
        }

        return null;
    }

    private ToolResult? RequireKdConnected_TargetBroken_CanResume()
    {
        if (!_state.KdConnected)
            return ToolResult.Error(ErrorMessages.KdNotConnected);

        var unreachable = KdTargetUnreachable();
        if (unreachable != null)
            return unreachable;

        if (_state.KdExecStatus != DebugExecutionStatus.Break)
            return ToolResult.Error(
                $"Target is in '{_state.KdExecStatus}' state — already running. " +
                "Call kd_break to halt it first, or kd_wait_for_event to " +
                "wait for a breakpoint hit" +
                (_state.KdModuleFlood ? " (the kernel is loading modules in bulk right now, so kd_break may need up to 60 s)." : "."));

        // A bugcheck is deliberately NOT blocked: continuing lets the kernel finish
        // the crash dump and reboot, which is one of the documented recovery paths.
        if (_state.KdWaitPending)
            return ToolResult.Error(ErrorMessages.WaitPending);

        return null;
    }

    private ToolResult? RequireKdConnected_TargetBroken_NoWaitPending()
    {
        var baseCheck = RequireKdConnected_TargetBroken();
        if (baseCheck != null) return baseCheck;

        // base check already covers WaitPending, but be explicit per architecture
        return null;
    }

    private ToolResult? RequireGuestOpsAvailable()
    {
        if (_state.VmPower == VmPowerState.Off)
            return ToolResult.Error(
                $"VM is {_state.VmPower}. Cannot execute guest operations. Start the VM with vm_start.");

        if (_state.VmPower == VmPowerState.Paused)
            return ToolResult.Error(ErrorMessages.VmIsPaused);

        if (_state.VmPower == VmPowerState.Unknown)
            return ToolResult.Error(ErrorMessages.VmPowerUnknown);

        if (_state.VmPower != VmPowerState.Running)
            return ToolResult.Error(
                $"VM is {_state.VmPower}. Cannot execute guest operations. Start the VM with vm_start.");

        // THE CRITICAL CHECK: is the kernel debugger holding the VM frozen?
        if (_state.KdConnected && _state.KdExecStatus == DebugExecutionStatus.Break)
        {
            if (_state.IsBugcheck)
                return ToolResult.Error(ErrorMessages.BsodGuestOpsUnavailable(_state.BugcheckCode));
            if (_state.KdFatalExceptionPending)
                return ToolResult.Error("VM is halted at a fatal exception; the guest OS is effectively crashed and " +
                                        "guest tools cannot run. " + ErrorMessages.FatalExceptionPending(_state.KdBreakReason));

            return ToolResult.Error(ErrorMessages.GuestFrozenByKd);
        }

        // A kernel that is rebooting after a crash has no OS to run guest tools in,
        // even while VMware's cached tools state still says "running".
        if (_state.KdConnected && _state.KdExecStatus is DebugExecutionStatus.NoDebuggee or DebugExecutionStatus.Uninitialized
            && _state.VmTools != VmToolsState.Running)
            return ToolResult.Error(ErrorMessages.GuestOpsDuringReboot);

        if (_state.VmTools != VmToolsState.Running)
            return ToolResult.Error(ErrorMessages.ToolsNotResponding);

        return null;
    }

    private ToolResult? RequireFridaAttached()
    {
        var guestCheck = RequireGuestOpsAvailable();
        if (guestCheck != null) return guestCheck;

        if (_state.FridaState == null || !_state.FridaState.Connected)
            return ToolResult.Error(
                "Frida is not attached to any process. Call umd_frida_attach first.");

        return null;
    }

    private ToolResult? RequireDbgsrvConnected()
    {
        if (_state.DbgsrvState == null || !_state.DbgsrvState.Connected)
            return ToolResult.Error(
                "dbgsrv is not connected. Call umd_dbgsrv_connect first.");

        return null;
    }
}
