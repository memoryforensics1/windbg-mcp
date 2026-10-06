using System.ComponentModel;
using ModelContextProtocol.Server;
using WinDbgMCP.Server.State;
using WinDbgMCP.Server.UserModeDebug;

namespace WinDbgMCP.Server.Tools;

[McpServerToolType]
public static class UserModeDebugTools
{
    // ═══════════════════════════════════════════════════════════════
    //  FRIDA TOOLS
    // ═══════════════════════════════════════════════════════════════

    [McpServerTool(Name = "umd_frida_attach"), Description(
        "Attach Frida to a process in the guest VM for dynamic instrumentation. " +
        "Requires: frida-tools installed on host (pip install frida-tools), " +
        "frida-server.exe running in the guest VM. " +
        "After attaching, use umd_frida to inject scripts, hook functions, etc.")]
    public static Task<string> UmdFridaAttach(
        StateCoordinator state,
        FridaManager frida,
        [Description("Process name to attach to (e.g., 'notepad.exe'). Mutually exclusive with pid.")] string? processName = null,
        [Description("Process ID to attach to. Mutually exclusive with processName.")] int? pid = null,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("umd_frida_attach", async () =>
        {
            if (processName == null && pid == null)
                return "Provide either processName or pid to attach to.";

            try
            {
                if (pid.HasValue)
                    return await frida.AttachAsync(pid.Value, ct);
                else
                    return await frida.AttachByNameAsync(processName!, ct);
            }
            catch (OperationCanceledException)
            {
                return "umd_frida_attach timed out. Is frida-server running in the guest?";
            }
            catch (Exception ex)
            {
                return $"umd_frida_attach failed: {ex.GetType().Name}: {ex.Message}";
            }
        });
    }

    [McpServerTool(Name = "umd_frida"), Description(
        "Perform Frida operations on an attached process. " +
        "Actions: 'inject' (run JS script), 'eval' (one-liner JS), " +
        "'list' (list guest processes via frida-ps), 'detach', " +
        "'inject_bg' (start persistent background hook session), " +
        "'collect_bg' (read output from background session), " +
        "'stop_bg' (stop background session).")]
    public static Task<string> UmdFrida(
        StateCoordinator state,
        FridaManager frida,
        [Description("Action: 'inject', 'eval', 'list', 'detach', 'inject_bg', 'collect_bg', 'stop_bg'")] string action,
        [Description("For 'inject'/'inject_bg': JavaScript code to inject. For 'eval': JS expression.")] string? code = null,
        [Description("Timeout in seconds (default 30). For 'inject' only.")] int timeoutSeconds = 30,
        [Description("For 'inject': if true, hooks persist in the target process after this call returns. " +
                     "Use this when installing Interceptor.attach/replace hooks that should survive across " +
                     "multiple umd_frida calls. Default false.")] bool eternalize = false,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("umd_frida", async () =>
        {
            try
            {
                return action.ToLowerInvariant() switch
                {
                    "inject" => code == null
                        ? "Provide 'code' parameter with JavaScript to inject."
                        : await frida.InjectScriptAsync(code, timeoutSeconds, eternalize, ct),

                    "eval" => code == null
                        ? "Provide 'code' parameter with JavaScript expression to evaluate."
                        : await frida.EvalAsync(code, timeoutSeconds, ct),

                    "list" => await frida.ListProcessesAsync(ct),

                    "detach" => frida.Detach(),

                    "inject_bg" => code == null
                        ? "Provide 'code' parameter with JavaScript to inject in background."
                        : await frida.InjectBackgroundAsync(code, ct),

                    "collect_bg" => frida.CollectBackgroundOutput(),

                    "stop_bg" => frida.StopBackgroundSession(),

                    _ => $"Unknown action '{action}'. Use: inject, eval, list, detach, inject_bg, collect_bg, stop_bg."
                };
            }
            catch (OperationCanceledException)
            {
                return "umd_frida timed out.";
            }
            catch (Exception ex)
            {
                return $"umd_frida failed: {ex.GetType().Name}: {ex.Message}";
            }
        });
    }

