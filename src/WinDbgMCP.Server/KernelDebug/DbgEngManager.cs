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

    private const long DbgStatusControlC = 1;
    private const long DbgStatusBugcheckFirst = 3;
    private const long DbgStatusBugcheckSecond = 4;
    private const long DbgStatusFatal = 5;

    public bool IsConnected => _client != null;
    public int PendingEventCount => _eventCallbacks.PendingCount;
    public bool RebootDetected => _eventCallbacks.RebootDetected;

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
        var timeout = TimeSpan.FromSeconds(_config.Timeouts.KdConnectSeconds);

        return await _thread.ExecuteAsync(() =>
        {
            // Reaching here with a live client means the coordinator already
            // considers the session lost (NoDebuggee); tear the old one down
            // rather than leaking it or wedging kd_connect/kd_disconnect.
            if (_client != null)
            {
                _logger.LogWarning("Stale DbgEng client found on connect; ending old session");
                _thread.PumpEnabled = false;
                try { _client.TryEndSession(DEBUG_END.ACTIVE_TERMINATE); } catch { }
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

            // WaitForEvent for live kernel targets MUST use INFINITE timeout
            // (per Microsoft docs). Use SetInterrupt from a timer as safety net.
            var waitTimeoutMs = _config.Timeouts.KdInitialBreakSeconds * 1000;
            using var interruptTimer = new Timer(_ => RequestInterrupt(), null, waitTimeoutMs, Timeout.Infinite);

            var waitHr = _client.Control.TryWaitForEvent(DEBUG_WAIT.DEFAULT, unchecked((int)0xFFFFFFFF));

            if (waitHr == HRESULT.S_OK)
            {
                _logger.LogInformation("Connected. Target at initial breakpoint.");
                ReadBreakWithStatusAddress();
                ClassifyPendingBreakIn();

                // Force reload symbols
                _outputCapture.Clear();
                _client.Control.TryExecute(DEBUG_OUTCTL.THIS_CLIENT, ".reload /f", DEBUG_EXECUTE.DEFAULT);
                _outputCapture.GetAndClear(); // Discard reload output

                return $"Connected to kernel via {transport}. Target is at initial breakpoint. " +
                       "You can now use kd_execute to run WinDbg commands, or kd_continue to resume.";
            }
            else if (waitHr == HRESULT.S_FALSE)
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
    /// </summary>
    public async Task<string> DisconnectAsync()
    {
        if (_client == null)
            return "Not connected.";

        // Step 1: If target is at BREAK, resume it by setting GO and letting
        // the event pump dispatch it. For kernel targets, WaitForEvent MUST use
        // INFINITE timeout — a finite timeout returns immediately without
        // dispatching. The pump already handles this correctly with its
        // INFINITE + interrupt timer pattern.
        var needsResume = await _thread.ExecuteAsync(() =>
        {
            if (_client == null) return false;

            try
            {
                var status = _client.Control.ExecutionStatus;
                if (status == DEBUG_STATUS.BREAK)
                {
                    _client.Control.TrySetExecutionStatus(DEBUG_STATUS.GO);
                    _thread.PumpEnabled = true;
                    return true;
                }
            }
            catch { }
            return false;
        }, TimeSpan.FromSeconds(5));

        if (needsResume)
        {
            // Give the pump time to dispatch the GO via WaitForEvent(INFINITE).
            // The pump uses a 1s interrupt timer cycle, so 3s is plenty.
            await Task.Delay(3000);
        }

        // Step 2: Disconnect. The pump will yield to this work item
        // within ~1s when its interrupt timer fires.
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
            return "Disconnected from kernel debugger. Target has been resumed.";
        }, TimeSpan.FromSeconds(10));
    }

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
        }, timeout);
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
            _client.Control.SetInterrupt(DEBUG_INTERRUPT.ACTIVE);

            // Wait for the break to take effect (INFINITE + interrupt timer for kernel targets)
            var breakTimeoutMs = _config.Timeouts.KdBreakSeconds * 1000;
            using var interruptTimer = new Timer(_ => RequestInterrupt(), null, breakTimeoutMs, Timeout.Infinite);

            var waitHr = _client.Control.TryWaitForEvent(
                DEBUG_WAIT.DEFAULT,
                unchecked((int)0xFFFFFFFF));

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
                return "SetInterrupt sent but target did not break within timeout. " +
                       "The target may be in a non-interruptible state. Try again or check get_system_state.";
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
            _client.Control.TrySetExecutionStatus(DEBUG_STATUS.GO);
            _thread.PumpEnabled = true;

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

            // Wait for step to complete (INFINITE + interrupt timer for kernel targets)
            var stepTimeoutMs = _config.Timeouts.KdStepSeconds * 1000;
            using var interruptTimer = new Timer(_ => RequestInterrupt(), null, stepTimeoutMs, Timeout.Infinite);

            var waitHr = _client.Control.TryWaitForEvent(
                DEBUG_WAIT.DEFAULT,
                unchecked((int)0xFFFFFFFF));

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
                return $"Step {mode} timed out. The instruction may have caused a long-running " +
                       "operation. Call kd_break to interrupt, or kd_wait_for_event to continue waiting.";
            }
        }, timeout + TimeSpan.FromSeconds(2));
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

            // INFINITE timeout + interrupt timer for kernel targets
            var waitTimeoutMs = timeoutSeconds * 1000;
            using var interruptTimer = new Timer(_ => RequestInterrupt(), null, waitTimeoutMs, Timeout.Infinite);

            var waitHr = _client.Control.TryWaitForEvent(
                DEBUG_WAIT.DEFAULT,
                unchecked((int)0xFFFFFFFF));

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
                // Our own timeout interrupt halted the target — resume it so the
                // "no event" answer is true. The pump dispatches the GO.
                _client.Control.TrySetExecutionStatus(DEBUG_STATUS.GO);
                _thread.PumpEnabled = true;
                return $"No debug event received within {timeoutSeconds}s. Target is still running. " +
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
    public async Task<(bool IsBugcheck, string? BugcheckCode)> DetectBugcheckAsync()
    {
        var timeout = TimeSpan.FromSeconds(5);

        return await _thread.ExecuteAsync(() =>
        {
            if (_client == null)
                return (false, (string?)null);

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
                return parsed.Value;

            _logger.LogDebug(".bugcheck output not recognised, falling back to .lastevent: {Output}",
                bugcheckOutput.Trim());

            _outputCapture.Clear();
            _client.Control.TryExecute(
                DEBUG_OUTCTL.THIS_CLIENT, ".lastevent", DEBUG_EXECUTE.DEFAULT);
            return ParseLastEventFallback(_outputCapture.GetAndClear());
        }, timeout);
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
        if (_client == null) return;

        // For kernel targets, WaitForEvent must use INFINITE.
        // Use an interrupt timer to periodically yield so the DbgEng thread
        // can process work items (tool calls). 5s balances responsiveness
        // with minimizing target micro-freezes.
        using var interruptTimer = new Timer(_ => RequestInterrupt(), null, 5000, Timeout.Infinite);

        var hr = _client.Control.TryWaitForEvent(DEBUG_WAIT.DEFAULT, unchecked((int)0xFFFFFFFF));

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

    private void RequestInterrupt()
    {
        _interruptRequested = true;
        try { _client?.Control.SetInterrupt(DEBUG_INTERRUPT.ACTIVE); }
        catch { }
    }

    /// <summary>
    /// Reads nt!RtlpBreakWithStatusInstruction from the KdDebuggerDataBlock
    /// (no symbols needed). Every DbgBreakPointWithStatus break-in — Ctrl+Break,
    /// KeBugCheck, the initial breakpoint — traps at exactly this address with
    /// the DBG_STATUS_* reason in RAX. This is how WinDbg tells them apart.
    /// </summary>
    private void ReadBreakWithStatusAddress()
    {
        if (_client == null) return;
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
        _logger.LogInformation("BreakpointWithStatus address: 0x{Addr:X16}", _breakWithStatusAddr);
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
            "Break-in at 0x{Addr:X16} ({Source}): rcx=0x{Rcx:X} rax=0x{Rax:X} status={Status} interruptRequested={Req} -> {Decision}",
            address, source, rcx ?? -1, rax ?? -1, status, interruptRequested, decision);

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
                          "kd_continue proceeds to the crash dump and reboot",
                    Address = address
                });
                return;

            default:
                _eventCallbacks.RecordBreakingEvent(new DebugEvent
                {
                    Type = DebugEventKind.BreakIn,
                    Details = status >= 0
                        ? $"DbgBreakPointWithStatus({status}) at 0x{address:X16}"
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _thread.PumpEnabled = false;

        try
        {
            if (_client != null)
            {
                _client.TryEndSession(DEBUG_END.ACTIVE_DETACH);
                _client = null;
            }
        }
        catch { }
    }
}
