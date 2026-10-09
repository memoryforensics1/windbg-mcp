namespace WinDbgMCP.Server.Guest.Models;

/// <summary>
/// Result of a command executed inside the guest VM.
/// </summary>
public sealed class GuestCommandResult
{
    public bool Success { get; }
    public int ExitCode { get; }
    public string Stdout { get; }
    public string Stderr { get; }
    public string? ErrorMessage { get; }

    /// <summary>Set when the command ran but part of its result is unknown.</summary>
    public string? Warning { get; }

    private GuestCommandResult(bool success, int exitCode, string stdout, string stderr, string? error = null, string? warning = null)
    {
        Success = success;
        ExitCode = exitCode;
        Stdout = stdout;
        Stderr = stderr;
        ErrorMessage = error;
        Warning = warning;
    }

    public static GuestCommandResult Ok(int exitCode, string stdout, string stderr, string? warning = null)
        => new(true, exitCode, stdout, stderr, warning: warning);

    public static GuestCommandResult Failed(string error)
        => new(false, -1, "", "", error);

    public override string ToString()
    {
        if (!Success)
            return $"FAILED: {ErrorMessage}";

        var result = $"Exit code: {ExitCode}";
        if (!string.IsNullOrWhiteSpace(Stdout))
            result += $"\n--- stdout ---\n{Stdout}";
        if (!string.IsNullOrWhiteSpace(Stderr))
            result += $"\n--- stderr ---\n{Stderr}";
        if (!string.IsNullOrWhiteSpace(Warning))
            result += $"\n--- WARNING ---\n{Warning}";
        return result;
    }
}
