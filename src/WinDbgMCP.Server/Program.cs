using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.Guest;
using WinDbgMCP.Server.KernelDebug;
using WinDbgMCP.Server.Notifications;
using WinDbgMCP.Server.State;
using WinDbgMCP.Server.Tools;
using WinDbgMCP.Server.UserModeDebug;
using WinDbgMCP.Server.Vmware;

var builder = Host.CreateApplicationBuilder(args);

// Load configuration relative to the executable, not CWD
var exeDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)!;
builder.Configuration.AddJsonFile(Path.Combine(exeDir, "appsettings.json"), optional: false, reloadOnChange: true);

// MCP servers use stdio — route all logs to stderr
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
    // Debug-event logging runs inside DbgEng callbacks; never let a full
    // stderr queue block the engine thread if the client stops draining it.
    options.QueueFullMode = Microsoft.Extensions.Logging.Console.ConsoleLoggerQueueFullMode.DropWrite;
});

// Bind configuration
var config = new ServerConfig();
builder.Configuration.Bind(config);

// Register services as singletons (single MCP server process, single VM)
builder.Services.AddSingleton(config);
builder.Services.AddSingleton<VmwareManager>();

// DbgEng thread + manager — kernel debugging layer
builder.Services.AddSingleton<DbgEngThread>(sp =>
    new DbgEngThread(sp.GetRequiredService<ILogger<DbgEngThread>>()));
builder.Services.AddSingleton<DbgEngManager>();

// Guest execution manager
builder.Services.AddSingleton<GuestExecManager>();

// Push important debug events to the MCP client (Notifications.PushDebugEvents)
builder.Services.AddHostedService<McpEventNotifier>();

// User-mode debug managers
builder.Services.AddSingleton<FridaManager>();
builder.Services.AddSingleton<DbgsrvManager>();
builder.Services.AddSingleton<TtdManager>();

builder.Services.AddSingleton<StateCoordinator>(sp =>
{
    var stateConfig = sp.GetRequiredService<ServerConfig>();
    var logger = sp.GetRequiredService<ILogger<StateCoordinator>>();
    var vmware = sp.GetRequiredService<VmwareManager>();
    var dbgEng = sp.GetRequiredService<DbgEngManager>();
    var frida = sp.GetRequiredService<FridaManager>();
    var dbgsrv = sp.GetRequiredService<DbgsrvManager>();

    var coordinator = new StateCoordinator(stateConfig, logger);

    // Wire up state refresh delegates to the VmwareManager
    coordinator.GetVmPowerStateAsync = () => vmware.GetPowerStateAsync();
    coordinator.AreToolsRunningAsync = (timeout) => vmware.AreToolsRunningAsync(timeout);

    // Wire up KD state delegates to DbgEngManager
    coordinator.IsDbgEngConnected = () => dbgEng.IsConnected;
    coordinator.IsEngineParked = () => dbgEng.EngineWedged;
    coordinator.GetEngineBusy = () => dbgEng.BusyDescription;
    coordinator.GetDbgEngExecutionStatus = () => dbgEng.GetExecutionStatus();
    coordinator.GetPendingEventCount = () => dbgEng.PendingEventCount;
    coordinator.GetPendingInformationalEventCount = () => dbgEng.PendingInformationalEventCount;
    coordinator.GetModuleEventsLast10s = () => dbgEng.ModuleEventsInLast10s;
    coordinator.GetRebootGeneration = () => dbgEng.RebootGeneration;
    coordinator.DetectBugcheckAsync = () => dbgEng.DetectBugcheckAsync();
    coordinator.IsSecondChancePending = () => dbgEng.SecondChancePending;
    coordinator.IsRebootDetected = () => dbgEng.RebootDetected;
    coordinator.DrainDebugEvents = () => dbgEng.DrainEvents();
    coordinator.GetRecentDebugEvents = () => dbgEng.RecentEvents;

    // Wire up UMD state delegates
    coordinator.IsFridaAttached = () => frida.IsAttached;
    coordinator.GetFridaTargetName = () => frida.AttachedProcessName;
    coordinator.IsDbgsrvConnected = () => dbgsrv.IsConnected;
    coordinator.GetDbgsrvAttachedPid = () => dbgsrv.AttachedPid;

    // Cleanup delegates for snapshot restore
    coordinator.CleanupKdSession = () => dbgEng.ResetConnectionState();
    coordinator.CleanupFridaSession = () => frida.Dispose();
    coordinator.CleanupDbgsrvSession = () => dbgsrv.Dispose();

    return coordinator;
});

