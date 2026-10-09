using System.Diagnostics;
using System.Reflection;
using WinDbgMCP.Server.Configuration;

namespace WinDbgMCP.Server.Diagnostics;

/// <summary>
/// Facts about the running server process that explain "which build, which
/// settings, where is the log": the questions that come first whenever a run
/// behaves differently from what the code in the checkout says.
/// </summary>
public static class ServerInfo
{
    public static DateTime StartedAt { get; } = DateTime.Now;
    public static int ProcessId { get; } = Environment.ProcessId;

    /// <summary>Informational version; the SDK appends "+&lt;git commit&gt;" when built in a git checkout.</summary>
    public static string Version { get; } =
        typeof(ServerInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    /// <summary>The commit the binary was built from (short form), or null.</summary>
    public static string? Commit
    {
        get
        {
            var plus = Version.IndexOf('+');
            if (plus < 0 || plus == Version.Length - 1) return null;
            var sha = Version[(plus + 1)..];
            return sha.Length > 7 ? sha[..7] : sha;
        }
    }

    public static string AssemblyPath { get; } = typeof(ServerInfo).Assembly.Location;

    public static DateTime? BuiltAt
    {
        get
        {
            try { return File.GetLastWriteTime(AssemblyPath); }
            catch { return null; }
        }
    }

    /// <summary>The settings file the server bound its configuration from.</summary>
    public static string? SettingsPath { get; set; }

    /// <summary>The log file this process writes, or null when file logging is off/failed.</summary>
    public static string? LogFilePath { get; set; }

    public static string BuildLine() =>
        $"WinDbgMCP {(Commit != null ? "commit " + Commit : Version)}" +
        (BuiltAt is { } b ? $", built {b:yyyy-MM-dd HH:mm}" : "") +
        $", running since {StartedAt:yyyy-MM-dd HH:mm:ss} (pid {ProcessId})";

    /// <summary>Configured transport and where it connects, without the KDNET key.</summary>
    public static string TransportLine(ServerConfig config)
    {
        var kd = config.KernelDebug;
        return kd.Transport.Equals("serial", StringComparison.OrdinalIgnoreCase)
            ? $"serial via named pipe {kd.Serial.PipeName}"
            : kd.Transport.Equals("kdnet", StringComparison.OrdinalIgnoreCase)
                ? $"kdnet on UDP port {kd.Kdnet.Port}"
                : $"'{kd.Transport}' (invalid: use kdnet or serial)";
    }
}