    [McpServerTool(Name = "umd_frida_skill"), Description(
        "Get Frida best practices, API reference, and usage patterns for the MCP Frida tools. " +
        "Call this BEFORE using Frida if you are unfamiliar with the workflow.")]
    public static Task<string> UmdFridaSkill(StateCoordinator state)
    {
        return state.RunToolAsync("umd_frida_skill", () => Task.FromResult("""
            ╔══════════════════════════════════════════════════════════════╗
            ║              FRIDA MCP TOOLS — QUICK REFERENCE             ║
            ╚══════════════════════════════════════════════════════════════╝

            ── PREREQUISITES ─────────────────────────────────────────────
            Guest VM:
              frida-server is at: C:\Tools\frida-server.exe
              It should already be running. Verify:
                guest_run_command("tasklist /FI \"IMAGENAME eq frida-server.exe\"")
              If not running, start it:
                guest_run_command("start /b C:\Tools\frida-server.exe -l 0.0.0.0:27042")

            Host: frida-tools must be installed (pip install frida-tools).
            The MCP server auto-resolves the frida CLI path.

            IP Discovery: get_system_state shows the guest VM IP address.
            You do NOT need to provide the IP — the MCP server resolves it
            automatically via VMware Tools.

            ── CRITICAL: SESSION MODEL ───────────────────────────────────
            ⚠ Each eval/inject spawns a FRESH frida process. When the
            frida process exits, ALL hooks are REMOVED and original code
            bytes are restored. This means:

            • Hooks installed via inject ONLY LAST for timeoutSeconds
            • Hooks installed via eval ONLY LAST for ~1 second
            • Checking hook bytes from a separate eval call will show
              ORIGINAL bytes (the hook is already gone)

            To make hooks PERSIST, use one of these approaches:

            Option 1 — eternalize (hooks survive after call returns):
              umd_frida(action="inject", code="<hook script>", eternalize=true)
              The Frida agent stays loaded in the target. Hooks persist
              until the target process exits or is restarted.
              NOTE: You cannot un-eternalize — to remove hooks, restart
              the target process.

            Option 2 — background session (hooks + live output):
              umd_frida(action="inject_bg", code="<hook script>")
              Frida runs in the background indefinitely. Hooks stay active.
              umd_frida(action="collect_bg") — read captured output
              umd_frida(action="stop_bg")    — stop session, remove hooks
              This is best for monitoring workflows where you want to
              collect hook output over time.

            Option 3 — long timeout (simple monitoring):
              umd_frida(action="inject", code="<script>", timeoutSeconds=120)
              Hooks are active for 120 seconds. Output is returned when
              the timeout expires. Blocks the tool call for that duration.

            ── WORKFLOW ──────────────────────────────────────────────────
            1. umd_frida_attach(processName="target.exe")   — attach by name
               umd_frida_attach(pid=1234)                   — or by PID
            2. umd_frida(action="eval", code="<expr>")      — quick one-liner
               umd_frida(action="inject", code="<script>")  — multi-line script
            3. umd_frida(action="detach")                    — clean up when done

            Tip: umd_frida(action="list") works WITHOUT attaching first.
            It runs frida-ps to list all guest processes with PIDs.

            ── ACTIONS REFERENCE ─────────────────────────────────────────
            eval        — One-shot JS expression. Returns result immediately.
                          Best for: reading data, enumerating modules/exports.
            inject      — Multi-line JS script. Runs for timeoutSeconds.
                          Set eternalize=true to persist hooks.
            inject_bg   — Start persistent background hook session.
                          Hooks stay active until stop_bg is called.
            collect_bg  — Read output from background session.
            stop_bg     — Stop background session, remove hooks.
            list        — List guest processes (no attach needed).
            detach      — Detach from process (also stops background).

            ── USEFUL EVAL EXPRESSIONS ───────────────────────────────────
            Process info:
              Process.id                              → PID
              Process.arch                            → "x64" / "ia32"
              Process.platform                        → "windows"

            Module enumeration:
              Process.enumerateModules().map(m=>m.name)
              Process.getModuleByName('ntdll.dll').base

            Export lookup:
              Process.getModuleByName('kernel32.dll').getExportByName('CreateFileW')

            Memory:
              ptr('0x7ff612340000').readUtf16String()
              ptr('0x7ff612340000').readByteArray(16)

            ── INJECT SCRIPT PATTERNS ────────────────────────────────────
            Hook a function (persistent — use with eternalize=true or inject_bg):
              var mod = Process.getModuleByName('kernel32.dll');
              Interceptor.attach(mod.getExportByName('CreateFileW'), {
                onEnter(args) {
                  console.log('CreateFileW: ' + args[0].readUtf16String());
                }
              });

            Hook with return value:
              var mod = Process.getModuleByName('kernel32.dll');
              Interceptor.attach(mod.getExportByName('ReadFile'), {
                onEnter(args) { this.buf = args[1]; this.size = args[2].toInt32(); },
                onLeave(retval) {
                  if (retval.toInt32()) console.log('Read ' + this.size + ' bytes');
                }
              });

            Enumerate threads:
              Process.enumerateThreads().forEach(t => {
                console.log('TID=' + t.id + ' state=' + t.state);
              });

            ── COMMON PITFALLS ───────────────────────────────────────────
            • ⚠ HOOKS ARE EPHEMERAL by default — see SESSION MODEL above.
              Use eternalize=true or inject_bg for persistent hooks.
            • ⚠ CODE VIEW: readU8() on hooked addresses returns ORIGINAL
              bytes, NOT the JMP trampoline. Frida's GumInterceptor hides
              its own hooks from script memory reads. This is by design.
              Do NOT use readU8() to verify hook installation — verify by
              checking if the onEnter/onLeave callback fires instead.
              The kernel debugger (kd_execute "db <addr>") will show the
              actual patched bytes if you need to confirm.
            • Process names are case-sensitive — use action="list" first.
            • For inject scripts, always include console.log() output —
              the result is captured from stdout. Silent scripts return empty.
            • Use single quotes inside JS code to avoid escaping issues.
            • Frida 17.x API: Use Process.getModuleByName('x').getExportByName('y')
              instead of Module.findExportByName('x', 'y').

            ── TROUBLESHOOTING ───────────────────────────────────────────
            "Cannot determine guest VM IP address":
              → VM may not have VMware Tools running. Check get_system_state.

            "Failed to start frida" / "frida not found":
              → Install on host: pip install frida-tools

            "Failed to connect to remote frida-server":
              → Verify frida-server is running in guest (see PREREQUISITES)
              → Check guest firewall: guest_run_command("netsh advfirewall
                firewall add rule name=frida dir=in action=allow
                protocol=TCP localport=27042")

            Hooks seem installed but bytes look unchanged (readU8):
              → This is EXPECTED. Frida's code view hides trampolines
                from readU8(). The hook IS installed. Verify by checking
                if your onEnter/onLeave callback fires, or use the kernel
                debugger (kd_execute "db <addr>") to see actual bytes.
              → If callbacks genuinely don't fire, check that you're using
                eternalize=true or inject_bg so hooks persist.

            Attach fails with "unable to find process":
              → Use umd_frida(action="list") to see exact process names
              → Process names are case-sensitive
            """));
    }

