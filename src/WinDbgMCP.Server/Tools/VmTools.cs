using System.ComponentModel;
using ModelContextProtocol.Server;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.KernelDebug;
using WinDbgMCP.Server.KernelDebug.Models;
using WinDbgMCP.Server.State;
using WinDbgMCP.Server.Vmware;

namespace WinDbgMCP.Server.Tools;

[McpServerToolType]
public static class VmTools
{
    [McpServerTool(Name = "vm_start"), Description(
        "Start the VM. The VM must be powered off. " +
        "After starting, wait for VMware Tools to report 'running' before using guest operations.")]
    public static Task<string> VmStart(
        StateCoordinator state,
        VmwareManager vmware,
        [Description("If true, start VM without a visible window (default: false)")] bool headless = false,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("vm_start", async () =>
        {
            try
            {
                var result = await vmware.StartAsync(headless, ct);
                if (!result.Success)
                    return $"vm_start failed: {result.Message}";

                state.SetVmPowerChangedByTool(VmPowerState.Running);
                return result.Message + " The guest now boots: poll get_system_state until VMware Tools reports Running " +
                       "before guest tools, and allow 30-60 s before kd_connect (KDNET initialises during boot; an earlier " +
                       "kd_connect simply times out, which is not a configuration error).";
            }
            catch (TimeoutException)
            {
                return ErrorMessages.OperationTimedOut("vm_start", 60);
            }
        });
    }

    [McpServerTool(Name = "vm_stop"), Description(
        "Stop the VM. Use hard=true for immediate power off, false for graceful shutdown.")]
    public static Task<string> VmStop(
        StateCoordinator state,
        VmwareManager vmware,
        DbgEngManager dbgEng,
        [Description("If true, force power off. If false, attempt graceful shutdown (default: false)")] bool hard = false,
        CancellationToken ct = default)
    {
        // The precheck's "KD session will be lost" warning is prepended by RunToolAsync.
        return state.RunToolAsync("vm_stop", async () =>
        {
            try
            {
                // Detach the kernel debugger first so the dying session's events
                // don't surface later as unexpected SessionEnded/Error alerts.
                var kdNote = "";
                if (state.State.KdConnected)
                {
                    // A graceful stop asks the guest OS to shut down; a crashed OS cannot.
                    if (!hard && (state.State.IsBugcheck || state.State.KdFatalExceptionPending))
                        return "NOT EXECUTED: a graceful vm_stop needs a live guest OS, and the guest is crashed " +
                               (state.State.IsBugcheck ? $"(bugcheck {state.State.BugcheckCode})" : $"(fatal exception: {state.State.KdBreakReason})") +
                               ". Use vm_stop(hard=true) to power it off, or kd_continue to let it write its dump and reboot.";

                    DetachResult detach;
                    try { detach = await dbgEng.DisconnectAsync(); }
                    catch (Exception ex) { detach = new DetachResult(false, $"{ex.GetType().Name}: {ex.Message}"); }

                    if (!detach.Detached && !hard)
                        return "NOT EXECUTED: a graceful vm_stop restarts the guest OS, and the kernel debugger " +
                               "could not be detached first (" + detach.Message + "). Call get_system_state, then " +
                               "kd_disconnect, and retry; or use vm_stop(hard=true).";

                    if (!detach.Detached)
                    {
                        // Hard power-off: the target dies anyway; drop the client so the
                        // state is at least consistent, and say what that costs.
                        state.CleanupKdSession?.Invoke();
                        kdNote = " WARNING: the kernel debugger could not be detached cleanly before the power-off (" +
                                 detach.Message + "); kernel-debug tools may need an MCP server restart.";
                    }
                    state.SetKdDisconnected();
                }

                var result = await vmware.StopAsync(hard, ct);
                if (!result.Success)
                    return $"vm_stop failed: {result.Message}";

                state.SetVmPowerChangedByTool(VmPowerState.Off);
                return result.Message + kdNote;
            }
            catch (TimeoutException)
            {
                return ErrorMessages.OperationTimedOut("vm_stop", 30);
            }
        });
    }

