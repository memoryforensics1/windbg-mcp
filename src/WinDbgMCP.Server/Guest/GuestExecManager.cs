using Microsoft.Extensions.Logging;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.Guest.Models;
using WinDbgMCP.Server.Vmware;

namespace WinDbgMCP.Server.Guest;

/// <summary>
/// Manages guest command execution with stdout/stderr capture.
/// Wraps VmwareManager's low-level vmrun guest operations with temp-file
/// redirection to capture command output.
/// </summary>
public sealed class GuestExecManager
{
    private readonly VmwareManager _vmware;
    private readonly ServerConfig _config;
    private readonly ILogger<GuestExecManager> _logger;

    public GuestExecManager(VmwareManager vmware, ServerConfig config, ILogger<GuestExecManager> logger)
    {
        _vmware = vmware;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Execute a command inside the guest VM and capture stdout/stderr.
    /// Uses the batch-file approach: writes a .bat wrapper on the host that redirects
    /// stdout/stderr to temp files, copies it to the guest, executes it via
    /// runProgramInGuest, then retrieves the output files.
    /// Note: runProgramInGuest uses CreateProcess (no shell), so > redirection in args
    /// doesn't work. runScriptInGuest hangs with cmd.exe. The batch-file approach is
    /// the only reliable method for output capture.
    /// </summary>
    public async Task<GuestCommandResult> RunCommandAsync(
        string command,
        string? workingDirectory = null,
        int timeoutSeconds = 60,
        CancellationToken ct = default)
    {
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        var guid = Guid.NewGuid().ToString("N")[..8];
        var guestBat = $@"C:\Windows\Temp\mcp_cmd_{guid}.bat";
        var guestStdout = $@"C:\Windows\Temp\mcp_out_{guid}.txt";
        var guestStderr = $@"C:\Windows\Temp\mcp_err_{guid}.txt";

        _logger.LogDebug("Guest exec: {Cmd} (timeout={Timeout}s)", command, timeoutSeconds);

        // Host temp files
        var hostTempDir = Path.GetTempPath();
        var hostBat = Path.Combine(hostTempDir, $"mcp_cmd_{guid}.bat");
        var hostStdout = Path.Combine(hostTempDir, $"mcp_out_{guid}.txt");
        var hostStderr = Path.Combine(hostTempDir, $"mcp_err_{guid}.txt");

        try
        {
            // Build batch file content with output redirection
            string wrappedCmd;
            if (workingDirectory != null)
                wrappedCmd = $"cd /d \"{workingDirectory}\" && {command}";
            else
                wrappedCmd = command;

            var batContent = $"@echo off\r\n{wrappedCmd} > \"{guestStdout}\" 2> \"{guestStderr}\"\r\nexit /b %ERRORLEVEL%";
            await File.WriteAllTextAsync(hostBat, batContent, System.Text.Encoding.ASCII, ct);

            // Copy batch file to guest
            var copyBat = await _vmware.CopyFileToGuestAsync(hostBat, guestBat, ct: ct);
            if (!copyBat.Success)
                return GuestCommandResult.Failed(
                    $"Failed to copy command script to guest: {copyBat.Stderr.Trim()}");

            // Execute the batch file in the guest
            var execResult = await _vmware.RunProgramInGuestAsync(
                @"C:\Windows\System32\cmd.exe",
                $"/c \"{guestBat}\"",
                timeout: timeout,
                ct: ct);

            var exitCode = execResult.ExitCode;

            // Copy stdout/stderr files from guest to host
            string stdout = "";
            string stderr = "";

            try
            {
                var copyOut = await _vmware.CopyFileFromGuestAsync(guestStdout, hostStdout, ct: ct);
                if (copyOut.Success && File.Exists(hostStdout))
                    stdout = (await File.ReadAllTextAsync(hostStdout, ct)).Trim();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to retrieve stdout from guest");
            }

            try
            {
                var copyErr = await _vmware.CopyFileFromGuestAsync(guestStderr, hostStderr, ct: ct);
                if (copyErr.Success && File.Exists(hostStderr))
                    stderr = (await File.ReadAllTextAsync(hostStderr, ct)).Trim();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to retrieve stderr from guest");
            }

            // Cleanup host temp files
            SafeDeleteFile(hostBat);
            SafeDeleteFile(hostStdout);
            SafeDeleteFile(hostStderr);

            // Best-effort cleanup in guest (don't fail if this times out)
            _ = Task.Run(async () =>
            {
                try
                {
                    await _vmware.RunProgramInGuestAsync(
                        @"C:\Windows\System32\cmd.exe",
                        $"/c del /q \"{guestBat}\" \"{guestStdout}\" \"{guestStderr}\"",
                        timeout: TimeSpan.FromSeconds(10), ct: CancellationToken.None);
                }
                catch { }
            }, CancellationToken.None);

            return GuestCommandResult.Ok(exitCode, stdout, stderr);
        }
        catch (TimeoutException)
        {
            SafeDeleteFile(hostBat);
            return GuestCommandResult.Failed(
                $"Command timed out after {timeoutSeconds}s. " +
                "The command may still be running in the guest. " +
                "Use guest_list_processes to check, and guest_kill_process to stop it.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SafeDeleteFile(hostBat);
            _logger.LogError(ex, "Guest command failed: {Cmd}", command);
            return GuestCommandResult.Failed(
                $"Guest command failed: {ex.Message}. " +
                "VMware Tools may not be responding — check get_system_state.");
        }
    }

    private static readonly string TransferLogPath = Path.Combine(
        AppContext.BaseDirectory, "file_transfer.log");

    private const int MinTransferTimeoutSeconds = 120;
    private const int BytesPerSecondEstimate = 2 * 1024 * 1024; // 2 MB/s conservative

    private TimeSpan ComputeTimeout(long fileSize)
    {
        var scaledSeconds = (int)(fileSize / BytesPerSecondEstimate) + 30; // 30s overhead
        return TimeSpan.FromSeconds(Math.Max(scaledSeconds, MinTransferTimeoutSeconds));
    }

    private static void AppendTransferLog(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
            File.AppendAllText(TransferLogPath, line);
        }
        catch { }
    }

    /// <summary>
    /// Copy a file from the host to the guest VM.
    /// Timeout scales with file size (minimum 120s, ~2 MB/s estimate + 30s overhead).
    /// All transfer attempts are logged to file_transfer.log for diagnostics.
    /// </summary>
    public async Task<string> CopyFileToGuestAsync(
        string hostPath, string guestPath, CancellationToken ct = default)
    {
        if (!File.Exists(hostPath))
            return $"Host file not found: {hostPath}";

        var fileSize = new FileInfo(hostPath).Length;
        var timeout = ComputeTimeout(fileSize);

        AppendTransferLog($"TO_GUEST START host={hostPath} guest={guestPath} " +
            $"size={fileSize:N0} timeout={timeout.TotalSeconds}s");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var result = await _vmware.CopyFileToGuestAsync(hostPath, guestPath, timeout, ct);
            sw.Stop();

            if (!result.Success)
            {
                AppendTransferLog($"TO_GUEST FAILED elapsed={sw.ElapsedMilliseconds}ms " +
                    $"exitCode={result.ExitCode} stderr={result.Stderr.Trim()}");
                return $"File transfer failed: {result.Stderr}";
            }

            AppendTransferLog($"TO_GUEST OK elapsed={sw.ElapsedMilliseconds}ms " +
                $"size={fileSize:N0} rate={fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001):N0} B/s");
            return $"Copied {hostPath} -> guest:{guestPath} ({fileSize:N0} bytes, {sw.Elapsed.TotalSeconds:F1}s)";
        }
        catch (TimeoutException ex)
        {
            sw.Stop();
            AppendTransferLog($"TO_GUEST TIMEOUT elapsed={sw.ElapsedMilliseconds}ms " +
                $"timeout={timeout.TotalSeconds}s message={ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppendTransferLog($"TO_GUEST EXCEPTION elapsed={sw.ElapsedMilliseconds}ms " +
                $"type={ex.GetType().Name} message={ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Copy a file from the guest VM to the host.
    /// Timeout uses the configured default (guest file size is unknown upfront).
    /// All transfer attempts are logged to file_transfer.log for diagnostics.
    /// </summary>
    public async Task<string> CopyFileFromGuestAsync(
        string guestPath, string hostPath, CancellationToken ct = default)
    {
        var hostDir = Path.GetDirectoryName(hostPath);
        if (!string.IsNullOrEmpty(hostDir) && !Directory.Exists(hostDir))
            Directory.CreateDirectory(hostDir);

        AppendTransferLog($"FROM_GUEST START guest={guestPath} host={hostPath} " +
            $"timeout={_config.Timeouts.GuestFileTransferSeconds}s");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var result = await _vmware.CopyFileFromGuestAsync(guestPath, hostPath, ct: ct);
            sw.Stop();

            if (!result.Success)
            {
                AppendTransferLog($"FROM_GUEST FAILED elapsed={sw.ElapsedMilliseconds}ms " +
                    $"exitCode={result.ExitCode} stderr={result.Stderr.Trim()}");
                return $"File transfer failed: {result.Stderr}";
            }

            if (File.Exists(hostPath))
            {
                var fileSize = new FileInfo(hostPath).Length;
                AppendTransferLog($"FROM_GUEST OK elapsed={sw.ElapsedMilliseconds}ms " +
                    $"size={fileSize:N0} rate={fileSize / Math.Max(sw.Elapsed.TotalSeconds, 0.001):N0} B/s");
                return $"Copied guest:{guestPath} -> {hostPath} ({fileSize:N0} bytes, {sw.Elapsed.TotalSeconds:F1}s)";
            }

            AppendTransferLog($"FROM_GUEST MISSING elapsed={sw.ElapsedMilliseconds}ms " +
                "file not found at destination after successful vmrun");
            return $"Transfer completed but file not found at {hostPath}";
        }
        catch (TimeoutException ex)
        {
            sw.Stop();
            AppendTransferLog($"FROM_GUEST TIMEOUT elapsed={sw.ElapsedMilliseconds}ms " +
                $"timeout={_config.Timeouts.GuestFileTransferSeconds}s message={ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppendTransferLog($"FROM_GUEST EXCEPTION elapsed={sw.ElapsedMilliseconds}ms " +
                $"type={ex.GetType().Name} message={ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// List processes running in the guest VM.
    /// </summary>
    public async Task<string> ListProcessesAsync(CancellationToken ct = default)
    {
        var result = await _vmware.ListProcessesInGuestAsync(ct);
        if (!result.Success)
            return $"Failed to list processes: {result.Stderr}";

        return result.Stdout;
    }

    /// <summary>
    /// Kill a process in the guest VM by PID.
    /// </summary>
    public async Task<string> KillProcessAsync(uint pid, CancellationToken ct = default)
    {
        var result = await _vmware.KillProcessInGuestAsync(pid, ct);
        if (!result.Success)
            return $"Failed to kill process {pid}: {result.Stderr}";

        return $"Process {pid} killed successfully.";
    }

    private static void SafeDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
