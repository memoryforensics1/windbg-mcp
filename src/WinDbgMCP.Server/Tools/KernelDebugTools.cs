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
                var budget = config.Timeouts.KdConnectSeconds + config.Timeouts.KdInitialBreakSeconds + 15;
                return "kd_connect timed out: the kernel debug target did not answer within " +
                       $"{budget}s. If the VM was started or reset less than a minute ago, KDNET is still initialising: " +
                       "wait 30-60 s and retry (nothing is wrong). Otherwise verify: " +
                       "(1) the VM is running with debug boot enabled (bcdedit /debug on + KDNET configured); " +
                       "(2) the KDNET port/key matches appsettings.json; " +
                       "(3) no other debugger is attached; " +
                       "(4) the host firewall allows the UDP port inbound.";
            }
            catch (Exception ex)
            {
                return $"kd_connect failed: {ex.GetType().Name}: {ex.Message}";
            }
        });
    }

    [McpServerTool(Name = "kd_disconnect"), Description(
        "Disconnect from the kernel debug target. " +
        "Resumes the target before disconnecting so the VM keeps running (at a BSOD or a fatal exception " +
        "that means the kernel proceeds to its crash dump and reboot).")]
    public static Task<string> KdDisconnect(
        StateCoordinator state,
        DbgEngManager dbgEng,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("kd_disconnect", async () =>
        {
            var crashed = state.State.IsBugcheck || state.State.KdFatalExceptionPending;
            var crashNote = crashed
                ? " The target was halted at a crash, so resuming it means the kernel now writes its dump and reboots " +
                  "on its own; guest tools work once VMware Tools is back, and kd_connect only after that."
                : "";
            try
            {
                var result = await dbgEng.DisconnectAsync();
                if (result.Detached)
                    state.SetKdDisconnected();
                return result.Detached
                    ? result.Message + crashNote
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
                    var (isBugcheck, bugcheckCode, bugcheckArgs, lastEvent) = await dbgEng.DetectBugcheckAsync();
                    state.SetBsodProbed(lastEvent, dbgEng.SecondChancePending && !isBugcheck);
                    if (isBugcheck)
                    {
                        state.SetBsodDetected(bugcheckCode, bugcheckArgs);
                        return result + $"\n\nWARNING: BSOD DETECTED (bugcheck {bugcheckCode}" +
                               (bugcheckArgs != null ? $", arguments {bugcheckArgs}" : "") + "). " +
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
                // The break-in logic itself explains boot floods; this is the outer
                // timeout: the engine thread could not even be reached.
                var busy = dbgEng.BusyDescription;
                if (busy != null)
                    return $"kd_break could not reach the engine: {busy} still occupies the debugger thread. " +
                           EngineBusyException.Explain(busy) + " Wait and retry.";
                if (state.State.KdModuleFlood)
                    return "kd_break could not reach the engine: the kernel is booting or loading drivers in bulk and " +
                           "drops break-ins until that settles (normal after a reboot). Wait 30-60 s and retry.";
                return "kd_break could not reach the engine within its timeout. The target may be rebooting, paused, " +
                       "or busy; call get_system_state, then retry.";
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
        "Guest operations (guest_run_command, guest_transfer_*) require the target to be running. " +
        "At a BSOD or a fatal exception this does not resume the OS: the kernel proceeds to its crash dump and reboot.")]
    public static Task<string> KdContinue(
        StateCoordinator state,
        DbgEngManager dbgEng,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("kd_continue", async () =>
        {
            var wasBugcheck = state.State.IsBugcheck;
            var bugcheckCode = state.State.BugcheckCode;
            var wasInitialBreakAfterReboot = state.State.KdRebootDetected;

            try
            {
                var result = await dbgEng.ContinueAsync();
                if (wasBugcheck)
                    return result + " The kernel is now in its crash path, NOT running the OS: guest tools stay unavailable " +
                           "until it has rebooted.\n\n" + ErrorMessages.BsodContinueWarning(bugcheckCode);
                if (result.Contains("(gn)", StringComparison.Ordinal))
                    return result;
                if (wasInitialBreakAfterReboot)
                    return result + " The OS boots now from the initial breakpoint. Expect a burst of informational module " +
                           "events (not stops); the kernel drops break-ins meanwhile, so kd_break may need up to 60 s; " +
                           "guest tools work once get_system_state shows VMware Tools: Running (typically 30-90 s).";
                return result + " Guest operations are now available. If you set breakpoints, call kd_wait_for_event " +
                       "to check for hits, or call kd_break to halt the target manually.";
            }
            catch (OperationCanceledException)
            {
                var busy = dbgEng.BusyDescription;
                return busy != null
                    ? $"kd_continue could not reach the engine: {busy} still occupies the debugger thread. " +
                      EngineBusyException.Explain(busy) + " The target is still halted. Wait and retry."
                    : "kd_continue could not reach the engine within its timeout; the target is still halted. " +
                      "Call get_system_state and retry.";
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
                var busy = dbgEng.BusyDescription;
                return busy != null
                    ? $"kd_step could not reach the engine: {busy} still occupies the debugger thread. " +
                      EngineBusyException.Explain(busy) + " Wait and retry."
                    : "kd_step did not complete within its timeout: the stepped instruction may have started a long " +
                      "operation and the target is running. Call get_system_state; kd_break halts it again.";
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
        "Execution-control commands (g, t, p, gu, wt) are BLOCKED — use kd_continue/kd_step instead. " +
        "Symbol-loading commands (!analyze -v, first lm/k/.reload) can take minutes the first time: pass " +
        "timeoutSeconds=300 for them. A command that outlives its timeout keeps running on the single " +
        "debugger thread; other kernel tools then say the engine is busy until it finishes.")]
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
                return $"Command '{command}' did not finish within {timeoutSeconds}s and is STILL RUNNING on the " +
                       "debugger thread (a running engine command cannot be cancelled). Until it finishes every " +
                       "other kernel tool reports the engine as busy, and get_system_state shows what is running. " +
                       "Commands that load symbols (!analyze -v, the first lm/k/.reload) often take several minutes " +
                       "on the first run. Do NOT re-issue the command: wait, poll get_system_state, and read the " +
                       "output when it completes by running the same command again once the engine is idle " +
                       "(symbols are cached by then, so it is fast).";
            }
            catch (Exception ex)
            {
                return $"kd_execute failed: {ex.GetType().Name}: {ex.Message}";
            }
        });
    }

    [McpServerTool(Name = "kd_wait_for_event"), Description(
        "Wait for a debug event (breakpoint hit, exception, etc.) with a timeout. " +
        "Use this after kd_execute('bp ...') + kd_continue to wait for the breakpoint to be hit. " +
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
                    var (isBugcheck, bugcheckCode, bugcheckArgs, lastEvent) = await dbgEng.DetectBugcheckAsync();
                    state.SetBsodProbed(lastEvent, dbgEng.SecondChancePending && !isBugcheck);
                    if (isBugcheck)
                    {
                        state.SetBsodDetected(bugcheckCode, bugcheckArgs);
                        return result + $"\n\nWARNING: BSOD DETECTED (bugcheck {bugcheckCode}" +
                               (bugcheckArgs != null ? $", arguments {bugcheckArgs}" : "") + "). " +
                               "The OS has crashed; guest operations will NOT work. " +
                               ErrorMessages.BsodRecoveryOptions;
                    }
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                // Don't assert "still running": the engine could not be reached before
                // the outer timeout, which has two very different causes.
                if (state.State.KdModuleFlood)
                    return $"Wait returned after {timeoutSeconds}s without reaching the engine: the kernel is booting " +
                           $"or loading drivers in bulk ({state.State.KdModuleEventsLast10s} module events in the last 10 s) " +
                           "and drops the break-in the engine needs to hand the thread over. Nothing is wrong and no " +
                           "stop event happened. Wait 30-60 s and call again; get_system_state shows 'Kernel Activity' " +
                           "until the burst is over, and guest tools work once VMware Tools reports running.";
                return $"Wait returned after {timeoutSeconds}s without reaching the engine wait. " +
                       "The target is rebooting (no debuggee yet) or busy; kernel tools queue behind it until it is back. " +
                       "Poll get_system_state (it shows TARGET REBOOTED when the kernel re-attaches).";
            }
            catch (Exception ex)
            {
                return $"kd_wait_for_event failed: {ex.GetType().Name}: {ex.Message}";
            }
        });
    }
}
