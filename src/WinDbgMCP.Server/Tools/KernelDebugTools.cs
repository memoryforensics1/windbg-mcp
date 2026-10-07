using System.ComponentModel;
using ModelContextProtocol.Server;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.KernelDebug;
using WinDbgMCP.Server.KernelDebug.Interop;
using WinDbgMCP.Server.State;

namespace WinDbgMCP.Server.Tools;

[McpServerToolType]
public static class KernelDebugTools
{
    [McpServerTool(Name = "kd_connect"), Description(
        "Connect to the kernel debug target via KDNET or serial. " +
        "The VM must be running with debug boot enabled. " +
        "Target will break on connect (initial breakpoint). " +
        "Optionally provide a raw connection string (e.g. 'net:port=50000,key=...' or 'com:pipe,port=\\\\.\\pipe\\com_1,resets=0,reconnect'); " +
        "if omitted, connects using the defaults from appsettings.json.")]
    public static Task<string> KdConnect(
        StateCoordinator state,
        DbgEngManager dbgEng,
        ServerConfig config,
        [Description("Optional raw DbgEng connection string. If omitted, uses appsettings.json defaults.")] string? connectionString = null,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("kd_connect", async () =>
        {
            try
            {
                var result = await dbgEng.ConnectKernelAsync(connectionString, ct);

                // Update state coordinator with actual transport type
                KdTransport transport;
                if (connectionString != null)
                    transport = connectionString.StartsWith("net:", StringComparison.OrdinalIgnoreCase) ? KdTransport.KDNET : KdTransport.Serial;
                else
                    transport = config.KernelDebug.Transport.Equals("kdnet", StringComparison.OrdinalIgnoreCase) ? KdTransport.KDNET : KdTransport.Serial;
                state.SetKdConnected(transport);

                return result;
            }
            catch (OperationCanceledException)
            {
                return "kd_connect timed out. The kernel debug target did not respond within " +
                       $"{config.Timeouts.KdConnectSeconds}s. Verify: " +
                       "(1) VM is running with debug boot enabled (bcdedit /debug on + KDNET configured). " +
                       "(2) The KDNET port/key matches appsettings.json. " +
                       "(3) No other debugger is already attached. " +
                       "(4) Host firewall allows UDP port inbound.";
            }
            catch (Exception ex)
            {
                return $"kd_connect failed: {ex.GetType().Name}: {ex.Message}";
            }
        });
    }

    [McpServerTool(Name = "kd_disconnect"), Description(
        "Disconnect from the kernel debug target. " +
        "Resumes the target before disconnecting so the VM keeps running.")]
    public static Task<string> KdDisconnect(
        StateCoordinator state,
        DbgEngManager dbgEng,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("kd_disconnect", async () =>
        {
            try
            {
                var result = await dbgEng.DisconnectAsync();
                if (result.Detached)
                    state.SetKdDisconnected();
                return result.Detached
                    ? result.Message
                    : "NOT DETACHED: " + result.Message + " The debugger is still connected.";
            }
            catch (Exception ex)
            {
                // Only report what is true: the session is gone only if the engine
                // really dropped its client.
                if (!dbgEng.IsConnected)
                    state.SetKdDisconnected();
                return $"kd_disconnect failed: {ex.GetType().Name}: {ex.Message}. " +
                       (dbgEng.IsConnected
                           ? "The debugger is still connected; call get_system_state and retry."
                           : "The session is gone; call kd_connect to attach again.");
            }
        });
    }

