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
            var copyBat = await _vmware.CopyFileToGuestAsync(hostBat, guestBat, ct);
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
                var copyOut = await _vmware.CopyFileFromGuestAsync(guestStdout, hostStdout, ct);
                if (copyOut.Success && File.Exists(hostStdout))
                    stdout = (await File.ReadAllTextAsync(hostStdout, ct)).Trim();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to retrieve stdout from guest");
            }

            try
            {
                var copyErr = await _vmware.CopyFileFromGuestAsync(guestStderr, hostStderr, ct);
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

    private const long LargeFileThreshold = 50 * 1024 * 1024; // 50 MB

    /// <summary>
    /// Copy a file from the host to the guest VM.
    /// Small files use vmrun copyFile; large files (>=50MB) use VMware shared folders
    /// (HGFS) which is the same fast mechanism as drag-and-drop.
    /// </summary>
    public async Task<string> CopyFileToGuestAsync(
        string hostPath, string guestPath, CancellationToken ct = default)
    {
        hostPath = Path.GetFullPath(hostPath);
        if (!File.Exists(hostPath))
            return $"Host file not found: {hostPath}";

        var fileSize = new FileInfo(hostPath).Length;

        if (fileSize >= LargeFileThreshold)
        {
            var shareResult = await CopyToGuestViaShareAsync(hostPath, guestPath, fileSize, ct);
            if (shareResult != null)
                return shareResult;
            _logger.LogWarning("Shared folder transfer failed, falling back to vmrun copyFile");
        }

        var result = await _vmware.CopyFileToGuestAsync(hostPath, guestPath, ct);
        if (!result.Success)
            return $"File transfer failed: {result.Stderr}";

        return $"Copied {hostPath} -> guest:{guestPath} ({fileSize:N0} bytes)";
    }

    /// <summary>
    /// Copy a file from the guest VM to the host.
    /// Asks the guest for the file size first: large files (>=50MB) go through a
    /// VMware shared folder straight away (vmrun copyFile runs at ~2 MB/s and its
    /// timeout would kill the copy and leave a partial file); small files use vmrun
    /// copyFile, with the shared folder as the fallback for any failure including a
    /// timeout. A failed copy never leaves a partial file on the host.
    /// </summary>
    public async Task<string> CopyFileFromGuestAsync(
        string guestPath, string hostPath, CancellationToken ct = default)
    {
        hostPath = Path.GetFullPath(hostPath);
        var hostDir = Path.GetDirectoryName(hostPath);
        if (!string.IsNullOrEmpty(hostDir) && !Directory.Exists(hostDir))
            Directory.CreateDirectory(hostDir);

        var guestSize = await GetGuestFileSizeAsync(guestPath, ct);
        if (guestSize == null)
            return $"Guest file not found: {guestPath}";

        if (guestSize >= LargeFileThreshold)
        {
            var shareFirst = await CopyFromGuestViaShareAsync(guestPath, hostPath, ct);
            if (shareFirst != null)
                return shareFirst;
            _logger.LogWarning("Shared folder transfer failed, falling back to vmrun copyFile");
        }

        string failure;
        try
        {
            var result = await _vmware.CopyFileFromGuestAsync(guestPath, hostPath, ct);
            if (result.Success && File.Exists(hostPath) && new FileInfo(hostPath).Length == guestSize)
                return $"Copied guest:{guestPath} -> {hostPath} ({guestSize:N0} bytes)";
            failure = result.Success ? "incomplete copy" : result.Stderr.Trim();
        }
        catch (TimeoutException)
        {
            failure = "vmrun copyFile timed out";
        }
        DeletePartialFile(hostPath);

        _logger.LogWarning("vmrun copyFile failed ({Reason}), trying shared folder", failure);
        var shareResult = await CopyFromGuestViaShareAsync(guestPath, hostPath, ct);
        if (shareResult != null)
            return shareResult;

        return $"File transfer failed: {failure}";
    }

    /// <summary>Size of a guest file in bytes, or null if it does not exist.</summary>
    private async Task<long?> GetGuestFileSizeAsync(string guestPath, CancellationToken ct)
    {
        // Runs inside a .bat (RunCommandAsync), hence %%I rather than %I.
        var sizeResult = await RunCommandAsync(
            $"for %%I in (\"{guestPath}\") do @echo %%~zI", timeoutSeconds: 30, ct: ct);
        if (!sizeResult.Success || sizeResult.ExitCode != 0)
            return null;
        var line = sizeResult.Stdout.Trim().Split('\n').LastOrDefault()?.Trim();
        return long.TryParse(line, out var size) ? size : null;
    }

    private void DeletePartialFile(string hostPath)
    {
        try { if (File.Exists(hostPath)) File.Delete(hostPath); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete partial file {Path}", hostPath); }
    }

    /// <summary>
    /// Transfer host→guest via a temporary VMware shared folder.
    /// Stages through an isolated temp directory so only the target file is exposed to the guest.
    /// Returns the success message, or null if shared folders aren't available.
    /// </summary>
    private async Task<string?> CopyToGuestViaShareAsync(
        string hostPath, string guestPath, long fileSize, CancellationToken ct)
    {
        var guid = Guid.NewGuid().ToString("N")[..8];
        var shareName = $"mcp_{guid}";
        var stagingDir = Path.Combine(Path.GetTempPath(), $"mcp_xfer_{guid}");
        var fileName = Path.GetFileName(hostPath);

        try
        {
            Directory.CreateDirectory(stagingDir);
            var stagedFile = Path.Combine(stagingDir, fileName);
            File.Copy(hostPath, stagedFile, overwrite: true);

            var enableResult = await _vmware.EnableSharedFoldersAsync(ct);
            if (!enableResult.Success)
                return null;

            var addResult = await _vmware.AddSharedFolderAsync(shareName, stagingDir, ct);
            if (!addResult.Success)
                return null;

            try
            {
                var guestDir = Path.GetDirectoryName(guestPath);
                if (!string.IsNullOrEmpty(guestDir))
                    await _vmware.CreateDirectoryInGuestAsync(guestDir, ct);

                var uncPath = $@"\\vmware-host\Shared Folders\{shareName}\{fileName}";
                var copyResult = await RunCommandAsync(
                    $"copy /y \"{uncPath}\" \"{guestPath}\"", timeoutSeconds: 600, ct: ct);

                if (!copyResult.Success || copyResult.ExitCode != 0)
                {
                    _logger.LogWarning("Shared folder copy failed: {Err}",
                        copyResult.Success ? copyResult.Stderr : copyResult.ErrorMessage);
                    return null;
                }

                return $"Copied {hostPath} -> guest:{guestPath} ({fileSize:N0} bytes) [via shared folder]";
            }
            finally
            {
                try { await _vmware.RemoveSharedFolderAsync(shareName, ct); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to remove shared folder {Name}", shareName); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Shared folder transfer failed");
            return null;
        }
        finally
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); }
            catch { }
        }
    }

    /// <summary>
    /// Transfer guest→host via a temporary VMware shared folder.
    /// Stages through an isolated temp directory so the guest can only write to that directory.
    /// Returns the success message, or null if shared folders aren't available.
    /// </summary>
    private async Task<string?> CopyFromGuestViaShareAsync(
        string guestPath, string hostPath, CancellationToken ct)
    {
        var guid = Guid.NewGuid().ToString("N")[..8];
        var shareName = $"mcp_{guid}";
        var stagingDir = Path.Combine(Path.GetTempPath(), $"mcp_xfer_{guid}");
        var fileName = Path.GetFileName(hostPath);

        try
        {
            Directory.CreateDirectory(stagingDir);

            var enableResult = await _vmware.EnableSharedFoldersAsync(ct);
            if (!enableResult.Success)
                return null;

            var addResult = await _vmware.AddSharedFolderAsync(shareName, stagingDir, ct);
            if (!addResult.Success)
                return null;

            try
            {
                var uncPath = $@"\\vmware-host\Shared Folders\{shareName}\{fileName}";
                var copyResult = await RunCommandAsync(
                    $"copy /y \"{guestPath}\" \"{uncPath}\"", timeoutSeconds: 600, ct: ct);

                if (!copyResult.Success || copyResult.ExitCode != 0)
                {
                    _logger.LogWarning("Shared folder copy failed: {Err}",
                        copyResult.Success ? copyResult.Stderr : copyResult.ErrorMessage);
                    return null;
                }

                var stagedFile = Path.Combine(stagingDir, fileName);
                if (!File.Exists(stagedFile))
                    return null;

                var hostDir = Path.GetDirectoryName(hostPath);
                if (!string.IsNullOrEmpty(hostDir) && !Directory.Exists(hostDir))
                    Directory.CreateDirectory(hostDir);

                File.Move(stagedFile, hostPath, overwrite: true);

                var fileSize = new FileInfo(hostPath).Length;
                return $"Copied guest:{guestPath} -> {hostPath} ({fileSize:N0} bytes) [via shared folder]";
            }
            finally
            {
                try { await _vmware.RemoveSharedFolderAsync(shareName, ct); }
                catch (Exception ex) { _logger.LogDebug(ex, "Failed to remove shared folder {Name}", shareName); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Shared folder transfer failed");
            return null;
        }
        finally
        {
            try { if (Directory.Exists(stagingDir)) Directory.Delete(stagingDir, recursive: true); }
            catch { }
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
