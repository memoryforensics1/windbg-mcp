using Microsoft.Extensions.Logging;
using WinDbgMCP.Server.Configuration;

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

    // Public read-only accessor for state
    public SystemState State => _state;

    // These will be set when the managers are created
    // Using Func<> delegates to avoid circular dependencies during construction
    public Func<Task<VmPowerState>>? GetVmPowerStateAsync { get; set; }
    public Func<TimeSpan, Task<bool>>? AreToolsRunningAsync { get; set; }
    public Func<DebugExecutionStatus>? GetDbgEngExecutionStatus { get; set; }
    public Func<bool>? IsDbgEngConnected { get; set; }
    public Func<int>? GetPendingEventCount { get; set; }
    public Func<Task<(bool IsBugcheck, string? BugcheckCode)>>? DetectBugcheckAsync { get; set; }
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
                "kd_wait_for_event" => RequireKdConnected(),

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

            // If DbgEng reports NoDebuggee but we thought we were connected,
            // the connection was lost (VM rebooted, snapshot restored, etc.)
            if (status == DebugExecutionStatus.NoDebuggee)
            {
                _logger.LogWarning("Kernel debugger connection lost (NoDebuggee detected)");
                _state.KdConnected = false;
                _state.KdBreakReason = null;
                _state.IsBugcheck = false;
                _state.BugcheckCode = null;
            }

            // 1.5 Reboot detection — the old kernel (and any BSOD it was in) is gone,
            // so the new break must be re-evaluated from scratch.
            var rebooted = IsRebootDetected?.Invoke() ?? false;
            if (rebooted && !_state.KdRebootDetected)
            {
                _logger.LogWarning("Target reboot detected; kernel state reset");
                _state.IsBugcheck = false;
                _state.BugcheckCode = null;
                _state.KdBreakReason = "Target rebooted";
                _bsodCheckedForCurrentBreak = false;
            }
            _state.KdRebootDetected = rebooted;
        }

        // 2. Event queue count
        _state.PendingEventCount = GetPendingEventCount?.Invoke() ?? 0;

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
                    var (isBugcheck, bugcheckCode) = await DetectBugcheckAsync();
                    _bsodCheckedForCurrentBreak = true;
                    if (isBugcheck)
                    {
                        _state.IsBugcheck = true;
                        _state.BugcheckCode = bugcheckCode;
                        _logger.LogWarning("BSOD detected during state refresh: {Code}", bugcheckCode);
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
                    _state.VmPower = await GetVmPowerStateAsync();
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
                    _state.VmTools = await AreToolsRunningAsync(toolsTimeout)
                        ? VmToolsState.Running
                        : VmToolsState.NotResponding;
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
    public void SetBsodDetected(string? bugcheckCode)
    {
        _state.IsBugcheck = true;
        _state.BugcheckCode = bugcheckCode;
    }

    // ═══════════════════════════════════════════════════════════════
    //  PRECONDITION CHECK IMPLEMENTATIONS
    // ═══════════════════════════════════════════════════════════════

    private ToolResult? RequireVmOff()
    {
        if (_state.VmPower != VmPowerState.Off)
            return ToolResult.Error(
                $"VM is {_state.VmPower}. Call vm_stop first, then vm_start.");
        return null;
    }

    private ToolResult? RequireVmNotOff(bool warnIfKdAttached = false)
    {
        if (_state.VmPower == VmPowerState.Off)
            return ToolResult.Error(ErrorMessages.VmIsOff);
        if (warnIfKdAttached && _state.KdConnected)
            return ToolResult.Success(
                "WARNING: Kernel debugger session will be lost. Proceeding.");
        return null;
    }

    private ToolResult? RequireVmRunning(bool warnIfKdAttached = false)
    {
        if (_state.VmPower != VmPowerState.Running)
            return ToolResult.Error(
                $"VM is {_state.VmPower}. Start the VM first with vm_start.");
        return null;
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
        if (_state.VmPower != VmPowerState.Running)
            return ToolResult.Error(
                $"VM is {_state.VmPower}. Start the VM first with vm_start.");
        if (_state.KdConnected)
            return ToolResult.Error(ErrorMessages.KdAlreadyConnected);
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

        if (_state.KdExecStatus != DebugExecutionStatus.Break)
            return ToolResult.Error(
                $"Target is in '{_state.KdExecStatus}' state — cannot read memory or execute " +
                "commands while the target is running. Call kd_break to halt the target first.");

        if (_state.KdWaitPending)
            return ToolResult.Error(ErrorMessages.WaitPending);

        return null;
    }

    private ToolResult? RequireKdConnected_TargetRunning()
    {
        if (!_state.KdConnected)
            return ToolResult.Error(ErrorMessages.KdNotConnected);

        if (_state.KdExecStatus == DebugExecutionStatus.Break)
        {
            if (_state.IsBugcheck)
                return ToolResult.Error(ErrorMessages.BsodCannotBreak(_state.BugcheckCode));

            return ToolResult.Error(ErrorMessages.TargetAlreadyBroken);
        }

        return null;
    }

    private ToolResult? RequireKdConnected_TargetBroken_CanResume()
    {
        if (!_state.KdConnected)
            return ToolResult.Error(ErrorMessages.KdNotConnected);

        if (_state.KdExecStatus != DebugExecutionStatus.Break)
            return ToolResult.Error(
                $"Target is in '{_state.KdExecStatus}' state — already running. " +
                "Call kd_break to halt it first, or kd_wait_for_event to " +
                "wait for a breakpoint hit.");

        if (_state.IsBugcheck)
            return ToolResult.Error(ErrorMessages.BsodCannotResume(_state.BugcheckCode));

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

        if (_state.VmPower != VmPowerState.Running)
            return ToolResult.Error(
                $"VM is {_state.VmPower}. Cannot execute guest operations. Start the VM with vm_start.");

        // THE CRITICAL CHECK: is the kernel debugger holding the VM frozen?
        if (_state.KdConnected && _state.KdExecStatus == DebugExecutionStatus.Break)
        {
            if (_state.IsBugcheck)
                return ToolResult.Error(ErrorMessages.BsodGuestOpsUnavailable(_state.BugcheckCode));

            return ToolResult.Error(ErrorMessages.GuestFrozenByKd);
        }

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
