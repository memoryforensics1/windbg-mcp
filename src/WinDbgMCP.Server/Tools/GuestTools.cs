using System.ComponentModel;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using WinDbgMCP.Server.Guest;
using WinDbgMCP.Server.KernelDebug;
using WinDbgMCP.Server.KernelDebug.Models;
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
    public static Task<string> GuestRunCommand(
        StateCoordinator state,
        GuestExecManager guest,
        DbgEngManager dbgEng,
        [Description("Command to execute (runs via cmd.exe /c)")] string command,
        [Description("Working directory inside the guest (optional)")] string? workingDirectory = null,
        [Description("Timeout in seconds (default 60)")] int timeoutSeconds = 60,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("guest_run_command", async () =>
        {
            // A graceful restart never re-attaches to an existing KD session and
            // leaves the engine stuck for good, so detach before letting it happen.
            var note = "";
            if (state.State.KdConnected && IsRestartCommand(command))
            {
                DetachResult detach;
                try { detach = await dbgEng.DisconnectAsync(); }
                catch (Exception ex) { detach = new DetachResult(false, $"{ex.GetType().Name}: {ex.Message}"); }

                if (!detach.Detached)
                    return "NOT EXECUTED: this command restarts or shuts down the guest, and the kernel " +
                           "debugger could not be detached first (" + detach.Message + "). Restarting with the " +
                           "debugger attached would leave the debug session unusable until the MCP server is " +
                           "restarted. Call get_system_state, then kd_disconnect, and retry the command.";

                state.SetKdDisconnected();
                note = "Kernel debugger detached before the guest restart (a graceful restart does not " +
                       "re-attach to an existing session). Call kd_connect once the OS is back up.\n";
            }

            var result = await guest.RunCommandAsync(command, workingDirectory, timeoutSeconds, ct);
            return note + result;
        });
    }

    /// <summary>
    /// True for a guest command that restarts or shuts the OS down, so KD can be
    /// detached first (a graceful restart never re-attaches to a live KD session).
    /// A missed match risks wedging the engine; a spurious match only costs a
    /// needless detach, so the bias is toward matching: any <c>shutdown</c> except
    /// the no-op switches (/a abort, /l logoff, /h hibernate, /i GUI, /? help), the
    /// PowerShell cmdlets, wmic reboot, and the Win32Shutdown WMI method.
    /// </summary>
    public static bool IsRestartCommand(string command) => RestartCommand.IsMatch(command);

    // The delimiter class includes quotes, parens and backslash so a full path or
    // quoted invocation still matches; the shutdown lookahead is bounded to the
    // current command segment ([^&|;]*) so a later "&& echo -l" cannot suppress it.
    private static readonly Regex RestartCommand = new(
        @"(^|[\s&|;""'(\\])(" +
        @"shutdown(\.exe)?\b(?![^&|;]*(/|-)(?:[alhi]\b|\?))|" +
        @"Restart-Computer\b|Stop-Computer\b|" +
        @"wmic\b[^&|;]*\bos\b[^&|;]*\b(reboot|shutdown)\b)" +
        @"|\bWin32Shutdown\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [McpServerTool(Name = "guest_transfer_to_vm"), Description(
        "Copy a file from the host machine to the guest VM. " +
        "The VM must be running (target NOT frozen). " +
        "Use this to deploy drivers, tools, or test binaries to the VM. " +
        "Large files (>50MB) automatically use VMware shared folders for fast transfer.")]
    public static Task<string> GuestTransferToVm(
        StateCoordinator state,
        GuestExecManager guest,
        [Description("Path to the file on the host")] string hostPath,
        [Description("Destination path inside the guest VM")] string guestPath,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("guest_transfer_to_vm", async () =>
        {
            try
            {
                return await guest.CopyFileToGuestAsync(hostPath, guestPath, ct);
            }
            catch (TimeoutException)
            {
                return "File transfer timed out. VMware Tools may not be responding. Check get_system_state.";
            }
            catch (Exception ex)
            {
                return $"guest_transfer_to_vm failed: {ex.GetType().Name}: {ex.Message}";
            }
        });
    }

    [McpServerTool(Name = "guest_transfer_from_vm"), Description(
        "Copy a file from the guest VM to the host machine. " +
        "The VM must be running (target NOT frozen). " +
        "Use this to retrieve crash dumps, logs, or output files from the VM. " +
        "Automatically uses VMware shared folders for fast transfer when available.")]
    public static Task<string> GuestTransferFromVm(
        StateCoordinator state,
        GuestExecManager guest,
        [Description("Path to the file inside the guest VM")] string guestPath,
        [Description("Destination path on the host")] string hostPath,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("guest_transfer_from_vm", async () =>
        {
            try
            {
                return await guest.CopyFileFromGuestAsync(guestPath, hostPath, ct);
            }
            catch (TimeoutException)
            {
                return "File transfer timed out. VMware Tools may not be responding. Check get_system_state.";
            }
            catch (Exception ex)
            {
                return $"guest_transfer_from_vm failed: {ex.GetType().Name}: {ex.Message}";
            }
        });
    }

    [McpServerTool(Name = "guest_list_processes"), Description(
        "List all running processes inside the guest VM. " +
        "The VM must be running (target NOT frozen). " +
        "Returns process names and PIDs.")]
    public static Task<string> GuestListProcesses(
        StateCoordinator state,
        GuestExecManager guest,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("guest_list_processes", async () =>
        {
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
        });
    }

    [McpServerTool(Name = "guest_kill_process"), Description(
        "Kill a process inside the guest VM by PID. " +
        "The VM must be running (target NOT frozen). " +
        "Use guest_list_processes first to find the PID.")]
    public static Task<string> GuestKillProcess(
        StateCoordinator state,
        GuestExecManager guest,
        [Description("Process ID (PID) to kill")] uint pid,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("guest_kill_process", async () =>
        {
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
        });
    }
}
