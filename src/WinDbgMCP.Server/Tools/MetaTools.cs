using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using WinDbgMCP.Server.State;

namespace WinDbgMCP.Server.Tools;

[McpServerToolType]
public static class MetaTools
{
    [McpServerTool(Name = "get_system_state", ReadOnly = true), Description(
        "Returns the complete state of the system: VM power, VMware Tools, kernel debugger, " +
        "guest operations availability, and user-mode debug sessions. " +
        "ALWAYS allowed — call this whenever you're unsure about the current state.")]
    public static Task<string> GetSystemState(
        StateCoordinator state,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("get_system_state", () =>
        {
            var s = state.State;

            var sb = new StringBuilder();
            sb.AppendLine("=== SYSTEM STATE ===");
            sb.AppendLine();

            // VM
            sb.AppendLine($"VM Power:          {s.VmPower}");
            sb.AppendLine($"VMware Tools:      {s.VmTools}");
            sb.AppendLine($"VM IP Address:     {s.VmIpAddress ?? "unknown"}");
            sb.AppendLine();

            // Kernel Debugger
            sb.AppendLine($"KD Connected:      {s.KdConnected}");
            if (s.KdConnected)
            {
                sb.AppendLine($"KD Transport:      {s.KdTransportType}");
                sb.AppendLine($"Execution Status:  {s.KdExecStatus}");
                if (s.KdModuleFlood)
                {
                    sb.AppendLine($"Kernel Activity:   booting / loading drivers in bulk ({s.KdModuleEventsLast10s} module events in the last 10 s).");
                    sb.AppendLine($"   Break-ins are dropped while this lasts, so kd_break may time out: wait 30-60 s and retry.");
                    sb.AppendLine($"   These are informational events, not a stop; guest tools work once VMware Tools reports running.");
                }

                if (s.KdRebootDetected)
                {
                    sb.AppendLine($"TARGET REBOOTED:   The kernel restarted since the last kd_continue.");
                    sb.AppendLine($"   Previous state (and any earlier BSOD) is gone.");
                    if (s.KdExecStatus == DebugExecutionStatus.Break)
                        sb.AppendLine($"   The debugger reconnected at the initial breakpoint; call kd_continue to let the OS finish booting.");
                    else if (s.VmTools == VmToolsState.Running)
                    {
                        sb.AppendLine($"   The OS has booted (VMware Tools is up) but the kernel did NOT re-attach to this debugger");
                        sb.AppendLine($"   session (this is what a graceful restart does). Call kd_disconnect then kd_connect; if kd_connect");
                        sb.AppendLine($"   reports the engine is parked, kernel-debug tools need an MCP server restart (guest/VM tools keep working).");
                        sb.AppendLine($"   Next time call kd_disconnect BEFORE restarting the guest (guest_run_command does this for 'shutdown /r').");
                    }
                    else
                    {
                        sb.AppendLine($"   The debugger is waiting for the target to come back; poll get_system_state.");
                        sb.AppendLine($"   If it never does (auto-reboot off), kd_disconnect then vm_stop(hard=true) + vm_start.");
                    }
                }

                if (s.KdExecStatus == DebugExecutionStatus.Break)
                {
                    sb.AppendLine($"Break Reason:      {s.KdBreakReason ?? "unknown"}");

                    if (s.IsBugcheck)
                    {
                        sb.AppendLine($"BSOD DETECTED:     {s.BugcheckCode}");
                        sb.AppendLine($"   The OS has CRASHED. Guest ops will NOT work.");
                        sb.AppendLine($"   {ErrorMessages.BsodRecoveryOptions}");
                    }
                    else if (s.KdFatalExceptionPending)
                    {
                        sb.AppendLine($"FATAL EXCEPTION:   {s.KdBreakReason ?? "unhandled exception (second chance)"}");
                        sb.AppendLine($"   {ErrorMessages.FatalExceptionPending(s.KdBreakReason)}");
                    }
                }

                sb.AppendLine($"Pending Events:    {s.PendingImportantEventCount} important (stop/session events), " +
                              $"{s.PendingInformationalEventCount} informational (module/process/thread notifications; never a reason the target stopped)");
                sb.AppendLine($"Wait Pending:      {s.KdWaitPending}");
                if (s.KdEngineBusyWith != null)
                {
                    sb.AppendLine($"ENGINE BUSY:       {s.KdEngineBusyWith}");
                    sb.AppendLine($"   A command that outlived its tool timeout is still running on the single debugger thread.");
                    sb.AppendLine($"   Every kernel tool waits behind it (and times out) until it finishes; symbol loading can take");
                    sb.AppendLine($"   minutes the first time. Wait and poll get_system_state; do not re-issue the command.");
                }
            }

            var recent = state.GetRecentDebugEvents?.Invoke() ?? new();
            if (recent.Count > 0)
            {
                sb.AppendLine($"Recent Debug Events (last {recent.Count} important ones, oldest first; module/process/thread events omitted; may include those in the banner above):");
                foreach (var evt in recent)
                    sb.AppendLine($"   {evt}");
            }
            sb.AppendLine();

            // Guest operations
            sb.AppendLine($"Guest Ops Available: {s.GuestOpsAvailable}");
            if (!s.GuestOpsAvailable)
            {
                if (s.VmPower != VmPowerState.Running)
                    sb.AppendLine($"   -> VM is {s.VmPower}");
                else if (s.KdConnected && s.KdExecStatus == DebugExecutionStatus.Break)
                {
                    if (s.IsBugcheck)
                        sb.AppendLine($"   -> BSOD: OS has crashed");
                    else if (s.KdFatalExceptionPending)
                        sb.AppendLine($"   -> Fatal exception: the OS is crashed; kd_continue would bugcheck it, not resume it");
                    else
                        sb.AppendLine($"   -> Kernel debugger has frozen the VM (call kd_continue)");
                }
                else if (s.VmTools != VmToolsState.Running)
                    sb.AppendLine($"   -> VMware Tools: {s.VmTools}");
            }
            sb.AppendLine();

            // User-mode debug
            if (s.FridaState != null)
                sb.AppendLine($"Frida:             {s.FridaState}");
            if (s.DbgsrvState != null)
                sb.AppendLine($"dbgsrv:            {s.DbgsrvState}");
            if (s.UserDebugSessions.Count > 0)
            {
                sb.AppendLine("Active Debug Sessions:");
                foreach (var session in s.UserDebugSessions)
                    sb.AppendLine($"   - [{session.Type}] PID {session.Pid} ({session.ProcessName})");
            }

            return Task.FromResult(sb.ToString());
        });
    }
}