    // ═══════════════════════════════════════════════════════════════
    //  DBGSRV TOOLS
    // ═══════════════════════════════════════════════════════════════

    [McpServerTool(Name = "umd_dbgsrv_skill"), Description(
        "Get dbgsrv best practices, WinDbg command reference, and usage patterns " +
        "for the MCP dbgsrv tools. Call this BEFORE using dbgsrv if you are unfamiliar with the workflow.")]
    public static Task<string> UmdDbgsrvSkill(StateCoordinator state)
    {
        return state.RunToolAsync("umd_dbgsrv_skill", () => Task.FromResult("""
            ╔══════════════════════════════════════════════════════════════╗
            ║            DBGSRV MCP TOOLS — QUICK REFERENCE              ║
            ╚══════════════════════════════════════════════════════════════╝

            ── PREREQUISITES ─────────────────────────────────────────────
            Guest VM — dbgsrv and its dependencies are installed at:
              C:\Tools\DbgSrv\dbgsrv.exe    (the process server)
              C:\Tools\DbgSrv\dbgeng.dll    (REQUIRED — must be same dir)
              C:\Tools\DbgSrv\dbghelp.dll   (REQUIRED — must be same dir)

            IMPORTANT: dbgsrv.exe MUST have dbgeng.dll and dbghelp.dll in
            the SAME directory. Without them, it will start but connections
            will fail with error 0x8004010C.

            IP Discovery: Call get_system_state — the "VM IP Address" field
            shows the guest IP. You need this for umd_dbgsrv_connect.

            ── WORKFLOW (STEP BY STEP) ───────────────────────────────────
            Step 1 — Verify dbgsrv exists in guest:
              guest_run_command("dir C:\\Tools\\DbgSrv\\")

            Step 2 — Start dbgsrv in the guest:
              guest_run_command("start /b C:\\Tools\\DbgSrv\\dbgsrv.exe -t tcp:port=5064")

            Step 3 — Verify it's listening:
              guest_run_command("netstat -ano | findstr 5064")
              (Should show LISTENING on port 5064)

            Step 4 — Get the guest IP:
              Call get_system_state and read "VM IP Address"
              (Or: guest_run_command("ipconfig | findstr IPv4"))

            Step 5 — Connect from the MCP server:
              umd_dbgsrv_connect(vmIpAddress="<guest IP>")

            Step 6 — Find a target process PID:
              guest_list_processes
              (Or: guest_run_command("tasklist /FI \"IMAGENAME eq target.exe\""))

            Step 7 — Attach to the process:
              umd_dbgsrv_execute(action="attach", argument="<PID>")

            Step 8 — Run WinDbg commands:
              umd_dbgsrv_execute(action="command", argument="lm")
              umd_dbgsrv_execute(action="command", argument="~*k")
              umd_dbgsrv_execute(action="command", argument="!peb")

            Step 9 — Clean up:
              umd_dbgsrv_execute(action="detach")
              umd_dbgsrv_execute(action="disconnect")

            ── ATTACH MODE ───────────────────────────────────────────────
            Attach is NONINVASIVE — the target process keeps running.
            You get full read access to memory, modules, threads, PEB,
            and stack traces. You CANNOT set breakpoints, single-step,
            or modify memory (use Frida or kernel debugging for that).

            Each command spawns a fresh cdb.exe process that connects
            to dbgsrv, attaches to the target, runs the command, and
            exits. This means:
            • Commands are STATELESS — .sympath changes don't persist
            • Works simultaneously with kernel debugging (kd_*)
            • No DbgEng in-process conflicts

            You can attach/detach to different processes without
            reconnecting to dbgsrv.

            ── USEFUL WINDBG COMMANDS ────────────────────────────────────
            Process & threads:
              ~                        — list all threads
              ~*k                      — stack traces for ALL threads
              ~0s; k                   — switch to thread 0, show stack
              !peb                     — process environment block
              !teb                     — thread environment block
              |                        — show attached process info

            Modules:
              lm                       — list loaded modules
              lm m kernel32            — info for specific module
              x kernel32!Create*       — search exports by pattern
              !lmi kernel32            — detailed module info (version, path)

            Memory:
              db <addr>                — display bytes
              dw <addr>                — display words
              dd <addr>                — display dwords
              dq <addr>                — display qwords
              da <addr>                — display ASCII string
              du <addr>                — display Unicode string
              dp <addr>                — display pointer-sized values
              !address                 — full virtual memory map
              !address <addr>          — info about specific address

            Structures:
              dt ntdll!_PEB @$peb      — dump PEB structure
              dt ntdll!_TEB @$teb      — dump TEB structure
              dt ntdll!_LDR_DATA_TABLE_ENTRY <addr>  — module entry

            Stack analysis:
              k                        — current thread stack trace
              kv                       — stack with frame pointer info
              kp                       — stack with full parameters
              kb                       — stack with first 3 args
              .frame N                 — set context to frame N
              dv                       — display local variables (if symbols)

            Search:
              s -a <start> L<len> "text"  — search for ASCII string
              s -u <start> L<len> "text"  — search for Unicode string
              s -b <start> L<len> 4D 5A   — search for byte pattern (MZ)

            Symbols (NOTE: each command is a fresh cdb.exe, so
              .sympath changes do NOT persist between calls):
              .sympath                 — show symbol path
              .reload /f               — force reload symbols
              ln <addr>                — list nearest symbols to address

            ── COMMON WORKFLOWS ──────────────────────────────────────────
            Inspect a suspicious process:
              lm                       → list DLLs (look for anomalies)
              !peb                     → check command line, environment
              ~*k                      → all thread stacks (find activity)
              !address -summary        → memory layout overview

            Find strings in memory:
              !address -f:MEM_COMMIT   → get committed memory ranges
              s -u 0 L?0x7fffffff "password"  → search all user memory

            Analyze a DLL:
              lm m <dllname>           → base address and size
              x <dllname>!*            → list all exports
              !lmi <dllname>           → version, timestamp, path

            ── TROUBLESHOOTING ───────────────────────────────────────────
            "ConnectProcessServer failed" or "cannot reach dbgsrv":
              cdb.exe could not connect to the remote process server.
              Common causes:
              1. dbgsrv.exe is NOT running — start it (see Step 2 above)
              2. dbgsrv.exe is missing dbgeng.dll/dbghelp.dll in its dir
                 → Verify: guest_run_command("dir C:\\Tools\\DbgSrv\\")
                 → All 3 files must exist: dbgsrv.exe, dbgeng.dll, dbghelp.dll
              3. Guest firewall blocking port 5064
                 → Fix: guest_run_command("netsh advfirewall firewall add
                   rule name=dbgsrv dir=in action=allow protocol=TCP
                   localport=5064")
              4. Wrong IP address — verify with:
                 guest_run_command("ipconfig | findstr IPv4")

            "AttachProcess failed":
              → Verify the PID exists: guest_list_processes
              → Protected processes (e.g., csrss.exe, lsass.exe) may
                reject noninvasive attach. Try a different process.

            "cdb.exe timed out":
              → The command took too long. Try a simpler command first
                (e.g., "lm" instead of "~*k" on a process with many threads)
              → Check dbgsrv is still running in the guest

            "cdb.exe not found":
              → Install Windows SDK / WDK Debuggers on the host machine
              → Expected at: C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\cdb.exe

            "Not connected to dbgsrv":
              → Call umd_dbgsrv_connect first with the guest IP

            ── COMMON PITFALLS ───────────────────────────────────────────
            • The dbgsrv path is C:\Tools\DbgSrv\dbgsrv.exe (NOT C:\Tools\dbgsrv.exe)
            • Each command is a fresh cdb.exe process — state like .sympath
              changes do NOT persist between calls. Symbols use srv* by default.
            • Noninvasive mode: you CANNOT set breakpoints, step, or
              write memory. Use Frida for dynamic instrumentation.
            • Multiple dbgsrv instances on the same port will conflict.
              Kill extras via guest_kill_process before connecting.
            • Symbols may show (deferred) — run .reload /f to load them.
            • Works simultaneously with kernel debugging (kd_*) —
              no conflicts, completely independent.
            """));
    }

