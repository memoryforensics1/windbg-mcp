using WinDbgMCP.Server.Tools;

namespace WinDbgMCP.Tests;

/// <summary>
/// guest_run_command detaches the kernel debugger before a restart/shutdown so a
/// graceful reboot does not wedge the engine. A missed match risks that wedge; a
/// spurious match only costs a needless detach. These lock in both directions.
/// </summary>
public class RestartCommandTests
{
    [Theory]
    // Plain and flagged shutdown, any flag order / dash form
    [InlineData("shutdown /r /t 0")]
    [InlineData("shutdown /s /t 60")]
    [InlineData("shutdown /g")]
    [InlineData("shutdown -r -t 0")]
    [InlineData("shutdown /f /r /t 0")]
    [InlineData("shutdown")]
    // Full path and quoting
    [InlineData(@"C:\Windows\System32\shutdown.exe /r /t 0")]
    [InlineData("\"C:\\Windows\\System32\\shutdown.exe\" /r")]
    [InlineData("(shutdown /r)")]
    // PowerShell cmdlets, including through powershell -Command
    [InlineData("Restart-Computer")]
    [InlineData("powershell -Command \"Restart-Computer -Force\"")]
    [InlineData("powershell -c 'Stop-Computer'")]
    // wmic and WMI
    [InlineData("wmic os where primary=true call reboot")]
    [InlineData("powershell (Get-WmiObject Win32_OperatingSystem).Win32Shutdown(6)")]
    // Chained after another command
    [InlineData("echo done && shutdown /r /t 0")]
    public void Matches_RestartCommands(string command)
        => Assert.True(GuestTools.IsRestartCommand(command), command);

    [Theory]
    // shutdown with only no-op switches must NOT trigger a detach
    [InlineData("shutdown /a")]
    [InlineData("shutdown /l")]
    [InlineData("shutdown /h")]
    [InlineData("shutdown /i")]
    [InlineData("shutdown /?")]
    // Unrelated commands
    [InlineData("hostname")]
    [InlineData("ipconfig /all")]
    [InlineData("dir C:\\Windows")]
    [InlineData("sc query MyDriver")]
    // The word inside another token must not match
    [InlineData("type shutdown_log.txt")]
    [InlineData("echo rebooting")]
    public void DoesNotMatch_SafeCommands(string command)
        => Assert.False(GuestTools.IsRestartCommand(command), command);
}
