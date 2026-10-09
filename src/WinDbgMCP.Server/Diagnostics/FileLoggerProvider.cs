using System.Text;
using Microsoft.Extensions.Logging;

namespace WinDbgMCP.Server.Diagnostics;

/// <summary>
/// Writes every log line to a file as well as stderr. MCP clients differ in what
/// they do with a server's stderr (some keep it, some drop it, some talk to the
/// server through files), so the log on disk is the one artifact that always exists
/// after a bad run. Configured secrets are replaced before anything is written.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int KeepFiles = 20;

    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private readonly string[] _secrets;
    private readonly LogLevel _minLevel;

    public string FilePath { get; }

    private FileLoggerProvider(string path, StreamWriter writer, IEnumerable<string?> secrets, LogLevel minLevel)
    {
        FilePath = path;
        _writer = writer;
        _minLevel = minLevel;
        // Longest first, so a secret that contains another is replaced whole.
        _secrets = secrets.Where(s => !string.IsNullOrEmpty(s) && s!.Length >= 4)
                          .Select(s => s!).Distinct().OrderByDescending(s => s.Length).ToArray();
    }

    /// <summary>
    /// Opens a new log file in <paramref name="directory"/> and prunes old ones.
    /// Returns null (logging to stderr only) when the directory is not writable.
    /// </summary>
    public static FileLoggerProvider? TryCreate(string directory, IEnumerable<string?> secrets, LogLevel minLevel)
    {
        try
        {
            Directory.CreateDirectory(directory);
            Prune(directory);
            var path = Path.Combine(directory,
                $"windbg-mcp-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            return new FileLoggerProvider(path, writer, secrets, minLevel);
        }
        catch
        {
            return null;
        }
    }

    private static void Prune(string directory)
    {
        try
        {
            var old = new DirectoryInfo(directory).GetFiles("windbg-mcp-*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepFiles - 1);
            foreach (var f in old)
            {
                try { f.Delete(); } catch { }
            }
        }
        catch { }
    }

    internal string Redact(string text)
    {
        foreach (var secret in _secrets)
            text = text.Replace(secret, "<redacted>", StringComparison.Ordinal);
        return text;
    }

    internal void Write(LogLevel level, string category, string message, Exception? ex)
    {
        if (level < _minLevel) return;
        var shortCategory = category[(category.LastIndexOf('.') + 1)..];
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {LevelTag(level)} {shortCategory}: {message}";
        if (ex != null) line += Environment.NewLine + ex;
        line = Redact(line);
        lock (_lock)
        {
            try { _writer.WriteLine(line); } catch { }
        }
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "FAIL",
        LogLevel.Critical => "CRIT",
        _ => "    ",
    };

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_lock)
        {
            try { _writer.Dispose(); } catch { }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