// Build tool type list — conditionally exclude tools that don't work with encrypted VMs
var isEncrypted = !string.IsNullOrEmpty(config.Vm.VmPassword);
var toolTypes = new List<Type>
{
    typeof(VmTools),
    typeof(KernelDebugTools),
    typeof(GuestTools),
    typeof(UserModeDebugTools),
    typeof(MetaTools),
};
if (!isEncrypted)
{
    toolTypes.Add(typeof(VmScreenshotTool));
}

// Register MCP server with conditionally selected tools
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "WinDbgMCP",
            Version = "1.0.0-alpha"
        };
        options.ServerInstructions = BuildServerInstructions(config);
        // McpEventNotifier sends notifications/message; the spec requires the
        // capability to be declared. Mutate rather than replace — the SDK fills Tools.
        options.Capabilities ??= new();
        options.Capabilities.Logging = new();
    })
    .WithStdioServerTransport()
    .WithTools(toolTypes);

await builder.Build().RunAsync();

static string BuildServerInstructions(ServerConfig config)
{
    var sb = new System.Text.StringBuilder();

    sb.AppendLine("# WinDbgMCP — Windows VM Control & Kernel Debugging Server");
    sb.AppendLine();
    sb.AppendLine("You have full control over a Windows VM: lifecycle, kernel debugging, guest execution, and user-mode debugging.");
    sb.AppendLine();

    // === Credentials ===
    sb.AppendLine("## VM Credentials (from config)");
    sb.AppendLine($"- Guest username: {config.Vm.GuestUsername}");
    sb.AppendLine($"- Guest password: {config.Vm.GuestPassword}");
    if (!string.IsNullOrEmpty(config.Vm.VmPassword))
        sb.AppendLine($"- VM encryption password: {config.Vm.VmPassword}");
    sb.AppendLine($"- KDNET port: {config.KernelDebug.Kdnet.Port}");
    sb.AppendLine($"- KDNET key: {config.KernelDebug.Kdnet.Key}");
    sb.AppendLine($"- Frida port: {config.Guest.FridaPort}");
    sb.AppendLine($"- dbgsrv port: {config.Guest.DbgsrvPort}");
    sb.AppendLine();

    // === Default snapshot ===
    if (!string.IsNullOrEmpty(config.Security.DefaultSnapshotName))
    {
        sb.AppendLine("## Default Snapshot");
        sb.AppendLine($"- Name: \"{config.Security.DefaultSnapshotName}\"");
        sb.AppendLine("- This is the known-good recovery snapshot. Use vm_snapshot_restore with this name to revert to a clean, well-configured state (debug boot enabled, tools installed, logged in).");
        sb.AppendLine();
    }

    // === Guest tool paths ===
    sb.AppendLine("## Guest VM Tool Paths");
    sb.AppendLine("These tools are pre-installed in the guest VM at known paths:");
    sb.AppendLine("- **frida-server**: `C:\\Tools\\frida-server.exe` — should already be running (port 27042)");
    sb.AppendLine("- **dbgsrv**: `C:\\Tools\\DbgSrv\\dbgsrv.exe` — start manually when needed (port 5064)");
    sb.AppendLine("  - Dependencies in same dir: `dbgeng.dll`, `dbghelp.dll` (all 3 files required)");
    sb.AppendLine("- **TTD**: `C:\\Tools\\TTD\\TTD.exe` — for Time Travel Debugging recordings");
    sb.AppendLine();
    sb.AppendLine("To start dbgsrv: `guest_run_command(\"start /b C:\\Tools\\DbgSrv\\dbgsrv.exe -t tcp:port=5064\")`");
    sb.AppendLine("To verify frida-server: `guest_run_command(\"tasklist /FI \\\"IMAGENAME eq frida-server.exe\\\"\")`");
    sb.AppendLine();

    // === Tool catalog ===
    var vmIsEncrypted = !string.IsNullOrEmpty(config.Vm.VmPassword);
    var totalTools = vmIsEncrypted ? 27 : 28;
    sb.AppendLine($"## Tools ({totalTools} total)");
    sb.AppendLine();
    sb.AppendLine("### Meta");
    sb.AppendLine("- `get_system_state` — Full state overview (VM, KD, guest ops, UMD). ALWAYS allowed. Call when unsure.");
    sb.AppendLine();

    var vmToolCount = vmIsEncrypted ? 6 : 7;
    sb.AppendLine($"### VM Tools ({vmToolCount})");
    sb.AppendLine("- `vm_start` — Power on. Wait for VMware Tools before guest ops.");
    sb.AppendLine("- `vm_stop` — Shut down (hard=true for force power off).");
    sb.AppendLine("- `vm_pause` — Freeze entire VM (NOT the same as kd_break).");
    sb.AppendLine("- `vm_resume` — Unpause a paused VM.");
    sb.AppendLine("- `vm_snapshot_restore` — Restore checkpoint. DESTROYS all debug sessions.");
    sb.AppendLine("- `vm_snapshot_list` — List available snapshots.");
    if (!vmIsEncrypted)
        sb.AppendLine("- `vm_screenshot` — Capture VM display (boot screen, BSOD, desktop).");
    sb.AppendLine();

    sb.AppendLine("### Kernel Debug Tools (7) — requires `kd_connect` first");
    sb.AppendLine("- `kd_connect` — Attach to kernel via KDNET. VM must have debug boot enabled. Target breaks on connect.");
    sb.AppendLine("- `kd_disconnect` — Detach. Resumes target so VM keeps running.");
    sb.AppendLine("- `kd_break` — Halt running target (Ctrl+Break). After breaking, use kd_execute.");
    sb.AppendLine("- `kd_continue` — Resume target (go). Returns immediately. Guest ops require target running.");
    sb.AppendLine("- `kd_step` — Step one instruction (mode='into' or 'over').");
    sb.AppendLine("- `kd_execute` — Run any WinDbg command: k, r, lm, !process 0 0, !analyze -v, db, u, x, .reload, bp, etc.");
    sb.AppendLine("- `kd_wait_for_event` — Wait for breakpoint/exception (with timeout). Always returns.");
    sb.AppendLine();

    sb.AppendLine("### Guest Tools (5) — requires VM running + VMware Tools + target NOT frozen");
    sb.AppendLine("- `guest_run_command` — Execute command in guest (cmd.exe /c). Captures stdout/stderr.");
    sb.AppendLine("- `guest_transfer_to_vm` — Copy file host -> guest (deploy drivers, tools).");
    sb.AppendLine("- `guest_transfer_from_vm` — Copy file guest -> host (retrieve dumps, logs).");
    sb.AppendLine("- `guest_list_processes` — List running processes with PIDs.");
    sb.AppendLine("- `guest_kill_process` — Kill process by PID.");
    sb.AppendLine();

    sb.AppendLine("### User-Mode Debug Tools (8)");
    sb.AppendLine("- `umd_frida_attach` — Attach Frida to guest process (requires frida-server in guest).");
    sb.AppendLine("- `umd_frida` — Frida operations: inject JS, eval, list processes, detach.");
    sb.AppendLine("- `umd_frida_skill` — Get Frida best practices, API reference, and usage patterns. Call before using Frida.");
    sb.AppendLine("- `umd_dbgsrv_connect` — Connect to dbgsrv.exe in guest for user-mode debugging.");
    sb.AppendLine("- `umd_dbgsrv_execute` — Attach to PID, run WinDbg commands, detach, disconnect.");
    sb.AppendLine("- `umd_dbgsrv_skill` — Get dbgsrv best practices, WinDbg command reference. Call before using dbgsrv.");
    sb.AppendLine("- `umd_ttd` — Time Travel Debugging: record_launch, record_attach, stop, retrieve, list.");
    sb.AppendLine("- `umd_ttd_query` — Query TTD traces (not yet implemented — use WinDbg Preview).");
    sb.AppendLine();

    // === Critical rules ===
    sb.AppendLine("## Critical Rules");
    sb.AppendLine();
    sb.AppendLine("1. **BREAK vs RUNNING**: Kernel debug commands (kd_execute, kd_step) require the target to be at a BREAK. Guest operations (guest_run_command, guest_transfer_*) require the target to be RUNNING. If the kernel debugger froze the VM, call `kd_continue` before guest ops.");
    sb.AppendLine();
    sb.AppendLine("2. **Blocked commands in kd_execute**: `g`, `gh`, `gn`, `gu`, `p`, `t`, `pa`, `ta`, `wt`, `tt`, `pc`, `tc` are BLOCKED because they change execution state. Use `kd_continue` (go) or `kd_step` (step) instead.");
    sb.AppendLine();
    sb.AppendLine("3. **kd_wait_for_event is safe**: It ALWAYS returns within the timeout. Use it after kd_continue + breakpoint to wait for the breakpoint to trigger.");
    sb.AppendLine();
    sb.AppendLine("4. **After BSOD**: get_system_state will show BSOD DETECTED. You can still debug — use kd_execute('!analyze -v'), kd_execute('k'), kd_execute('r'), etc. to investigate the crash. Guest operations won't work while at the BSOD. To recover: (a) kd_continue — the kernel writes the dump and reboots (some targets break in a second time first; that is expected — kd_continue again); kd_wait_for_event(90) / get_system_state then show TARGET REBOOTED at the initial breakpoint, and one more kd_continue boots the OS; OR (b) vm_snapshot_restore to revert to a clean state; OR (c) vm_stop(hard=true) + vm_start if the VM never reboots on its own. Choose whichever fits your goal.");
    sb.AppendLine();
    sb.AppendLine("5. **Snapshot restore resets everything**: All debug sessions (KD, Frida, dbgsrv) are destroyed. Reconnect after restoring.");
    sb.AppendLine();
    sb.AppendLine("5b. **Restarting the guest on purpose** (guest_run_command('shutdown /r'), vm_stop/vm_start): call kd_disconnect FIRST, then restart, then kd_connect once the OS is up. After a graceful restart the kernel does not re-attach to an existing debugger session; only a crash (BSOD) reboot reconnects automatically at the initial breakpoint. If get_system_state ever reports the kernel did not re-attach, kd_disconnect then kd_connect.");
    sb.AppendLine();
    sb.AppendLine("6. **get_system_state first**: When unsure about the current state, call get_system_state. It's always allowed and tells you exactly what's available.");
    sb.AppendLine();

    // === Common workflows ===
    sb.AppendLine("## Common Workflows");
    sb.AppendLine();
    sb.AppendLine("**Inspect a running kernel:**");
    sb.AppendLine("kd_connect -> kd_execute('lm') -> kd_execute('!process 0 0') -> kd_disconnect");
    sb.AppendLine();
    sb.AppendLine("**Set breakpoint and wait:**");
    sb.AppendLine("kd_connect -> kd_execute('bp nt!NtCreateFile') -> kd_continue -> kd_wait_for_event(30) -> kd_execute('k') -> kd_disconnect");
    sb.AppendLine();
    sb.AppendLine("**Deploy and debug a driver:**");
    sb.AppendLine("guest_transfer_to_vm(driver.sys, C:\\Windows\\System32\\drivers\\driver.sys) -> guest_run_command('sc create MyDrv type= kernel binPath= ...') -> guest_run_command('sc start MyDrv') -> kd_connect -> kd_execute('lm m MyDrv') -> kd_disconnect");
    sb.AppendLine();
    sb.AppendLine("**Execute command in guest:**");
    sb.AppendLine("guest_run_command('ipconfig /all') — runs in guest, returns stdout/stderr");
    sb.AppendLine();
    sb.AppendLine("**Crash analysis:**");
    sb.AppendLine("kd_connect -> kd_execute('!analyze -v') -> kd_execute('k') -> kd_execute('r') -> kd_disconnect");
    sb.AppendLine();
    sb.AppendLine("**Record with TTD:**");
    sb.AppendLine("umd_ttd(action='record_launch', target='C:\\path\\to\\app.exe') -> [use the app] -> umd_ttd(action='stop') -> umd_ttd(action='retrieve', target='trace.run', outputPath='C:\\host\\trace.run')");

    return sb.ToString();
}
