using System.Text;
using ClrDebug;
using Microsoft.Extensions.Logging;
using WinDbgMCP.Server.Diagnostics;
using WinDbgMCP.Server.Guest;
using WinDbgMCP.Server.KernelDebug;
using WinDbgMCP.Server.Vmware;

namespace WinDbgMCP.Tests;

/// <summary>
/// The pieces kd_connect and the logs rely on to tell "connected", "no answer"
/// and "misconfigured" apart, and to keep secrets out of log files.
/// </summary>
public class ConnectDiagnosticsTests
{
    private const string EngineConnected =
        "Microsoft (R) Windows Debugger Version 10.0.26100.6901 AMD64\n" +
        "Waiting to reconnect...\n" +
        "Connected to Windows 10 26100 x64 target at (Fri Oct  9 12:07:41.700 2026 (UTC + 3:00)), ptr64 TRUE\n" +
        "Kernel Debugger connection established.  (Initial Breakpoint requested)\n";

    [Fact]
    public void ConnectionEstablished_RecognisesEngineBanner()
    {
        Assert.True(DbgEngManager.ConnectionEstablished(EngineConnected));
        Assert.False(DbgEngManager.ConnectionEstablished("Using NET for debugging\nWaiting to reconnect...\n"));
    }

    [Fact]
    public void TargetBanner_ReturnsTheConnectedLine()
    {
        Assert.StartsWith("Connected to Windows 10 26100 x64 target", DbgEngManager.TargetBanner(EngineConnected));
        Assert.Equal("", DbgEngManager.TargetBanner("Waiting to reconnect...\n"));
    }

    [Fact]
    public void LastEngineLine_SkipsDecorationAndGalleryNoise()
    {
        var sb = new StringBuilder(
            "************* Preparing the environment for Debugger Extensions Gallery repositories **************\n" +
            "Opened WinSock 2.0\nWaiting to reconnect...\n" +
            ">>>>>>>>>>>>> Waiting for Debugger Extensions Gallery to Initialize completed\n\n");
        Assert.Equal("Waiting to reconnect...", DbgEngManager.LastEngineLine(sb));
        Assert.Null(DbgEngManager.LastEngineLine(new StringBuilder("\n\n")));
    }

    [Theory]
    [InlineData(@"com:pipe,port=\\.\pipe\windbg_com1,resets=0,reconnect", @"\\.\pipe\windbg_com1")]
    [InlineData(@"COM:PIPE,PORT=\\.\pipe\com_1", @"\\.\pipe\com_1")]
    [InlineData("net:port=50000,key=1.2.3.4", null)]
    [InlineData("com:port=COM1,baud=115200", null)]
    public void SerialPipeName_OnlyForPipeConnections(string connStr, string? expected)
    {
        Assert.Equal(expected, DbgEngManager.SerialPipeName(connStr));
    }

    [Fact]
    public void DescribeHResult_SpellsOutWin32Errors()
    {
        // 0x800700E7 is what AttachKernel returned for a busy VMware serial pipe.
        var text = DbgEngManager.DescribeHResult(unchecked((HRESULT)(int)0x800700E7));
        Assert.StartsWith("0x800700E7", text);
        Assert.Contains("busy", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PendingMessage_SaysItCompletesOnItsOwnAndHowToTellMisconfiguration()
    {
        var msg = DbgEngManager.PendingMessage(WinDbgMCP.Server.State.KdTransport.Serial, 30);
        Assert.Contains("PENDING", msg);
        Assert.Contains("completes on its own", msg);
        Assert.Contains("restart the MCP server", msg);
        Assert.Contains("bcdedit /dbgsettings", msg);
    }

    [Theory]
    [InlineData("net:port=50000,key=3cyy6s77i6u0h.2p05tab42qguu.3fqeye0rfyhac.dxcgplyny6qg", "net:port=50000,key=<redacted>")]
    [InlineData("net:port=50000,KEY=abc.def,target=1.2.3.4", "net:port=50000,KEY=<redacted>,target=1.2.3.4")]
    [InlineData(@"com:pipe,port=\\.\pipe\x,resets=0", @"com:pipe,port=\\.\pipe\x,resets=0")]
    public void RedactConnectionString_HidesTheKdnetKey(string input, string expected)
    {
        Assert.Equal(expected, DbgEngManager.RedactConnectionString(input));
    }

    [Fact]
    public void RedactSecrets_HidesVmAndGuestPasswords()
    {
        var args = "-vp \"VmSecret!\" -T ws -gu \"user\" -gp \"Guest Pass1\" runProgramInGuest \"x.vmx\" cmd.exe";
        var redacted = VmwareManager.RedactSecrets(args);
        Assert.DoesNotContain("VmSecret!", redacted);
        Assert.DoesNotContain("Guest Pass1", redacted);
        Assert.Contains("-gu \"user\"", redacted);
        Assert.Contains("runProgramInGuest", redacted);
    }

    [Theory]
    [InlineData("Guest program exited with non-zero exit code: 5", "", true, 5)]
    [InlineData("", "Guest program exited with non-zero exit code: 255", true, 255)]
    [InlineData("Error: VMware Tools are not running in the guest", "", false, 0)]
    public void TryParseGuestExitCode_TellsProgramExitFromVmrunFailure(string stdout, string stderr, bool isProgramExit, int code)
    {
        var parsed = GuestExecManager.TryParseGuestExitCode(new ProcessResult(1, stdout, stderr), out var exit);
        Assert.Equal(isProgramExit, parsed);
        Assert.Equal(code, exit);
    }

    [Fact]
    public void FileLogger_ReplacesConfiguredSecretsInEveryLine()
    {
        var dir = Path.Combine(Path.GetTempPath(), "windbgmcp-logtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var provider = FileLoggerProvider.TryCreate(dir, new[] { "Sup3rSecret", "key.abc.def", null, "" }, LogLevel.Trace))
            {
                Assert.NotNull(provider);
                var logger = provider!.CreateLogger("WinDbgMCP.Test");
                logger.LogInformation("vmrun -vp Sup3rSecret and net:port=1,key=key.abc.def");
                logger.LogDebug("plain line");
            }
            var text = File.ReadAllText(Directory.GetFiles(dir, "windbg-mcp-*.log").Single());
            Assert.DoesNotContain("Sup3rSecret", text);
            Assert.DoesNotContain("key.abc.def", text);
            Assert.Contains("<redacted>", text);
            Assert.Contains("plain line", text);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
