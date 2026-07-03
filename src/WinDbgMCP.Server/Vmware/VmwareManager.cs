using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.State;

namespace WinDbgMCP.Server.Vmware;

/// <summary>
/// Wraps vmrun CLI. All operations are async with timeout.
/// Every vmrun call:
///   1. Has a timeout (kills process on timeout)
///   2. Captures stdout and stderr
///   3. Parses exit codes (0 = success, nonzero = error)
/// </summary>
public sealed class VmwareManager
{
    private readonly string _vmrunPath;
    private string _vmxPath;
    private string _vmPassword;
    private string _guestUser;
    private string _guestPass;
    private readonly TimeoutConfig _timeouts;
    private readonly SecurityConfig _security;
    private readonly ILogger<VmwareManager> _logger;

    // Serialize vmrun calls — VMware doesn't handle concurrent vmrun processes well
    // for the same VM. Concurrent calls can corrupt internal state and cause timeouts.
    private readonly SemaphoreSlim _vmrunLock = new(1, 1);

    // EXTERNAL-TARGET mode: when vmrun.exe is not present we cannot manage a local
    // VMware VM, but kernel debugging over KDNET to a physical / externally-managed
    // target still works. In that mode VM/guest tools are disabled and the target is
    // treated as "always running" so kd_connect is not gated on a VMware power state.
    private readonly bool _available;

    public bool Available => _available;

    public string VmxPath => _vmxPath;
    public string GuestUser => _guestUser;

    public VmwareManager(ServerConfig config, ILogger<VmwareManager> logger)
    {
        _vmrunPath = config.Vm.VmrunPath;
        _vmxPath = config.Vm.VmxPath;
        _vmPassword = config.Vm.VmPassword;
        _guestUser = config.Vm.GuestUsername;
        _guestPass = config.Vm.GuestPassword;
        _timeouts = config.Timeouts;
        _security = config.Security;
        _logger = logger;

        // Do NOT throw when vmrun is missing — that would break EVERY tool (VmwareManager
        // is a dependency of StateCoordinator, which every tool resolves). Instead fall back
        // to EXTERNAL-TARGET mode so kernel debugging over KDNET still works.
        _available = File.Exists(_vmrunPath);
        if (!_available)
        {
            _logger.LogWarning(
                "vmrun not found at '{Path}'. Running in EXTERNAL-TARGET mode: VM and guest " +
                "tools are disabled; kernel debugging over KDNET only.", _vmrunPath);
        }
    }

    /// <summary>
    /// Switch the active VM target at runtime.
    /// All subsequent VM and guest operations will target the new VM.
    /// </summary>
    public void UpdateTarget(string vmxPath, string guestUser, string guestPass, string vmPassword = "")
    {
        _vmxPath = vmxPath;
        _guestUser = guestUser;
        _guestPass = guestPass;
        _vmPassword = vmPassword;
        _logger.LogInformation("VM target updated: {VmxPath} (user: {User})", vmxPath, guestUser);
    }

    // ═══════════════════════════════════════════════════════════════
    //  POWER OPERATIONS
    // ═══════════════════════════════════════════════════════════════

    public async Task<VmResult> StartAsync(bool headless = true, CancellationToken ct = default)
    {
        var guiArg = headless ? "nogui" : "gui";
        var result = await RunVmrunAsync(
            $"-T ws start \"{_vmxPath}\" {guiArg}",
            TimeSpan.FromSeconds(_timeouts.VmStartSeconds), ct);

        if (result.Success)
            return VmResult.Ok("VM started successfully.");

        return VmResult.Failed(
            $"Failed to start VM: {result.Stderr.Trim()}",
            $"Exit code: {result.ExitCode}");
    }

    public async Task<VmResult> StopAsync(bool hard = false, CancellationToken ct = default)
    {
        var mode = hard ? "hard" : "soft";
        var result = await RunVmrunAsync(
            $"-T ws stop \"{_vmxPath}\" {mode}",
            TimeSpan.FromSeconds(_timeouts.VmStopSeconds), ct);

        if (result.Success)
            return VmResult.Ok($"VM stopped ({mode}).");

        return VmResult.Failed(
            $"Failed to stop VM: {result.Stderr.Trim()}",
            $"Exit code: {result.ExitCode}");
    }

    public async Task<VmResult> PauseAsync(CancellationToken ct = default)
    {
        var result = await RunVmrunAsync(
            $"-T ws pause \"{_vmxPath}\"",
            TimeSpan.FromSeconds(_timeouts.VmPauseResumeSeconds), ct);

        if (result.Success)
            return VmResult.Ok("VM paused.");

        return VmResult.Failed($"Failed to pause VM: {result.Stderr.Trim()}");
    }

