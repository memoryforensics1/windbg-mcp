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
    public static async Task<string> GetSystemState(
        StateCoordinator state,
        CancellationToken ct = default)
    {
        await state.RefreshStateAsync();
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

            if (s.KdRebootDetected)
            {
                sb.AppendLine($"TARGET REBOOTED:   The kernel restarted since the last kd_continue.");
                sb.AppendLine($"   Previous state (and any earlier BSOD) is gone. The debugger reconnected");
                sb.AppendLine($"   at the initial breakpoint; call kd_continue to let the OS finish booting.");
            }

            if (s.KdExecStatus == DebugExecutionStatus.Break)
            {
                sb.AppendLine($"Break Reason:      {s.KdBreakReason ?? "unknown"}");

                if (s.IsBugcheck)
                {
                    sb.AppendLine($"BSOD DETECTED:     {s.BugcheckCode}");
                    sb.AppendLine($"   The OS has CRASHED. Guest ops will NOT work.");
                    sb.AppendLine($"   Run kd_execute('!analyze -v') or vm_snapshot_restore.");
                }
            }

            sb.AppendLine($"Pending Events:    {s.PendingEventCount}");
            sb.AppendLine($"Wait Pending:      {s.KdWaitPending}");
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

        return sb.ToString();
    }
}