    [McpServerTool(Name = "umd_dbgsrv_connect"), Description(
        "Connect to dbgsrv.exe running in the guest VM for remote user-mode debugging. " +
        "Requires: dbgsrv.exe running in guest (C:\\Tools\\DbgSrv\\dbgsrv.exe -t tcp:port=5064), " +
        "and cdb.exe installed on the host (Windows SDK Debuggers). " +
        "Uses cdb.exe externally — fully independent from kernel debugging. " +
        "Both kernel debug and user-mode debug can be active simultaneously.")]
    public static Task<string> UmdDbgsrvConnect(
        StateCoordinator state,
        DbgsrvManager dbgsrv,
        [Description("Guest VM IP address")] string vmIpAddress,
        [Description("dbgsrv TCP port (default 5064)")] int port = 5064,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("umd_dbgsrv_connect", async () =>
        {
            try
            {
                return await dbgsrv.ConnectAsync(vmIpAddress, port, ct);
            }
            catch (OperationCanceledException)
            {
                return "umd_dbgsrv_connect timed out. Is dbgsrv.exe running in the guest? " +
                       "Start it: guest_run_command(\"start /b C:\\Tools\\DbgSrv\\dbgsrv.exe -t tcp:port=5064\")";
            }
            catch (Exception ex)
            {
                return $"umd_dbgsrv_connect failed: {ex.GetType().Name}: {ex.Message}";
            }
        });
    }

