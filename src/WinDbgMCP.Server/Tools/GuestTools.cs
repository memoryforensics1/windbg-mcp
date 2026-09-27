using System.ComponentModel;
using ModelContextProtocol.Server;
using WinDbgMCP.Server.Guest;
using WinDbgMCP.Server.State;

namespace WinDbgMCP.Server.Tools;

[McpServerToolType]
public static class GuestTools
{
    [McpServerTool(Name = "guest_run_command"), Description(
        "Execute a command inside the guest VM and capture stdout/stderr. " +
        "The VM must be running (target NOT frozen at a breakpoint). " +
        "If the kernel debugger has frozen the VM, call kd_continue first. " +
        "Examples: 'ipconfig /all', 'sc query MyDriver', 'dir C:\\Windows\\System32'. " +
        "The command runs via cmd.exe /c, so pipe/redirect syntax works.")]
    public static async Task<string> GuestRunCommand(
        StateCoordinator state,
        GuestExecManager guest,
        [Description("Command to execute (runs via cmd.exe /c)")] string command,
        [Description("Working directory inside the guest (optional)")] string? workingDirectory = null,
        [Description("Timeout in seconds (default 60)")] int timeoutSeconds = 60,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("guest_run_command");
        if (precheck != null) return precheck.ErrorMessage!;

        var result = await guest.RunCommandAsync(command, workingDirectory, timeoutSeconds, ct);
        return result.ToString();
    }

    [McpServerTool(Name = "guest_transfer_to_vm"), Description(
        "Copy a file from the host machine to the guest VM. " +
        "The VM must be running (target NOT frozen). " +
        "Use this to deploy drivers, tools, or test binaries to the VM. " +
        "Timeout scales with file size. Diagnostics logged to file_transfer.log.")]
    public static async Task<string> GuestTransferToVm(
        StateCoordinator state,
        GuestExecManager guest,
        [Description("Path to the file on the host")] string hostPath,
        [Description("Destination path inside the guest VM")] string guestPath,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("guest_transfer_to_vm");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            return await guest.CopyFileToGuestAsync(hostPath, guestPath, ct);
        }
        catch (TimeoutException)
        {
            return "File transfer timed out. Check file_transfer.log in the server directory for details. " +
                   "VMware Tools may not be responding — check get_system_state.";
        }
        catch (Exception ex)
        {
            return $"guest_transfer_to_vm failed: {ex.GetType().Name}: {ex.Message}. " +
                   "Check file_transfer.log for details.";
        }
    }

    [McpServerTool(Name = "guest_transfer_from_vm"), Description(
        "Copy a file from the guest VM to the host machine. " +
        "The VM must be running (target NOT frozen). " +
        "Use this to retrieve crash dumps, logs, or output files from the VM. " +
        "Diagnostics logged to file_transfer.log.")]
    public static async Task<string> GuestTransferFromVm(
        StateCoordinator state,
        GuestExecManager guest,
        [Description("Path to the file inside the guest VM")] string guestPath,
        [Description("Destination path on the host")] string hostPath,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("guest_transfer_from_vm");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            return await guest.CopyFileFromGuestAsync(guestPath, hostPath, ct);
        }
        catch (TimeoutException)
        {
            return "File transfer timed out. Check file_transfer.log in the server directory for details. " +
                   "VMware Tools may not be responding — check get_system_state.";
        }
        catch (Exception ex)
        {
            return $"guest_transfer_from_vm failed: {ex.GetType().Name}: {ex.Message}. " +
                   "Check file_transfer.log for details.";
        }
    }

    [McpServerTool(Name = "guest_list_processes"), Description(
        "List all running processes inside the guest VM. " +
        "The VM must be running (target NOT frozen). " +
        "Returns process names and PIDs.")]
    public static async Task<string> GuestListProcesses(
        StateCoordinator state,
        GuestExecManager guest,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("guest_list_processes");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            return await guest.ListProcessesAsync(ct);
        }
        catch (TimeoutException)
        {
            return "Process listing timed out. VMware Tools may not be responding.";
        }
        catch (Exception ex)
        {
            return $"guest_list_processes failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

    [McpServerTool(Name = "guest_kill_process"), Description(
        "Kill a process inside the guest VM by PID. " +
        "The VM must be running (target NOT frozen). " +
        "Use guest_list_processes first to find the PID.")]
    public static async Task<string> GuestKillProcess(
        StateCoordinator state,
        GuestExecManager guest,
        [Description("Process ID (PID) to kill")] uint pid,
        CancellationToken ct = default)
    {
        var precheck = await state.ValidatePreconditionsAsync("guest_kill_process");
        if (precheck != null) return precheck.ErrorMessage!;

        try
        {
            return await guest.KillProcessAsync(pid, ct);
        }
        catch (TimeoutException)
        {
            return $"Kill process {pid} timed out. Process may still be running.";
        }
        catch (Exception ex)
        {
            return $"guest_kill_process failed: {ex.GetType().Name}: {ex.Message}";
        }
    }
}
