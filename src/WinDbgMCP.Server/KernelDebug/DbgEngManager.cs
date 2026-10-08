using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ClrDebug;
using ClrDebug.DbgEng;
using Microsoft.Extensions.Logging;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.KernelDebug.Interop;
using WinDbgMCP.Server.KernelDebug.Models;
using WinDbgMCP.Server.State;

namespace WinDbgMCP.Server.KernelDebug;

/// <summary>
/// Manages the DbgEng kernel debugging session.
/// All DbgEng COM operations are marshaled to the dedicated DbgEngThread.
/// </summary>
public sealed class DbgEngManager : IDisposable
{
    private readonly DbgEngThread _thread;
    private readonly ServerConfig _config;
    private readonly ILogger<DbgEngManager> _logger;
    private readonly OutputCapture _outputCapture = new();
    private readonly DebugEventCallbacks _eventCallbacks;

    private DebugClient? _client;
    private bool _disposed;

    // nt!RtlpBreakWithStatusInstruction from the KdDebuggerDataBlock; 0 if unknown
    private ulong _breakWithStatusAddr;
    // Set before every SetInterrupt we issue; fallback discriminator when the
    // data block is unavailable
    private volatile bool _interruptRequested;

    // Interrupt timers are periodic: a break-in request sent while the kernel is
    // busy (e.g. the module-load flood right after a reboot) is silently dropped,
    // and with a one-shot timer WaitForEvent(INFINITE) would then never return.
    private const int InterruptRetryMs = 2000;
    private const int PumpYieldMs = 5000;

    // E_PENDING: WaitForEvent returned because of SetInterrupt(DEBUG_INTERRUPT_EXIT)
    private static readonly HRESULT WaitExited = (HRESULT)0x8000000AU;

    private const long DbgStatusControlC = 1;
    private const long DbgStatusBugcheckFirst = 3;
    private const long DbgStatusBugcheckSecond = 4;
    private const long DbgStatusFatal = 5;

    public bool IsConnected => _client != null;
    public int PendingEventCount => _eventCallbacks.PendingCount;
    public int PendingInformationalEventCount => _eventCallbacks.PendingInformationalCount;
    public bool RebootDetected => _eventCallbacks.RebootDetected;
    /// <summary>A second-chance (unhandled) exception is the current event: the next resume bugchecks the OS.</summary>
    public bool SecondChancePending => _eventCallbacks.SecondChancePending;
    public List<DebugEvent> DrainEvents() => _eventCallbacks.DrainEvents();
    public List<DebugEvent> RecentEvents => _eventCallbacks.RecentEvents;

    /// <summary>Called on the DbgEng thread for every queued event; must not block.</summary>
    public Action<DebugEvent>? EventRaised
    {
        get => _eventCallbacks.EventRaised;
        set => _eventCallbacks.EventRaised = value;
    }

    public DbgEngManager(DbgEngThread thread, ServerConfig config, ILogger<DbgEngManager> logger)
    {
        _thread = thread;
        _config = config;
        _logger = logger;
        _eventCallbacks = new DebugEventCallbacks(logger);

        // Set up the event pump action
        _thread.PumpEventsAction = PumpEvents;
    }

    // ═══════════════════════════════════════════════════════════════
    //  CONNECTION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Connect to a kernel debug target via KDNET or serial.
    /// </summary>
    public async Task<string> ConnectKernelAsync(string? connectionString = null, CancellationToken ct = default)
    {
        // The work item covers attach + the initial-break wait; the outer timeout
        // must not expire first or the target is left halted behind a live client.
        var timeout = TimeSpan.FromSeconds(_config.Timeouts.KdConnectSeconds + _config.Timeouts.KdInitialBreakSeconds + 15);

        // A stale client's pump may be parked in a target-less wait: try to free the
        // thread, and if it does not answer, report the engine as wedged. A wedge
        // recorded earlier is only a suspicion (the target may have been mid-reboot),
        // so every connect re-probes instead of failing on the flag.
        if (_client != null)
        {
            WakeEngineThread();
            try
            {
                // Stop the pump from re-entering its wait between this probe and
                // the connect work item below.
                await _thread.ExecuteAsync(() => { _thread.PumpEnabled = false; }, ThreadGrabTimeout);
            }
            catch (OperationCanceledException)
            {
                if (!EngineHasNoDebuggee)
                    throw new InvalidOperationException(
                        "The engine thread is busy with the previous session (the target did not answer a " +
                        "break-in); wait a few seconds and call kd_connect again.");
                MarkEngineWedged();
                throw new InvalidOperationException(EngineWedgedMessage);
            }
            _engineWedged = false;
        }
        else
        {
            // No client, but the thread may still be parked in the wait of a session
            // that was dropped (hard vm_stop / snapshot restore with a parked engine).
            // Say so instead of attaching behind it and blaming the KDNET settings.
            try
            {
                await _thread.ExecuteAsync(() => { }, TimeSpan.FromSeconds(3));
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "The engine thread is not responding: it is still parked in the wait of a previous session " +
                    "whose target went away. Kernel-debug tools need an MCP server restart; guest/VM tools still work.");
            }
        }

