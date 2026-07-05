using System.ComponentModel;
using ModelContextProtocol.Server;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.KernelDebug;
using WinDbgMCP.Server.State;
using WinDbgMCP.Server.Vmware;

namespace WinDbgMCP.Server.Tools;

[McpServerToolType]
public static class VmTools
{
    [McpServerTool(Name = "vm_start"), Description(
        "Start the VM. The VM must be powered off. " +
        "After starting, wait for VMware Tools to report 'running' before using guest operations.")]
    public static async Task<string> VmStart(
        StateCoordinator state,
        VmwareManager vmware,
        [Description("If true, start VM without a visible window (default: false)")] bool headless = false,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("vm_start");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            var result = await vmware.StartAsync(headless, ct);
            if (!result.Success)
                return $"vm_start failed: {result.Message}";

            return result.Message + " Call get_system_state to check when VMware Tools is running.";
        }
        catch (TimeoutException)
        {
            return ErrorMessages.OperationTimedOut("vm_start", 60);
        }
    }

    [McpServerTool(Name = "vm_stop"), Description(
        "Stop the VM. Use hard=true for immediate power off, false for graceful shutdown.")]
    public static async Task<string> VmStop(
        StateCoordinator state,
        VmwareManager vmware,
        [Description("If true, force power off. If false, attempt graceful shutdown (default: false)")] bool hard = false,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("vm_stop");
        // vm_stop precheck returns Success with a warning if KD is attached — that's OK
        if (precheck != null && !precheck.IsSuccess) return precheck.ErrorMessage!;

        string warning = precheck?.IsSuccess == true ? precheck.Message + " " : "";

        try
        {
            // If KD is connected, note it will be lost
            if (state.State.KdConnected)
                state.SetKdDisconnected();

            var result = await vmware.StopAsync(hard, ct);
            if (!result.Success)
                return $"vm_stop failed: {result.Message}";

            return warning + result.Message;
        }
        catch (TimeoutException)
        {
            return ErrorMessages.OperationTimedOut("vm_stop", 30);
        }
    }

    [McpServerTool(Name = "vm_pause"), Description(
        "Pause the VM. EVERYTHING freezes: kernel debugger, guest, network. " +
        "This is different from kd_break! Use vm_resume to unpause.")]
    public static async Task<string> VmPause(
        StateCoordinator state,
        VmwareManager vmware,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("vm_pause");
        if (precheck != null && !precheck.IsSuccess) return precheck.ErrorMessage!;

        string warning = precheck?.IsSuccess == true ? precheck.Message + " " : "";

        try
        {
            var result = await vmware.PauseAsync(ct);
            if (!result.Success)
                return $"vm_pause failed: {result.Message}";

            state.SetVmPaused();
            return warning + result.Message;
        }
        catch (TimeoutException)
        {
            return ErrorMessages.OperationTimedOut("vm_pause", 10);
        }
    }

    [McpServerTool(Name = "vm_resume"), Description(
        "Resume a paused VM. The VM must be in the Paused state (via vm_pause).")]
    public static async Task<string> VmResume(
        StateCoordinator state,
        VmwareManager vmware,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("vm_resume");
        if (precheck != null) return precheck.ErrorMessage!;

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
    }

    [McpServerTool(Name = "vm_snapshot_restore"), Description(
        "Restore a named snapshot. Destroys all debug sessions (Frida, dbgsrv). " +
        "If the kernel debugger was connected, it is cleanly disconnected before restore " +
        "and automatically reconnected afterwards — no manual kd_connect needed.")]
    public static async Task<string> VmSnapshotRestore(
        StateCoordinator state,
        VmwareManager vmware,
        DbgEngManager dbgEng,
        ServerConfig config,
        [Description("Name of the snapshot to restore")] string name,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("vm_snapshot_restore");
        if (precheck != null) return precheck.ErrorMessage!;

        // Remember whether KD was connected so we can reconnect after restore
        var wasKdConnected = state.State.KdConnected;

        try
        {
            // Step 1: Clean KD disconnect BEFORE restore while KDNET is still alive.
            // This avoids the race condition where the snapshot restore kills the
            // KDNET connection while DbgEng is mid-operation on its dedicated thread.
            if (wasKdConnected)
            {
                try
                {
                    await dbgEng.DisconnectAsync();
                }
                catch
                {
                    // Best-effort — if disconnect fails the restore still proceeds
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

            var statusMsg = powerState == VmPowerState.Running
                ? $"Snapshot '{name}' restored and VM is running."
                : $"Snapshot '{name}' restored but VM is {powerState}. Call vm_start to start it.";

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
                    return statusMsg + $" Kernel debugger reconnected automatically. {reconnectResult}";
                }
                catch (Exception ex)
                {
                    return statusMsg + $" Auto-reconnect failed: {ex.Message} " +
                           "Call kd_connect manually when the VM is ready.";
                }
            }

            return statusMsg + " " + ErrorMessages.SnapshotRestoredWarning;
        }
        catch (TimeoutException)
        {
            return ErrorMessages.OperationTimedOut("vm_snapshot_restore", 60);
        }
    }

    [McpServerTool(Name = "vm_set_target"), Description(
        "Switch the active VM target at runtime. " +
        "All VM, guest, and snapshot operations will target the new VM after this call. " +
        "If the kernel debugger is connected, it is cleanly disconnected first. " +
        "Supports remote hypervisors: pass hostType/hostUrl/host credentials to target " +
        "a VM on ESXi, vCenter, or shared Workstation instead of the local machine. " +
        "Note: kd_connect uses its own connection string — this only affects guest/VM operations.")]
    public static async Task<string> VmSetTarget(
        StateCoordinator state,
        VmwareManager vmware,
        DbgEngManager dbgEng,
        [Description("Path to the .vmx file. For esx/vc hosts use a datastore path like \"[datastore1] win10/win10.vmx\"")] string vmxPath,
        [Description("Guest OS username")] string guestUsername,
        [Description("Guest OS password")] string guestPassword,
        [Description("VM encryption password (leave empty if VM is not encrypted)")] string vmPassword = "",
        [Description("Hypervisor type: ws (local Workstation), esx, vc, ws-shared, fusion, player. Omit to keep current.")] string? hostType = null,
        [Description("Remote hypervisor URL, e.g. https://esxi-host/sdk. Pass empty string to switch back to local. Omit to keep current.")] string? hostUrl = null,
        [Description("Hypervisor login username (required when hostUrl is set). Omit to keep current.")] string? hostUsername = null,
        [Description("Hypervisor login password. Omit to keep current.")] string? hostPassword = null,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("vm_set_target");
        if (precheck != null) return precheck.ErrorMessage!;

        var wasKdConnected = state.State.KdConnected;

        // Cleanly disconnect KD if connected — it was pointing at the old VM
        if (wasKdConnected)
        {
            try { await dbgEng.DisconnectAsync(); } catch { }
            state.SetKdDisconnected();
        }

        // Switch the target
        try
        {
            vmware.UpdateTarget(vmxPath, guestUsername, guestPassword, vmPassword,
                hostType, hostUrl, hostUsername, hostPassword);
        }
        catch (InvalidOperationException ex)
        {
            return $"vm_set_target failed: {ex.Message}";
        }

        // Reset all state — power state of the new VM is unknown until we check
        var powerState = await vmware.GetPowerStateAsync(ct);
        state.ResetAllState(powerState, vmxPath);

        var kdNote = wasKdConnected
            ? " Previous kernel debugger session was disconnected."
            : "";

        return $"VM target switched to '{vmxPath}' (user: {guestUsername}). " +
               $"VM is currently {powerState}.{kdNote} " +
               "Use vm_start if the VM is off, or proceed with guest/VM operations if it is running.";
    }

    [McpServerTool(Name = "vm_snapshot_list"), Description(
        "List all snapshots for the VM.")]
    public static async Task<string> VmSnapshotList(
        StateCoordinator state,
        VmwareManager vmware,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("vm_snapshot_list");
        if (precheck != null) return precheck.ErrorMessage!;

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
    }

}