    [McpServerTool(Name = "vm_pause"), Description(
        "Pause the VM. EVERYTHING freezes: kernel debugger, guest, network. " +
        "This is different from kd_break! Use vm_resume to unpause.")]
    public static Task<string> VmPause(
        StateCoordinator state,
        VmwareManager vmware,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("vm_pause", async () =>
        {
            try
            {
                var result = await vmware.PauseAsync(ct);
                if (!result.Success)
                    return $"vm_pause failed: {result.Message}";

                state.SetVmPaused();
                return result.Message;
            }
            catch (TimeoutException)
            {
                return ErrorMessages.OperationTimedOut("vm_pause", 10);
            }
        });
    }

    [McpServerTool(Name = "vm_resume"), Description(
        "Resume a paused VM. The VM must be in the Paused state (via vm_pause).")]
    public static Task<string> VmResume(
        StateCoordinator state,
        VmwareManager vmware,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("vm_resume", async () =>
        {
            try
            {
                var result = await vmware.UnpauseAsync(ct);
                if (!result.Success)
                    return $"vm_resume failed: {result.Message}";

                state.SetVmResumed();
                return result.Message;
            }
            catch (TimeoutException)
            {
                return ErrorMessages.OperationTimedOut("vm_resume", 10);
            }
        });
    }

    [McpServerTool(Name = "vm_snapshot_restore"), Description(
        "Restore a named snapshot. Destroys all debug sessions (Frida, dbgsrv). " +
        "If the kernel debugger was connected, it is cleanly disconnected before restore " +
        "and automatically reconnected afterwards — no manual kd_connect needed.")]
    public static Task<string> VmSnapshotRestore(
        StateCoordinator state,
        VmwareManager vmware,
        DbgEngManager dbgEng,
        ServerConfig config,
        [Description("Name of the snapshot to restore")] string name,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("vm_snapshot_restore", async () =>
        {
            // Remember whether KD was connected so we can reconnect after restore
            var wasKdConnected = state.State.KdConnected;

            try
            {
                // Step 1: Clean KD disconnect BEFORE restore while KDNET is still alive.
                // This avoids the race condition where the snapshot restore kills the
                // KDNET connection while DbgEng is mid-operation on its dedicated thread.
                var kdNote = "";
                if (wasKdConnected)
                {
                    // Best-effort: the restore replaces the target anyway, but a session
                    // the engine would not release has to be dropped and reported.
                    DetachResult detach;
                    try { detach = await dbgEng.DisconnectAsync(); }
                    catch (Exception ex) { detach = new DetachResult(false, $"{ex.GetType().Name}: {ex.Message}"); }
                    if (!detach.Detached)
                    {
                        state.CleanupKdSession?.Invoke();
                        kdNote = " WARNING: the kernel debugger could not be detached cleanly before the restore (" +
                                 detach.Message + "); if kd_connect now fails, restart the MCP server.";
                    }
                    state.SetKdDisconnected();
                }

                // Step 2: Restore the snapshot
                var result = await vmware.SnapshotRestoreAsync(name, ct);
                if (!result.Success)
                    return $"vm_snapshot_restore failed: {result.Message}";

                // Step 3: Check power state and auto-start if needed
                var powerState = await vmware.GetPowerStateAsync(ct);
                if (powerState != VmPowerState.Running)
                {
                    var startResult = await vmware.StartAsync(headless: false, ct);
                    if (startResult.Success)
                        powerState = VmPowerState.Running;
                }

                // Step 4: Reset all state (safe — KD already disconnected above)
                state.ResetAllState(powerState);

                var statusMsg = (powerState == VmPowerState.Running
                    ? $"Snapshot '{name}' restored and VM is running."
                    : $"Snapshot '{name}' restored but VM is {powerState}. Call vm_start to start it.") + kdNote;

                // Step 5: If KD was connected before, attempt transparent reconnect
                if (wasKdConnected && powerState == VmPowerState.Running)
                {
                    try
                    {
                        var reconnectResult = await dbgEng.ConnectKernelAsync(ct: ct);
                        var transport = config.KernelDebug.Transport.Equals("kdnet", StringComparison.OrdinalIgnoreCase)
                            ? KdTransport.KDNET
                            : KdTransport.Serial;
                        state.SetKdConnected(transport);
                        return statusMsg + $" Kernel debugger reconnected automatically. {reconnectResult} " +
                               "Frida and dbgsrv sessions (if any) are gone. The target is halted at its initial " +
                               "breakpoint: call kd_continue before any guest operation.";
                    }
                    catch (Exception ex)
                    {
                        return statusMsg + $" Auto-reconnect failed: {ex.Message} " +
                               "Frida and dbgsrv sessions (if any) are gone. Call kd_connect when the VM is ready " +
                               "(allow 30-60 s after a restore for KDNET).";
                    }
                }

                return statusMsg + (wasKdConnected
                    ? " " + ErrorMessages.SnapshotRestoredWarning
                    : " Frida and dbgsrv sessions (if any) were terminated by the restore; the kernel debugger was not connected.");
            }
            catch (TimeoutException)
            {
                return ErrorMessages.OperationTimedOut("vm_snapshot_restore", 60);
            }
        });
    }