        return await _thread.ExecuteAsync(() =>
        {
            // Reaching here with a live client means the coordinator already
            // considers the session lost (NoDebuggee); tear the old one down
            // rather than leaking it or wedging kd_connect/kd_disconnect.
            if (_client != null)
            {
                _logger.LogWarning("Stale DbgEng client found on connect; ending old session");
                _thread.PumpEnabled = false;
                // ACTIVE_DETACH is the mode verified live to resume a halted kernel.
                try { _client.TryEndSession(DEBUG_END.ACTIVE_DETACH); } catch { }
                _client = null;
            }

            _logger.LogInformation("Creating DbgEng client...");

            // Find Windows SDK debugger directory for dbgeng.dll
            var debuggerDir = FindDebuggerDirectory();
            if (debuggerDir != null)
            {
                NativeMethods.SetDllDirectory(debuggerDir);
                _logger.LogInformation("Using dbgeng from: {Dir}", debuggerDir);
            }

            // Load dbgeng.dll and get DebugCreate
            var hDbgEng = NativeMethods.LoadLibrary("dbgeng.dll");
            if (hDbgEng == IntPtr.Zero)
                throw new InvalidOperationException(
                    "Failed to load dbgeng.dll. Install Debugging Tools for Windows " +
                    "(part of Windows SDK) or WinDbg Preview from the Microsoft Store.");

            var pDebugCreate = NativeMethods.GetProcAddress(hDbgEng, "DebugCreate");
            if (pDebugCreate == IntPtr.Zero)
                throw new InvalidOperationException("Failed to find DebugCreate in dbgeng.dll");

            var debugCreate = Marshal.GetDelegateForFunctionPointer<Interop.DebugCreateDelegate>(pDebugCreate);

            // Create the debug client
            var hr = debugCreate(DebugClient.IID_IDebugClient, out var pClient);
            if (hr != HRESULT.S_OK)
                throw new InvalidOperationException($"DebugCreate failed: {hr}");

            _client = new DebugClient(pClient);
            _eventCallbacks.ClearEvents();
            _eventCallbacks.ClearBreakingEventFlag();
            _eventCallbacks.ClearRebootFlag();
            _eventCallbacks.TryTakePendingBreakIn(out _);
            _breakWithStatusAddr = 0;
            _interruptRequested = false;

            // Set callbacks
            _client.OutputCallbacks = _outputCapture;
            _client.EventCallbacks = _eventCallbacks;

            // Configure engine options
            _client.Control.EngineOptions = DEBUG_ENGOPT.INITIAL_BREAK;

            // Configure symbol path
            _client.Symbols.SymbolPath = _config.KernelDebug.SymbolPath;
            _logger.LogInformation("Symbol path: {Path}", _config.KernelDebug.SymbolPath);

            // Build connection string
            string connStr;
            KdTransport transport;
            if (connectionString != null)
            {
                connStr = connectionString;
                transport = connectionString.StartsWith("net:", StringComparison.OrdinalIgnoreCase)
                    ? KdTransport.KDNET
                    : KdTransport.Serial;
            }
            else if (_config.KernelDebug.Transport.Equals("kdnet", StringComparison.OrdinalIgnoreCase))
            {
                connStr = $"net:port={_config.KernelDebug.Kdnet.Port},key={_config.KernelDebug.Kdnet.Key}";
                transport = KdTransport.KDNET;
            }
            else if (_config.KernelDebug.Transport.Equals("serial", StringComparison.OrdinalIgnoreCase))
            {
                connStr = $"com:pipe,port={_config.KernelDebug.Serial.PipeName},resets=0,reconnect";
                transport = KdTransport.Serial;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unknown kernel debug transport: '{_config.KernelDebug.Transport}'. " +
                    "Use 'kdnet' or 'serial'.");
            }

            _logger.LogInformation("Attaching kernel: {ConnStr}", connStr);

            // Attach to kernel
            var attachHr = _client.TryAttachKernel(DEBUG_ATTACH.KERNEL_CONNECTION, connStr);
            if (attachHr != HRESULT.S_OK)
                throw new InvalidOperationException(
                    $"AttachKernel failed: {attachHr}. " + ErrorMessages.KdConnectFailed);

            _logger.LogInformation("AttachKernel succeeded, waiting for initial breakpoint...");

            var initialBreakMs = _config.Timeouts.KdInitialBreakSeconds * 1000;
            var waitHr = WaitForEventInterruptible(initialBreakMs, initialBreakMs, breakInEarly: false);

            if (waitHr == HRESULT.S_OK)
            {
                _logger.LogInformation("Connected. Target at initial breakpoint.");
                ReadBreakWithStatusAddress();
                ClassifyPendingBreakIn();

                // No eager ".reload /f": symbols load lazily on first use, and a slow
                // symbol server here once pushed kd_connect past its timeout, leaving
                // the target halted with a live client the coordinator didn't know about.

                return $"Connected to kernel via {transport}. Target is at initial breakpoint. " +
                       "You can now use kd_execute to run WinDbg commands, or kd_continue to resume.";
            }
            else if (WaitAbandoned(waitHr))
            {
                // Timeout — target is running but we're connected
                _logger.LogInformation("Connected. Target is running (no initial break within timeout).");
                _thread.PumpEnabled = true;

                return $"Connected to kernel via {transport}. Target is running freely. " +
                       "Call kd_break to halt the target for inspection.";
            }
            else
            {
                // Real failure
                _client.TryEndSession(DEBUG_END.ACTIVE_TERMINATE);
                _client = null;
                throw new InvalidOperationException(
                    $"WaitForEvent failed: {waitHr}. " + ErrorMessages.KdConnectFailed);
            }
        }, timeout);
    }

    /// <summary>
    /// Disconnect from the kernel debug target.
    /// Resumes the target first so the VM keeps running after disconnect.
    /// Returns <c>Detached == false</c> when the session is still live; callers
    /// must then keep treating the debugger as connected.
    /// </summary>
    public async Task<DetachResult> DisconnectAsync()
    {
        if (_client == null)
            return new DetachResult(true, "Not connected.");

        // Step 1: If target is at BREAK, resume it by setting GO and letting
        // the event pump dispatch it (kernel targets need INFINITE waits, and the
        // pump's wait is where the GO is dispatched).
        bool needsResume;
        var passedBackUnhandled = false;
        try
        {
            WakeEngineThread();
            // Must outlast the pump's yield period, which is the only thing that
            // frees the thread while a live target is running.
            needsResume = await _thread.ExecuteAsync(() =>
            {
                if (_client == null) return false;

                try
                {
                    var status = _client.Control.ExecutionStatus;
                    if (status == DEBUG_STATUS.BREAK)
                    {
                        // Same rule as kd_continue: a second-chance exception must be
                        // handed back to the kernel (gn), or a plain GO re-faults and
                        // the detach below then delivers it anyway - as a BSOD right
                        // after "Target has been resumed".
                        var unhandled = _eventCallbacks.SecondChancePending;
                        _eventCallbacks.ClearSecondChancePending();
                        passedBackUnhandled = unhandled;
                        _client.Control.TrySetExecutionStatus(unhandled ? DEBUG_STATUS.GO_NOT_HANDLED : DEBUG_STATUS.GO);
                        _thread.PumpEnabled = true;
                        return true;
                    }
                }
                catch { }
                return false;
            }, ThreadGrabTimeout);
        }
        catch (OperationCanceledException)
        {
            if (!EngineHasNoDebuggee)
                return new DetachResult(false,
                    "kd_disconnect could not get hold of the engine thread in time (the target did not answer " +
                    "a break-in: busy, e.g. writing a crash dump); it is still attached. Try kd_disconnect again.");
            // Not detached: the session stays live (and tracked) so the model sees
            // TARGET REBOOTED if the kernel comes back, and can detach then.
            MarkEngineWedged();
            return new DetachResult(false, EngineWedgedMessage);
        }

        if (needsResume)
        {
            // Give the pump time to dispatch the GO via WaitForEvent(INFINITE).
            await Task.Delay(3000);
        }

        // Step 2: Disconnect (wake the thread again: the pump re-entered its wait
        // to dispatch the GO).
        WakeEngineThread();

        try
        {
            return await _thread.ExecuteAsync(() =>
            {
                _thread.PumpEnabled = false;

                try
                {
                    _client?.TryEndSession(DEBUG_END.ACTIVE_DETACH);
                }
                catch { }

                _client = null;
                _logger.LogInformation("Disconnected from kernel debugger.");
                return new DetachResult(true, passedBackUnhandled
                    ? "Disconnected from kernel debugger. The target was resumed with its unhandled exception " +
                      "passed back to the kernel (gn): expect it to BSOD and reboot on its own now; " +
                      "kd_connect once the OS is back up if you need the debugger."
                    : "Disconnected from kernel debugger. Target has been resumed.");
            }, ThreadGrabTimeout + TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            if (!EngineHasNoDebuggee)
                return new DetachResult(false,
                    "kd_disconnect could not get hold of the engine thread in time; the target is still " +
                    "attached. Call get_system_state, then try kd_disconnect again.");
            MarkEngineWedged();
            return new DetachResult(false, EngineWedgedMessage);
        }
    }

    private bool EngineHasNoDebuggee => _eventCallbacks.LastExecutionStatus == DEBUG_STATUS.NO_DEBUGGEE;
    private static readonly TimeSpan ThreadGrabTimeout = TimeSpan.FromMilliseconds(PumpYieldMs + 5000);

    // ═══════════════════════════════════════════════════════════════
    //  STATE QUERY
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Get the current execution status. Safe to call from any thread
    /// via the DbgEngThread.
    /// </summary>
    public DebugExecutionStatus GetExecutionStatus()
    {
        if (_client == null) return DebugExecutionStatus.NoDebuggee;

        try
        {
            // Use the cached value from event callbacks (thread-safe)
            var status = _eventCallbacks.LastExecutionStatus;
            return (DebugExecutionStatus)(int)status;
        }
        catch
        {
            return DebugExecutionStatus.Uninitialized;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  COMMAND EXECUTION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Execute a WinDbg command and capture its output.
    /// Must be called while target is in Break state.
    /// </summary>
    public async Task<string> ExecuteCommandAsync(string command, int timeoutSeconds = 30)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        return await _thread.ExecuteAsync(() =>
        {
            if (_client == null)
                throw new InvalidOperationException("Not connected.");

            _outputCapture.Clear();

            var hr = _client.Control.TryExecute(
                DEBUG_OUTCTL.THIS_CLIENT,
                command,
                DEBUG_EXECUTE.DEFAULT);

            var output = _outputCapture.GetAndClear();

            if (hr != HRESULT.S_OK && hr != HRESULT.S_FALSE)
                return $"Command failed (0x{(int)hr:X8}): {output}";

            return string.IsNullOrWhiteSpace(output) ? "(no output)" : output;
        }, timeout, $"kd_execute {command}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  EXECUTION CONTROL
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Break into a running target.
    /// </summary>
    public async Task<string> BreakAsync()
    {
        var timeout = TimeSpan.FromSeconds(_config.Timeouts.KdBreakSeconds);

        return await _thread.ExecuteAsync(() =>
        {
            if (_client == null)
                throw new InvalidOperationException("Not connected.");

            _thread.PumpEnabled = false;

            // Deliberately not RequestInterrupt(): a SetInterrupt failure must throw
            // here rather than let WaitForEvent(INFINITE) wedge the engine thread.
            _interruptRequested = true;
            try
            {
                _client.Control.SetInterrupt(DEBUG_INTERRUPT.ACTIVE);
            }
            catch
            {
                // Target is still running and nobody is in WaitForEvent — put the
                // pump back so a later breakpoint/BSOD is still caught.
                _interruptRequested = false;
                _thread.PumpEnabled = true;
                throw;
            }

            // A break-in sent while the kernel is busy is silently dropped, so
            // re-send it on every wait slice until the target halts or we time out.
            var waitHr = WaitForEventInterruptible(InterruptRetryMs, _config.Timeouts.KdBreakSeconds * 1000, breakInEarly: true);

            if (waitHr == HRESULT.S_OK)
            {
                ClassifyPendingBreakIn();

                // Get break reason
                _outputCapture.Clear();
                _client.Control.TryExecute(
                    DEBUG_OUTCTL.THIS_CLIENT, ".lastevent", DEBUG_EXECUTE.DEFAULT);
                var lastEvent = _outputCapture.GetAndClear().Trim();

                return $"Target halted. {lastEvent}\n" +
                       "Use kd_execute to inspect state (e.g., 'k' for stack, 'r' for registers).";
            }
            else
            {
                // Target never answered the break-in (busy, rebooting, or the kernel
                // booted without re-attaching). Keep watching it rather than wedge.
                _thread.PumpEnabled = true;
                return "Break-in sent repeatedly but the target did not halt within the timeout. " +
                       "It may be rebooting, booting without the debugger attached, or non-interruptible. " +
                       "Check get_system_state; if it says the kernel did not re-attach, kd_disconnect then kd_connect.";
            }
        }, timeout + TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Resume execution (go). Returns immediately.
    /// </summary>
    public async Task<string> ContinueAsync()
    {
        var timeout = TimeSpan.FromSeconds(5);

        return await _thread.ExecuteAsync(() =>
        {
            if (_client == null)
                throw new InvalidOperationException("Not connected.");

            _eventCallbacks.ClearBreakingEventFlag();
            _eventCallbacks.ClearRebootFlag();

            // At a second-chance exception a plain GO re-executes the faulting
            // instruction and the same exception comes straight back. GO_NOT_HANDLED
            // (WinDbg's gn) hands it to the kernel, which has no handler and bugchecks.
            var unhandled = _eventCallbacks.SecondChancePending;
            _eventCallbacks.ClearSecondChancePending();
            _client.Control.TrySetExecutionStatus(unhandled ? DEBUG_STATUS.GO_NOT_HANDLED : DEBUG_STATUS.GO);
            _thread.PumpEnabled = true;

            if (unhandled)
                return "Target resumed with the unhandled exception passed back to the kernel (gn). " +
                       "Expect a BSOD next: call kd_wait_for_event to catch the bugcheck break-in.";

            return "Target resumed. Guest operations are now available. " +
                   "If you set breakpoints, call kd_wait_for_event to check for hits, " +
                   "or call kd_break to halt the target manually.";
        }, timeout);
    }

    /// <summary>
    /// Step one instruction (into or over).
    /// </summary>
    public async Task<string> StepAsync(string mode = "over")
    {
        var timeout = TimeSpan.FromSeconds(_config.Timeouts.KdStepSeconds);

        return await _thread.ExecuteAsync(() =>
        {
            if (_client == null)
                throw new InvalidOperationException("Not connected.");

            var status = mode.ToLowerInvariant() switch
            {
                "into" => DEBUG_STATUS.STEP_INTO,
                "over" => DEBUG_STATUS.STEP_OVER,
                _ => throw new ArgumentException(
                    $"Invalid step mode '{mode}'. Use 'into' or 'over'.")
            };

            _client.Control.TrySetExecutionStatus(status);

            var stepMs = _config.Timeouts.KdStepSeconds * 1000;
            var waitHr = WaitForEventInterruptible(stepMs, stepMs, breakInEarly: false);

            if (waitHr == HRESULT.S_OK)
            {
                ClassifyPendingBreakIn();

                // Show where we ended up
                _outputCapture.Clear();
                _client.Control.TryExecute(
                    DEBUG_OUTCTL.THIS_CLIENT, "r rip", DEBUG_EXECUTE.DEFAULT);
                var rip = _outputCapture.GetAndClear().Trim();

                _outputCapture.Clear();
                _client.Control.TryExecute(
                    DEBUG_OUTCTL.THIS_CLIENT, "u . L1", DEBUG_EXECUTE.DEFAULT);
                var disasm = _outputCapture.GetAndClear().Trim();

                return $"Step {mode} complete.\n{rip}\n{disasm}";
            }
            else
            {
                _thread.PumpEnabled = true;
                return $"Step {mode} timed out; the target is still running. The instruction may have " +
                       "caused a long-running operation. Call kd_break to interrupt, or kd_wait_for_event to continue waiting.";
            }
        }, timeout + TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Wait for a debug event (breakpoint hit, exception, etc.).
    /// </summary>
    public async Task<string> WaitForEventAsync(int timeoutSeconds = 10)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        return await _thread.ExecuteAsync(() =>
        {
            if (_client == null)
                throw new InvalidOperationException("Not connected.");

            // While the pump is enabled a BREAK status is only ever its transient
            // yield (a GO is pending); with the pump off, BREAK means the target is
            // genuinely halted (pump caught an event, kd_break, or initial break).
            var pumpWasEnabled = _thread.PumpEnabled;
            _thread.PumpEnabled = false;

            // WaitForEvent on a halted target would never return (or implicitly
            // resume it), so report what was captured instead.
            if (!pumpWasEnabled && _client.Control.ExecutionStatus == DEBUG_STATUS.BREAK)
            {
                _outputCapture.Clear();
                _client.Control.TryExecute(
                    DEBUG_OUTCTL.THIS_CLIENT, ".lastevent", DEBUG_EXECUTE.DEFAULT);
                var lastEvent = _outputCapture.GetAndClear().Trim();

                return $"Target is already halted.\n{lastEvent}{FormatQueuedEvents()}\n" +
                       "Use kd_execute to inspect state, or kd_continue to resume.";
            }

            // At the timeout a break-in halts the target (resumed below) and EXIT is
            // sent as well, so the thread comes back whether or not a target exists.
            var waitMs = timeoutSeconds * 1000;
            var waitHr = WaitForEventInterruptible(waitMs, waitMs, breakInEarly: false);

            if (waitHr == HRESULT.S_OK)
                ClassifyPendingBreakIn();

            if (waitHr == HRESULT.S_OK && _eventCallbacks.HasBreakingEvent)
            {
                _outputCapture.Clear();
                _client.Control.TryExecute(
                    DEBUG_OUTCTL.THIS_CLIENT, ".lastevent", DEBUG_EXECUTE.DEFAULT);
                var lastEvent = _outputCapture.GetAndClear().Trim();

                return $"Debug event received! Target is now halted.\n{lastEvent}{FormatQueuedEvents()}";
            }

            if (waitHr == HRESULT.S_OK)
            {
                // A stray break-in (e.g. the pump's last yield) halted the target —
                // resume it so the "no event" answer is true. The pump dispatches the GO.
                _client.Control.TrySetExecutionStatus(DEBUG_STATUS.GO);
            }

            if (waitHr == HRESULT.S_OK || WaitAbandoned(waitHr))
            {
                _thread.PumpEnabled = true;
                var noTarget = _eventCallbacks.LastExecutionStatus == DEBUG_STATUS.NO_DEBUGGEE;
                return noTarget
                    ? $"No debug event received within {timeoutSeconds}s and the engine has no debuggee: the target " +
                      "is rebooting or booted without re-attaching. Check get_system_state (it says which); " +
                      "if the OS is up, kd_disconnect then kd_connect."
                    : $"No debug event received within {timeoutSeconds}s. Target is still running. " +
                      "You can: (1) Call kd_wait_for_event again to keep waiting, " +
                      "(2) Call kd_break to manually halt the target, or " +
                      "(3) Proceed with guest operations while the target runs.";
            }

            _logger.LogWarning("kd_wait_for_event: WaitForEvent returned {Hr}", waitHr);
            _eventCallbacks.EnqueueError($"kd_wait_for_event: WaitForEvent returned {waitHr}");
            return $"WaitForEvent failed with {waitHr}. Target state is unknown — " +
                   "call get_system_state, then kd_break or kd_continue as appropriate.";
        }, timeout + TimeSpan.FromSeconds(5)); // Outer timeout slightly larger
    }

    // ═══════════════════════════════════════════════════════════════
    //  BSOD DETECTION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Check if the current break is due to a BSOD/bugcheck.
    /// Must be called while target is broken in.
    /// </summary>
    public async Task<(bool IsBugcheck, string? BugcheckCode, string? LastEvent)> DetectBugcheckAsync()
    {
        var timeout = TimeSpan.FromSeconds(5);

        return await _thread.ExecuteAsync(() =>
        {
            if (_client == null)
                return (false, (string?)null, (string?)null);

            // .lastevent is the human-readable reason for this break (breakpoint,
            // access violation, break instruction...). It goes into get_system_state
            // as the Break Reason, so the model never sees "unknown" at a halt.
            _outputCapture.Clear();
            _client.Control.TryExecute(
                DEBUG_OUTCTL.THIS_CLIENT, ".lastevent", DEBUG_EXECUTE.DEFAULT);
            var lastEventOutput = _outputCapture.GetAndClear();
            var lastEvent = FirstLastEventLine(lastEventOutput);

            // .bugcheck reads KiBugCheckData, which KeBugCheckEx fills in — so a
            // breakpoint sitting *at* nt!KeBugCheckEx is correctly not a bugcheck yet.
            // .lastevent for a live BSOD just says "Break instruction exception",
            // so it is only a fallback if .bugcheck output cannot be parsed.
            _outputCapture.Clear();
            _client.Control.TryExecute(
                DEBUG_OUTCTL.THIS_CLIENT, ".bugcheck", DEBUG_EXECUTE.DEFAULT);
            var bugcheckOutput = _outputCapture.GetAndClear();

            var parsed = ParseBugcheckOutput(bugcheckOutput);
            if (parsed != null)
                return (parsed.Value.IsBugcheck, parsed.Value.BugcheckCode, lastEvent);

            _logger.LogDebug(".bugcheck output not recognised, falling back to .lastevent: {Output}",
                bugcheckOutput.Trim());

            var fallback = ParseLastEventFallback(lastEventOutput);
            return (fallback.IsBugcheck, fallback.BugcheckCode, lastEvent);
        }, timeout);
    }

    /// <summary>"Last event: Access violation - code c0000005 (!!! second chance !!!)" -> the part after the colon.</summary>
    public static string? FirstLastEventLine(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line == null) return null;
        const string prefix = "Last event:";
        return line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? line[prefix.Length..].Trim() : line;
    }

    /// <summary>
    /// Parses ".bugcheck" output ("Bugcheck code 000000D1"). Returns null if the
    /// output does not carry a bugcheck code line at all.
    /// </summary>
    public static (bool IsBugcheck, string? BugcheckCode)? ParseBugcheckOutput(string output)
    {
        var match = Regex.Match(output, @"Bugcheck code\s+(?:0x)?([0-9A-Fa-f]{1,16})\b",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return null;

        var code = Convert.ToUInt64(match.Groups[1].Value, 16);
        return code != 0 ? (true, $"0x{code:X8}") : (false, null);
    }

    public static (bool IsBugcheck, string? BugcheckCode) ParseLastEventFallback(string output)
    {
        var match = Regex.Match(output, @"Bug\s*check\s+(?:0x)?([0-9A-Fa-f]{1,16})\b",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return (false, null);

        var code = Convert.ToUInt64(match.Groups[1].Value, 16);
        return code != 0 ? (true, $"0x{code:X8}") : (false, null);
    }

    // ═══════════════════════════════════════════════════════════════
    //  EVENT PUMP (called by DbgEngThread when idle + target running)
    // ═══════════════════════════════════════════════════════════════

    private string FormatQueuedEvents()
    {
        var events = _eventCallbacks.DrainEvents();
        return events.Count > 0
            ? "\nQueued events:\n" + string.Join("\n", events.Select(e => $"  {e}"))
            : "";
    }

    private void PumpEvents()
    {
        if (_client == null)
        {
            // Nothing to pump; without this the thread loop would spin on this call.
            _thread.PumpEnabled = false;
            return;
        }

        // Live kernel targets only support INFINITE waits. A periodic ACTIVE
        // break-in yields the thread every few seconds so queued tool calls run
        // (no-op while the engine has no debuggee: an interrupt during the KDNET
        // reconnect handshake makes the kernel retry it). If the target never
        // comes back this wait cannot be woken; see ReplaceEngineThread.
        using var interruptTimer = new Timer(_ => RequestInterrupt(), null, PumpYieldMs, PumpYieldMs);

        var hr = _client.Control.TryWaitForEvent(DEBUG_WAIT.DEFAULT, unchecked((int)0xFFFFFFFF));
        _logger.LogInformation("Pump: WaitForEvent returned {Hr}, status={Status}", hr, _eventCallbacks.LastExecutionStatus);

        if (hr == WaitExited)
            return; // a tool asked the wait to exit so its work item can run

        if (hr == HRESULT.S_OK)
        {
            ClassifyPendingBreakIn();
            var status = _client.Control.ExecutionStatus;
            if (status == DEBUG_STATUS.BREAK)
            {
                if (_eventCallbacks.HasBreakingEvent)
                {
                    // Real event (breakpoint, exception, system error) — stop pumping
                    _thread.PumpEnabled = false;
                }
                else
                {
                    // Our yield interrupt — resume target and keep pumping.
                    // The next WaitForEvent call will dispatch the GO.
                    _client.Control.TrySetExecutionStatus(DEBUG_STATUS.GO);
                }
            }
            // If status is still GO, it was just our interrupt to yield — keep pumping
        }
        else
        {
            _logger.LogWarning("Event pump WaitForEvent failed with {Hr}; pump stopped", hr);
            _eventCallbacks.EnqueueError($"Event pump stopped: WaitForEvent returned {hr}. " +
                                         "Target state is unknown; call get_system_state.");
            _thread.PumpEnabled = false;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  BREAK-IN CLASSIFICATION
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// INFINITE WaitForEvent (a finite timeout is E_NOTIMPL on live kernel targets —
    /// verified) driven by a periodic timer: from <paramref name="firstMs"/> on it
    /// sends ACTIVE break-ins every <see cref="InterruptRetryMs"/> (a request sent
    /// while the kernel is busy is dropped, so one shot is not enough); once
    /// <paramref name="deadlineMs"/> has passed it wakes the thread with
    /// <see cref="WakeEngineThread"/> (a break-in on a live target, EXIT on a
    /// target-less wait). With <paramref name="breakInEarly"/> false, nothing is
    /// sent before the deadline.
    /// Returns S_OK on an event, S_FALSE / E_PENDING when the wait was abandoned.
    /// </summary>
    private HRESULT WaitForEventInterruptible(int firstMs, int deadlineMs, bool breakInEarly)
    {
        var started = Environment.TickCount64;
        var exitSent = false;
        using var timer = new Timer(_ =>
        {
            if (Environment.TickCount64 - started >= deadlineMs)
            {
                // A break-in is retried every tick (a busy kernel drops it); EXIT is
                // sent once: it frees a target-less wait at once or not at all, and a
                // later EXIT could land on a target that has meanwhile re-attached.
                if (!EngineHasNoDebuggee)
                    RequestInterrupt();
                else if (!exitSent)
                {
                    exitSent = true;
                    WakeEngineThread();
                }
            }
            else if (breakInEarly)
                RequestInterrupt();
        }, null, firstMs, InterruptRetryMs);

        return _client!.Control.TryWaitForEvent(DEBUG_WAIT.DEFAULT, unchecked((int)0xFFFFFFFF));
    }

    private static bool WaitAbandoned(HRESULT hr) => hr == HRESULT.S_FALSE || hr == WaitExited;

    /// <summary>
    /// The engine thread is parked in a target-less INFINITE wait and did not answer
    /// a probe. Nothing wakes that wait while there is no target — SetInterrupt(EXIT),
    /// EndSession(REENTRANT) and a replacement thread/client were all tried live;
    /// dbgeng is process-global and the stuck session owns it. Two cases look the
    /// same from here: the target is still rebooting (it will come back and the
    /// thread frees itself), or it restarted gracefully and will never re-attach
    /// (only an MCP server restart helps). The message says how to tell them apart;
    /// the flag is a suspicion that a later successful probe clears.
    /// </summary>
    public const string EngineWedgedMessage =
        "The kernel debugger engine is parked waiting for a target that has not (re-)attached, so the " +
        "session could not be detached and is still tracked. If the target is rebooting (after a BSOD), " +
        "wait: get_system_state shows TARGET REBOOTED at the initial breakpoint, then kd_continue " +
        "(kd_disconnect works again once the target is back). If get_system_state shows the OS is already " +
        "up (VMware Tools running), the guest restarted gracefully while the debugger was connected and " +
        "the session cannot be recovered in-process: kernel-debug tools need an MCP server restart " +
        "(guest/VM tools still work). To avoid this, call kd_disconnect before restarting the guest.";

    public bool EngineWedged => _engineWedged;

    /// <summary>What is still running on the engine thread past a tool timeout, or null.</summary>
    public string? BusyDescription => _thread.BusyDescription;
    private volatile bool _engineWedged;

    private void MarkEngineWedged()
    {
        _logger.LogError("Engine thread did not answer a probe while the engine has no debuggee; kernel tools unavailable until it does");
        _engineWedged = true;
        // The client stays: the thread is still inside its WaitForEvent and will use
        // it if the target ever comes back. The pump must not restart on its own.
        _thread.PumpEnabled = false;
        _eventCallbacks.EnqueueError(EngineWedgedMessage);
    }

    /// <summary>
    /// Makes the pump's WaitForEvent(INFINITE) return so a queued work item can run.
    /// With a live target that is a break-in (ACTIVE): the pump classifies it as its
    /// own yield and resumes the target. DEBUG_INTERRUPT_EXIT is only ever sent to a
    /// target-less wait: on a running target it was observed twice (live runs 14 and
    /// 16) to leave the engine wait unresponsive to every later break-in, with the
    /// guest halted in the debugger and nothing able to free the engine thread.
    /// </summary>
    private void WakeEngineThread()
    {
        if (!EngineHasNoDebuggee)
        {
            RequestInterrupt();
            return;
        }

        try
        {
            var hr = _client?.Control.TrySetInterrupt(DEBUG_INTERRUPT.EXIT);
            _logger.LogInformation("SetInterrupt(EXIT) -> {Hr} (no debuggee)", hr);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SetInterrupt(EXIT) threw");
        }
    }

    private void RequestInterrupt()
    {
        // While the engine has no debuggee (target rebooting / reconnecting) any
        // interrupt disturbs the KDNET handshake; the initial breakpoint arrives on
        // its own once the kernel is back. Runs 2 and 4 reconnected cleanly this way.
        if (_eventCallbacks.LastExecutionStatus == DEBUG_STATUS.NO_DEBUGGEE)
            return;

        _interruptRequested = true;
        try
        {
            var hr = _client?.Control.TrySetInterrupt(DEBUG_INTERRUPT.ACTIVE);
            _logger.LogInformation("SetInterrupt -> {Hr} (rebootDetected={Reboot})", hr, _eventCallbacks.RebootDetected);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SetInterrupt threw");
        }
    }

    /// <summary>
    /// Reads nt!RtlpBreakWithStatusInstruction from the KdDebuggerDataBlock
    /// (no symbols needed). Every DbgBreakPointWithStatus break-in — Ctrl+Break,
    /// KeBugCheck, the initial breakpoint — traps at exactly this address with
    /// the DBG_STATUS_* reason in RCX (EAX on x86). This is how WinDbg tells them apart.
    /// </summary>
    private void ReadBreakWithStatusAddress()
    {
        if (_client == null) return;
        var previous = _breakWithStatusAddr;
        var buffer = Marshal.AllocHGlobal(8);
        try
        {
            var hr = _client.DataSpaces.TryReadDebuggerData(
                DEBUG_DATA.BreakpointWithStatusAddr, buffer, 8, out var size);
            _breakWithStatusAddr = hr == HRESULT.S_OK && size == 8
                ? (ulong)Marshal.ReadInt64(buffer)
                : 0;
        }
        catch
        {
            _breakWithStatusAddr = 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        if (_breakWithStatusAddr != previous)
            _logger.LogInformation("BreakpointWithStatus address: 0x{Addr:X16} (was 0x{Prev:X16})",
                _breakWithStatusAddr, previous);
    }

    /// <summary>
    /// Must run on the DbgEng thread right after a WaitForEvent returned S_OK.
    /// Decides whether a first-chance int 3 was our own interrupt (ignored), a
    /// bugcheck, or a break instruction in target code, and flags the latter two
    /// as breaking events so the pump stops and the LLM sees them.
    /// </summary>
    private void ClassifyPendingBreakIn()
    {
        if (_client == null || !_eventCallbacks.TryTakePendingBreakIn(out var address))
            return;

        // Consume the flag only together with a break-in, otherwise an interrupt
        // that coincided with a breakpoint event would be lost and its break-in
        // misread after the next kd_continue.
        var interruptRequested = _interruptRequested;
        _interruptRequested = false;

        // KASLR relocates ntoskrnl on every boot, so a cached address is stale after
        // a reboot. Re-read on any mismatch before concluding "target code".
        if (_breakWithStatusAddr == 0 || address != _breakWithStatusAddr)
            ReadBreakWithStatusAddress();

        // DbgBreakPointWithStatus(Status) is "int 3; ret" on x64 — the status is
        // the untouched first argument, RCX (EAX on x86). RAX is logged only so a
        // live run can confirm which register carries it.
        var rcx = ReadRegister("rcx");
        var rax = ReadRegister("rax");
        var argRegister = rcx ?? ReadRegister("eax");
        var atBreakWithStatus = _breakWithStatusAddr != 0 && address == _breakWithStatusAddr;

        long status;
        string source;
        if (atBreakWithStatus)
        {
            status = argRegister.HasValue ? (long)(uint)argRegister.Value : -1L;
            source = "DbgBreakPointWithStatus";
        }
        else if (_breakWithStatusAddr == 0)
        {
            // No data block: still trust a bugcheck-range status (a false halt costs
            // one kd_continue; a false resume loses the BSOD), otherwise fall back
            // to whether we asked for the interrupt.
            long candidate = argRegister.HasValue ? (long)(uint)argRegister.Value : -1L;
            status = candidate is DbgStatusBugcheckFirst or DbgStatusBugcheckSecond or DbgStatusFatal
                ? candidate
                : interruptRequested ? DbgStatusControlC : -1;
            source = "unknown (no debugger data block)";
        }
        else
        {
            status = -1;
            source = "int 3 in target code";
        }

        var decision = status switch
        {
            DbgStatusControlC => "debugger break-in, ignored",
            DbgStatusBugcheckFirst or DbgStatusBugcheckSecond or DbgStatusFatal => "bugcheck",
            _ => "target break-in"
        };
        _logger.LogInformation(
            "Break-in at 0x{Addr:X16} ({Source}): arg=0x{Arg:X} rcx=0x{Rcx:X} rax=0x{Rax:X} status={Status} interruptRequested={Req} -> {Decision}",
            address, source, argRegister ?? -1, rcx ?? -1, rax ?? -1, status, interruptRequested, decision);

        switch (status)
        {
            case DbgStatusControlC:
                return;

            case DbgStatusBugcheckFirst:
            case DbgStatusBugcheckSecond:
            case DbgStatusFatal:
                _eventCallbacks.RecordBreakingEvent(new DebugEvent
                {
                    Type = DebugEventKind.Bugcheck,
                    Details = status == DbgStatusBugcheckSecond
                        ? "BSOD: second bugcheck break-in (crash dump written). kd_continue should reboot the VM; " +
                          "if it keeps breaking here, use vm_stop(hard=true) + vm_start"
                        : "BSOD: kernel entered the bugcheck handler. Run kd_execute('!analyze -v') now; " +
                          "kd_continue proceeds to the crash dump and reboot (then kd_wait_for_event for TARGET REBOOTED)",
                    Address = address
                });
                return;

            default:
                _eventCallbacks.RecordBreakingEvent(new DebugEvent
                {
                    Type = DebugEventKind.BreakIn,
                    Details = status >= 0
                        ? $"DbgBreakPointWithStatus({status}) at 0x{address:X16}"
                        : atBreakWithStatus
                            ? $"DbgBreakPointWithStatus at 0x{address:X16}, status register unreadable — treated as a target break-in"
                            : $"Break instruction in target code at 0x{address:X16} (DbgBreakPoint/__debugbreak)",
                    Address = address
                });
                return;
        }
    }

    private long? ReadRegister(string name)
    {
        if (_client == null) return null;
        try
        {
            var index = _client.Registers.GetIndexByName(name);
            var value = _client.Registers.GetValue(index);
            return value.I64;
        }
        catch
        {
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════

    private static string? FindDebuggerDirectory()
    {
        // Try common locations for Debugging Tools for Windows
        var candidates = new[]
        {
            @"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64",
            @"C:\Program Files\Windows Kits\10\Debuggers\x64",
            @"C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64",
            @"C:\Debuggers",
        };

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "dbgeng.dll")))
                return dir;
        }

        // Also check if WinDbg Preview is installed
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windbgPreview = Path.Combine(localAppData, "Microsoft", "WindowsApps");
        if (File.Exists(Path.Combine(windbgPreview, "dbgeng.dll")))
            return windbgPreview;

        return null; // Will try system PATH
    }

    /// <summary>
    /// Reset connection state without disposing the manager.
    /// Used by snapshot restore — the client was already cleanly disconnected
    /// before the restore, so we just null the reference and stop pumping.
    /// The manager stays fully usable for the next kd_connect.
    /// </summary>
    public void ResetConnectionState()
    {
        _thread.PumpEnabled = false;
        _client = null;
        _eventCallbacks.ClearEvents();
        _eventCallbacks.ClearRebootFlag();
        // _disposed intentionally NOT set — manager remains usable
    }

    /// <summary>
    /// Server shutdown. Uses the full kd_disconnect sequence: a bare
    /// EndSession(ACTIVE_DETACH) on a target halted at a breakpoint reports
    /// GO_HANDLED but does not actually resume the kernel (seen live twice: the
    /// guest stayed frozen after the MCP client restarted the server), whereas
    /// GO dispatched by the pump followed by a yield + detach is verified to
    /// leave the guest running.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_client == null)
            return;

        try
        {
            var result = DisconnectAsync().GetAwaiter().GetResult();
            _logger.LogInformation("Shutdown detach: {Message}", result.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not detach cleanly on shutdown; the target may be left halted");
        }
    }
}