    public async Task<VmResult> UnpauseAsync(CancellationToken ct = default)
    {
        var result = await RunVmrunAsync(
            $"-T ws unpause \"{_vmxPath}\"",
            TimeSpan.FromSeconds(_timeouts.VmPauseResumeSeconds), ct);

        if (result.Success)
            return VmResult.Ok("VM resumed.");

        return VmResult.Failed($"Failed to unpause VM: {result.Stderr.Trim()}");
    }

    public async Task<VmResult> ResetAsync(bool hard = false, CancellationToken ct = default)
    {
        var mode = hard ? "hard" : "soft";
        var result = await RunVmrunAsync(
            $"-T ws reset \"{_vmxPath}\" {mode}",
            TimeSpan.FromSeconds(_timeouts.VmStartSeconds), ct);

        if (result.Success)
            return VmResult.Ok($"VM reset ({mode}).");

        return VmResult.Failed($"Failed to reset VM: {result.Stderr.Trim()}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  SNAPSHOT OPERATIONS
    // ═══════════════════════════════════════════════════════════════

    public async Task<VmResult> SnapshotCreateAsync(string name, CancellationToken ct = default)
    {
        var result = await RunVmrunAsync(
            $"-T ws snapshot \"{_vmxPath}\" \"{name}\"",
            TimeSpan.FromSeconds(_timeouts.VmSnapshotCreateSeconds), ct);

        if (result.Success)
            return VmResult.Ok($"Snapshot '{name}' created.");

        return VmResult.Failed($"Failed to create snapshot: {result.Stderr.Trim()}");
    }

    public async Task<VmResult> SnapshotRestoreAsync(string name, CancellationToken ct = default)
    {
        var result = await RunVmrunAsync(
            $"-T ws revertToSnapshot \"{_vmxPath}\" \"{name}\"",
            TimeSpan.FromSeconds(_timeouts.VmSnapshotRestoreSeconds), ct);

        if (result.Success)
            return VmResult.Ok($"Snapshot '{name}' restored.");

        return VmResult.Failed($"Failed to restore snapshot: {result.Stderr.Trim()}");
    }

    public async Task<VmResult> SnapshotDeleteAsync(string name, CancellationToken ct = default)
    {
        // Safeguard 1: Snapshot deletion must be explicitly enabled
        if (!_security.SnapshotDeleteEnabled)
            return VmResult.Failed(
                "Snapshot deletion is DISABLED. Set Security.SnapshotDeleteEnabled=true in appsettings.json to allow.");

        // Safeguard 2: Protected snapshots cannot be deleted
        if (_security.ProtectedSnapshots.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return VmResult.Failed(
                $"Snapshot '{name}' is PROTECTED and cannot be deleted. " +
                "Remove it from Security.ProtectedSnapshots in appsettings.json to allow.");

        // Safeguard 3: Refuse to delete the last snapshot
        if (_security.PreventLastSnapshotDeletion)
        {
            var listResult = await SnapshotListAsync(ct);
            if (listResult.Success && listResult.Snapshots.Count <= 1)
                return VmResult.Failed(
                    "REFUSED: This is the LAST snapshot. Deleting it would leave no recovery point. " +
                    "Set Security.PreventLastSnapshotDeletion=false to override (not recommended).");
        }

        var result = await RunVmrunAsync(
            $"-T ws deleteSnapshot \"{_vmxPath}\" \"{name}\"",
            TimeSpan.FromSeconds(_timeouts.VmSnapshotRestoreSeconds), ct);

        if (result.Success)
            return VmResult.Ok($"Snapshot '{name}' deleted.");

        return VmResult.Failed($"Failed to delete snapshot: {result.Stderr.Trim()}");
    }

    public async Task<SnapshotListResult> SnapshotListAsync(CancellationToken ct = default)
    {
        var result = await RunVmrunAsync(
            $"-T ws listSnapshots \"{_vmxPath}\"",
            TimeSpan.FromSeconds(_timeouts.VmToolsCheckSeconds), ct);

        if (!result.Success)
            return SnapshotListResult.Failed($"Failed to list snapshots: {result.Stderr.Trim()}");

        // Parse output: first line is "Total snapshots: N", then one snapshot name per line
        var lines = result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var snapshots = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("Total snapshots:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(line))
                snapshots.Add(line);
        }

        return SnapshotListResult.Ok(snapshots);
    }

    // ═══════════════════════════════════════════════════════════════
    //  STATE QUERIES
    // ═══════════════════════════════════════════════════════════════

    public async Task<VmPowerState> GetPowerStateAsync(CancellationToken ct = default)
    {
        // External-target mode: no vmrun to query. Treat the KDNET target as running so
        // kd_connect's "VM must be running" precondition is satisfied.
        if (!_available)
            return VmPowerState.Running;

        try
        {
            // vmrun list returns all running VMs
            var result = await RunVmrunAsync(
                "-T ws list",
                TimeSpan.FromSeconds(_timeouts.VmToolsCheckSeconds), ct);

            if (!result.Success)
            {
                _logger.LogWarning("vmrun list failed: {Stderr}", result.Stderr);
                return VmPowerState.Unknown;
            }

            // Check if our VMX is in the list of running VMs
            // vmrun list output: "Total running VMs: N\npath1\npath2\n..."
            var vmxNormalized = _vmxPath.Replace('/', '\\').TrimEnd('\\');
            var isRunning = result.Stdout
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(line => !line.StartsWith("Total running VMs:") &&
                             line.Replace('/', '\\').TrimEnd('\\')
                                 .Equals(vmxNormalized, StringComparison.OrdinalIgnoreCase));

            return isRunning ? VmPowerState.Running : VmPowerState.Off;
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("vmrun list timed out");
            return VmPowerState.Unknown;
        }
    }

    public async Task<bool> AreToolsRunningAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        // External-target mode: no VMware Tools. Guest ops go through other channels (e.g. SSH).
        if (!_available)
            return false;

        // vmrun checkToolsState returns "running", "installed", or "unknown"
        // This can HANG if the guest is kernel-broken, so we use a short timeout.
        timeout ??= TimeSpan.FromSeconds(_timeouts.VmToolsCheckSeconds);
        try
        {
            var result = await RunVmrunAsync(
                $"-T ws checkToolsState \"{_vmxPath}\"",
                timeout.Value, ct);
            return result.Stdout.Trim().Equals("running", StringComparison.OrdinalIgnoreCase);
        }
        catch (TimeoutException)
        {
            return false; // Tools not responding
        }
    }

    public async Task<string?> GetGuestIpAddressAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await RunVmrunAsync(
                $"-T ws getGuestIPAddress \"{_vmxPath}\"",
                TimeSpan.FromSeconds(_timeouts.VmGetIpSeconds), ct);

            if (result.Success)
            {
                var ip = result.Stdout.Trim();
                // Validate it looks like an IP
                if (System.Net.IPAddress.TryParse(ip, out _))
                    return ip;
            }

            return null;
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    public async Task<VmResult> CaptureScreenAsync(string outputPath, CancellationToken ct = default)
    {
        // Ensure output directory exists
        var dir = Path.GetDirectoryName(outputPath);
        if (dir != null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var result = await RunVmrunAsync(
            $"-T ws -gu \"{_guestUser}\" -gp \"{_guestPass}\" captureScreen \"{_vmxPath}\" \"{outputPath}\"",
            TimeSpan.FromSeconds(_timeouts.VmScreenshotSeconds), ct);

        if (result.Success)
            return VmResult.Ok($"Screenshot saved to {outputPath}");

        return VmResult.Failed($"Failed to capture screen: {result.Stderr.Trim()}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  GUEST OPERATIONS (used by GuestExecManager)
    // ═══════════════════════════════════════════════════════════════

    public async Task<ProcessResult> RunProgramInGuestAsync(
        string program, string arguments = "", bool interactive = false,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        timeout ??= TimeSpan.FromSeconds(_timeouts.GuestCommandSeconds);
        var interactiveArg = interactive ? "-interactive " : "";
        return await RunVmrunAsync(
            $"-T ws -gu \"{_guestUser}\" -gp \"{_guestPass}\" " +
            $"runProgramInGuest \"{_vmxPath}\" {interactiveArg}\"{program}\" {arguments}",
            timeout.Value, ct);
    }

    public async Task<ProcessResult> RunScriptInGuestAsync(
        string interpreter, string scriptText,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        timeout ??= TimeSpan.FromSeconds(_timeouts.GuestCommandSeconds);
        return await RunVmrunAsync(
            $"-T ws -gu \"{_guestUser}\" -gp \"{_guestPass}\" " +
            $"runScriptInGuest \"{_vmxPath}\" \"{interpreter}\" \"{scriptText}\"",
            timeout.Value, ct);
    }

    public async Task<ProcessResult> CopyFileToGuestAsync(
        string hostPath, string guestPath, CancellationToken ct = default)
    {
        return await RunVmrunAsync(
            $"-T ws -gu \"{_guestUser}\" -gp \"{_guestPass}\" " +
            $"copyFileFromHostToGuest \"{_vmxPath}\" \"{hostPath}\" \"{guestPath}\"",
            TimeSpan.FromSeconds(_timeouts.GuestFileTransferSeconds), ct);
    }

    public async Task<ProcessResult> CopyFileFromGuestAsync(
        string guestPath, string hostPath, CancellationToken ct = default)
    {
        return await RunVmrunAsync(
            $"-T ws -gu \"{_guestUser}\" -gp \"{_guestPass}\" " +
            $"copyFileFromGuestToHost \"{_vmxPath}\" \"{guestPath}\" \"{hostPath}\"",
            TimeSpan.FromSeconds(_timeouts.GuestFileTransferSeconds), ct);
    }

    public async Task<ProcessResult> ListProcessesInGuestAsync(CancellationToken ct = default)
    {
        return await RunVmrunAsync(
            $"-T ws -gu \"{_guestUser}\" -gp \"{_guestPass}\" " +
            $"listProcessesInGuest \"{_vmxPath}\"",
            TimeSpan.FromSeconds(_timeouts.GuestListProcessesSeconds), ct);
    }

    public async Task<ProcessResult> KillProcessInGuestAsync(uint pid, CancellationToken ct = default)
    {
        return await RunVmrunAsync(
            $"-T ws -gu \"{_guestUser}\" -gp \"{_guestPass}\" " +
            $"killProcessInGuest \"{_vmxPath}\" {pid}",
            TimeSpan.FromSeconds(_timeouts.GuestKillProcessSeconds), ct);
    }

    public async Task<ProcessResult> FileExistsInGuestAsync(string guestPath, CancellationToken ct = default)
    {
        return await RunVmrunAsync(
            $"-T ws -gu \"{_guestUser}\" -gp \"{_guestPass}\" " +
            $"fileExistsInGuest \"{_vmxPath}\" \"{guestPath}\"",
            TimeSpan.FromSeconds(_timeouts.VmToolsCheckSeconds), ct);
    }

    public async Task<ProcessResult> CreateDirectoryInGuestAsync(string guestPath, CancellationToken ct = default)
    {
        return await RunVmrunAsync(
            $"-T ws -gu \"{_guestUser}\" -gp \"{_guestPass}\" " +
            $"createDirectoryInGuest \"{_vmxPath}\" \"{guestPath}\"",
            TimeSpan.FromSeconds(_timeouts.VmToolsCheckSeconds), ct);
    }

    // ═══════════════════════════════════════════════════════════════
    //  INTERNAL: vmrun process execution
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Run vmrun with timeout. Captures stdout/stderr. Kills on timeout.
    /// </summary>
    internal async Task<ProcessResult> RunVmrunAsync(
        string args, TimeSpan timeout, CancellationToken ct = default)
    {
        // Serialize vmrun calls — VMware can't handle concurrent vmrun processes
        // for the same VM. Without this, a slow captureScreen can corrupt state
        // and cause all subsequent vmrun calls to time out.
        // Use a timeout on semaphore acquisition to avoid indefinite blocking.
        var lockTimeout = timeout + TimeSpan.FromSeconds(10);
        if (!await _vmrunLock.WaitAsync(lockTimeout, ct))
        {
            throw new TimeoutException(
                $"vmrun queue timeout — another vmrun command has been running for >{lockTimeout.TotalSeconds}s. " +
                "VMware may be stuck. Try vm_snapshot_restore to recover.");
        }

        try
        {
            return await RunVmrunCoreAsync(args, timeout, ct);
        }
        finally
        {
            _vmrunLock.Release();
        }
    }

    private async Task<ProcessResult> RunVmrunCoreAsync(
        string args, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        // Prepend VM encryption password if configured
        if (!string.IsNullOrEmpty(_vmPassword))
            args = $"-vp \"{_vmPassword}\" {args}";

        _logger.LogDebug("vmrun {Args}", args);

        var psi = new ProcessStartInfo
        {
            FileName = _vmrunPath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

        try
        {
            await process.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            _logger.LogDebug("vmrun exit={ExitCode} stdout={Stdout} stderr={Stderr}",
                process.ExitCode, stdout.Length > 200 ? stdout[..200] + "..." : stdout, stderr);

            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our timeout fired, not the caller's cancellation.
            // Kill the process and wait for it to actually exit to avoid
            // leaving orphaned vmrun processes that block future calls.
            try
            {
                process.Kill(entireProcessTree: true);
                // Wait up to 3s for the process to actually die
                using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await process.WaitForExitAsync(exitCts.Token); } catch { }
            }
            catch { }

            throw new TimeoutException(
                $"vmrun timed out after {timeout.TotalSeconds}s. " +
                $"Command: vmrun {args.Split(' ').FirstOrDefault()}...");
        }
    }
}