    [McpServerTool(Name = "vm_set_target"), Description(
        "Switch the active VM target at runtime. " +
        "All VM, guest, and snapshot operations will target the new VM after this call. " +
        "If the kernel debugger is connected, it is cleanly disconnected first. " +
        "Note: kd_connect uses its own connection string — this only affects guest/VM operations.")]
    public static Task<string> VmSetTarget(
        StateCoordinator state,
        VmwareManager vmware,
        DbgEngManager dbgEng,
        [Description("Absolute path to the .vmx file of the target VM")] string vmxPath,
        [Description("Guest OS username")] string guestUsername,
        [Description("Guest OS password")] string guestPassword,
        [Description("VM encryption password (leave empty if VM is not encrypted)")] string vmPassword = "",
        CancellationToken ct = default)
    {
        return state.RunToolAsync("vm_set_target", async () =>
        {
            var wasKdConnected = state.State.KdConnected;

            // Cleanly disconnect KD if connected — it was pointing at the old VM
            if (wasKdConnected)
            {
                DetachResult detach;
                try { detach = await dbgEng.DisconnectAsync(); }
                catch (Exception ex) { detach = new DetachResult(false, $"{ex.GetType().Name}: {ex.Message}"); }
                if (!detach.Detached)
                    return "NOT EXECUTED: the kernel debugger is attached to the current VM and could not be " +
                           "detached (" + detach.Message + "). Call get_system_state, then kd_disconnect, and retry.";
                state.SetKdDisconnected();
            }

            // Switch the target
            vmware.UpdateTarget(vmxPath, guestUsername, guestPassword, vmPassword);

            // Reset all state — power state of the new VM is unknown until we check
            var powerState = await vmware.GetPowerStateAsync(ct);
            state.ResetAllState(powerState, vmxPath);

            var kdNote = wasKdConnected
                ? " Previous kernel debugger session was disconnected."
                : "";

            return $"VM target switched to '{vmxPath}' (user: {guestUsername}). " +
                   $"VM is currently {powerState}.{kdNote} " +
                   "Use vm_start if the VM is off, or proceed with guest/VM operations if it is running.";
        });
    }

    [McpServerTool(Name = "vm_snapshot_list"), Description(
        "List all snapshots for the VM.")]
    public static Task<string> VmSnapshotList(
        StateCoordinator state,
        VmwareManager vmware,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("vm_snapshot_list", async () =>
        {
            try
            {
                var result = await vmware.SnapshotListAsync(ct);
                if (!result.Success)
                    return $"vm_snapshot_list failed: {result.ErrorMessage}";

                if (result.Snapshots.Count == 0)
                    return "No snapshots found for this VM.";

                return $"Snapshots ({result.Snapshots.Count}):\n" +
                       string.Join("\n", result.Snapshots.Select(s => $"  - {s}"));
            }
            catch (TimeoutException)
            {
                return ErrorMessages.OperationTimedOut("vm_snapshot_list", 10);
            }
        });
    }

}