    [McpServerTool(Name = "umd_dbgsrv_execute"), Description(
        "Execute operations via the dbgsrv remote user-mode debug connection. " +
        "Actions: 'attach' (attach to PID), 'command' (run WinDbg command), " +
        "'detach', 'disconnect'.")]
    public static Task<string> UmdDbgsrvExecute(
        StateCoordinator state,
        DbgsrvManager dbgsrv,
        [Description("Action: 'attach', 'command', 'detach', 'disconnect'")] string action,
        [Description("For 'attach': PID. For 'command': WinDbg command string.")] string? argument = null,
        [Description("Timeout in seconds (default 30)")] int timeoutSeconds = 30,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("umd_dbgsrv_execute", async () =>
        {
            try
            {
                return action.ToLowerInvariant() switch
                {
                    "attach" => argument == null || !uint.TryParse(argument, out var pid)
                        ? "Provide 'argument' with the PID to attach to."
                        : await dbgsrv.AttachToProcessAsync(pid),

                    "command" => argument == null
                        ? "Provide 'argument' with the WinDbg command to execute."
                        : await dbgsrv.ExecuteCommandAsync(argument, timeoutSeconds),

                    "detach" => await dbgsrv.DetachAsync(),

                    "disconnect" => await dbgsrv.DisconnectAsync(),

                    _ => $"Unknown action '{action}'. Use: attach, command, detach, disconnect."
                };
            }
            catch (OperationCanceledException)
            {
                return $"umd_dbgsrv_execute '{action}' timed out. The DbgEng thread may be busy. " +
                       "Try again, or disconnect and reconnect.";
            }
            catch (Exception ex)
            {
                return $"umd_dbgsrv_execute '{action}' failed: {ex.GetType().Name}: {ex.Message}";
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════
    //  TTD TOOLS
    // ═══════════════════════════════════════════════════════════════

    [McpServerTool(Name = "umd_ttd"), Description(
        "Manage Time Travel Debugging recordings in the guest VM. " +
        "Actions: 'record_launch' (start a process under TTD), " +
        "'record_attach' (attach TTD to running PID), 'stop' (stop recording), " +
        "'retrieve' (copy trace to host), 'list' (list trace files). " +
        "Requires: TTD.exe installed in guest (C:\\Tools\\TTD\\TTD.exe).")]
    public static Task<string> UmdTtd(
        StateCoordinator state,
        TtdManager ttd,
        [Description("Action: 'record_launch', 'record_attach', 'stop', 'retrieve', 'list'")] string action,
        [Description("For 'record_launch': target executable path in guest. " +
                     "For 'record_attach': PID. " +
                     "For 'retrieve': guest trace file path.")] string? target = null,
        [Description("For 'record_launch': command line arguments")] string? arguments = null,
        [Description("For 'retrieve': host output path")] string? outputPath = null,
        [Description("Timeout in seconds (default 300 for recordings)")] int timeoutSeconds = 300,
        CancellationToken ct = default)
    {
        return state.RunToolAsync("umd_ttd", async () =>
        {
            try
            {
                return action.ToLowerInvariant() switch
                {
                    "record_launch" => target == null
                        ? "Provide 'target' with the executable path in the guest."
                        : await ttd.RecordLaunchAsync(target, arguments ?? "", timeoutSeconds: timeoutSeconds, ct: ct),

                    "record_attach" => target == null || !uint.TryParse(target, out var pid)
                        ? "Provide 'target' with the PID to record."
                        : await ttd.RecordAttachAsync(pid, timeoutSeconds: timeoutSeconds, ct: ct),

                    "stop" => await ttd.StopRecordingAsync(ct),

                    "retrieve" => target == null || outputPath == null
                        ? "Provide 'target' (guest trace path) and 'outputPath' (host path)."
                        : await ttd.RetrieveTraceAsync(target, outputPath, ct),

                    "list" => await ttd.ListTracesAsync(ct: ct),

                    _ => $"Unknown action '{action}'. Use: record_launch, record_attach, stop, retrieve, list."
                };
            }
            catch (OperationCanceledException)
            {
                return "umd_ttd timed out.";
            }
            catch (Exception ex)
            {
                return $"umd_ttd failed: {ex.GetType().Name}: {ex.Message}";
            }
        });
    }

    [McpServerTool(Name = "umd_ttd_query"), Description(
        "Query a TTD trace file on the host using WinDbg/DbgEng TTD queries. " +
        "The trace must first be retrieved from the guest via umd_ttd(action='retrieve'). " +
        "NOTE: This tool is not yet implemented — use WinDbg Preview to open .run files.")]
    public static Task<string> UmdTtdQuery(
        StateCoordinator state,
        [Description("Path to the .run trace file on the host")] string tracePath,
        [Description("TTD query (e.g., 'dx @$cursession.TTD.Calls(\"kernel32!CreateFileW\")')")] string query)
    {
        // TTD query via DbgEng requires opening the trace as a dump target
        // in a separate DbgEng session. This is complex to implement and is
        // deferred to a later phase. For now, suggest using WinDbg Preview.
        return state.RunToolAsync("umd_ttd_query", () => Task.FromResult(
            "umd_ttd_query is not yet implemented. To analyze TTD traces:\n" +
            $"1. Open the trace in WinDbg Preview: File > Open Trace > {tracePath}\n" +
            "2. Run TTD queries like:\n" +
            "   dx @$cursession.TTD.Calls(\"kernel32!CreateFileW\")\n" +
            "   dx @$cursession.TTD.Memory(0x12345, 0x12345+4, \"w\")\n" +
            "   !tt 0:0  (go to start of trace)\n" +
            "   !tt 100  (go to end of trace)\n" +
            "This feature will be implemented in a future update."));
    }
}