    [McpServerTool(Name = "kd_break"), Description(
        "Break into a running target (equivalent to Ctrl+Break in WinDbg). " +
        "Target must be running. After breaking, use kd_execute to inspect state.")]
    public static Task<string> KdBreak(
        StateCoordinator state,
        DbgEngManager dbgEng,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("kd_break", async () =>
        {
            try
            {
                var result = await dbgEng.BreakAsync();
                if (!result.StartsWith("Target halted", StringComparison.Ordinal))
                    return result;

                // A probe failure must not be reported as a failed break; the state
                // refresh retries the probe on the next tool call anyway.
                try
                {
                    var (isBugcheck, bugcheckCode) = await dbgEng.DetectBugcheckAsync();
                    state.SetBsodProbed();
                    if (isBugcheck)
                    {
                        state.SetBsodDetected(bugcheckCode);
                        return result + $"\n\nWARNING: BSOD DETECTED (bugcheck {bugcheckCode}). " +
                               "The OS has crashed; guest operations will NOT work. " +
                               ErrorMessages.BsodRecoveryOptions;
                    }
                }
                catch (Exception ex)
                {
                    return result + $"\n\n(BSOD probe failed: {ex.GetType().Name}; call get_system_state to re-check.)";
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                return "kd_break timed out. The target may not be in a state where it can break.";
            }
            catch (Exception ex)
            {
                return $"kd_break failed: {ex.GetType().Name}: {ex.Message}";
            }
        });
    }

    [McpServerTool(Name = "kd_continue"), Description(
        "Resume target execution (go). Returns immediately — the target starts running. " +
        "Use kd_wait_for_event to check for breakpoint hits, or kd_break to halt again. " +
        "Guest operations (vm_execute, vm_send_file) require the target to be running.")]
    public static Task<string> KdContinue(
        StateCoordinator state,
        DbgEngManager dbgEng,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("kd_continue", async () =>
        {
            var wasBugcheck = state.State.IsBugcheck;
            var bugcheckCode = state.State.BugcheckCode;

            try
            {
                var result = await dbgEng.ContinueAsync();
                return wasBugcheck
                    ? result + "\n\n" + ErrorMessages.BsodContinueWarning(bugcheckCode)
                    : result;
            }
            catch (OperationCanceledException)
            {
                return "kd_continue timed out.";
            }
            catch (Exception ex)
            {
                return $"kd_continue failed: {ex.GetType().Name}: {ex.Message}";
            }
        });
    }

    [McpServerTool(Name = "kd_step"), Description(
        "Step one instruction. Mode 'over' steps over calls, 'into' steps into calls. " +
        "Target must be at a breakpoint (broken). Returns the new instruction pointer and disassembly.")]
    public static Task<string> KdStep(
        StateCoordinator state,
        DbgEngManager dbgEng,
        [Description("Step mode: 'into' (step into calls) or 'over' (step over calls, default)")] string mode = "over",
        CancellationToken ct = default)
    {
        return state.RunToolAsync("kd_step", async () =>
        {
            try
            {
                return await dbgEng.StepAsync(mode);
            }
            catch (OperationCanceledException)
            {
                return "kd_step timed out. The target may be in an unexpected state.";
            }
            catch (Exception ex)
            {
                return $"kd_step failed: {ex.GetType().Name}: {ex.Message}";
            }
        });
    }

    [McpServerTool(Name = "kd_execute"), Description(
        "Execute any WinDbg command and return output. Target must be halted (at breakpoint). " +
        "Examples: 'k' (stack), 'r' (registers), 'lm' (modules), '!process 0 0', '!analyze -v', " +
        "'db addr' (memory), 'u addr' (disassemble), 'bp symbol' (set breakpoint). " +
        "Execution-control commands (g, t, p, gu, wt) are BLOCKED — use kd_continue/kd_step instead.")]
    public static Task<string> KdExecute(
        StateCoordinator state,
        DbgEngManager dbgEng,
        [Description("WinDbg command to execute")] string command,
        [Description("Timeout in seconds (default 30)")] int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("kd_execute", async () =>
        {
            // Check for blocked commands
            var (isBlocked, blockedCmd, suggestion) = DbgEngConstants.CheckCommand(command);
            if (isBlocked)
            {
                return $"BLOCKED: The command '{blockedCmd}' changes execution state and " +
                       $"would hang the debugger if run via kd_execute. {suggestion}";
            }

            try
            {
                return await dbgEng.ExecuteCommandAsync(command, timeoutSeconds);
            }
            catch (OperationCanceledException)
            {
                return $"Command '{command}' timed out after {timeoutSeconds}s. " +
                       "The command may be waiting for something. Try kd_break to interrupt.";
            }
            catch (Exception ex)
            {
                return $"kd_execute failed: {ex.GetType().Name}: {ex.Message}";
            }
        });
    }

    [McpServerTool(Name = "kd_wait_for_event"), Description(
        "Wait for a debug event (breakpoint hit, exception, etc.) with a timeout. " +
        "Use this after kd_continue + set_breakpoint to wait for the breakpoint to be hit. " +
        "ALWAYS returns within timeout — never hangs. If no event, target keeps running.")]
    public static Task<string> KdWaitForEvent(
        StateCoordinator state,
        DbgEngManager dbgEng,
        [Description("How many seconds to wait for an event (default 10, max 120)")] int timeoutSeconds = 10,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("kd_wait_for_event", async () =>
        {
            // Clamp timeout
            timeoutSeconds = Math.Clamp(timeoutSeconds, 1, 120);

            try
            {
                var result = await dbgEng.WaitForEventAsync(timeoutSeconds);

                // Check for BSOD if we received an event
                if (result.Contains("halted", StringComparison.OrdinalIgnoreCase))
                {
                    var (isBugcheck, bugcheckCode) = await dbgEng.DetectBugcheckAsync();
                    state.SetBsodProbed();
                    if (isBugcheck)
                    {
                        state.SetBsodDetected(bugcheckCode);
                        return result + $"\n\nWARNING: BSOD DETECTED (bugcheck {bugcheckCode}). " +
                               "The OS has crashed; guest operations will NOT work. " +
                               ErrorMessages.BsodRecoveryOptions;
                    }
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                return $"Wait cancelled after {timeoutSeconds}s. Target is still running.";
            }
            catch (Exception ex)
            {
                return $"kd_wait_for_event failed: {ex.GetType().Name}: {ex.Message}";
            }
        });
    }
}
