using WinDbgMCP.Server.KernelDebug;

namespace WinDbgMCP.Tests;

public class BugcheckParsingTests
{
    [Fact]
    public void BugcheckOutput_NonzeroCode_IsBugcheck()
    {
        var result = DbgEngManager.ParseBugcheckOutput(
            "Bugcheck code 000000D1\nArguments 00000000`00000000 00000000`00000002 00000000`00000000 fffff800`12345678\n");
        Assert.NotNull(result);
        Assert.True(result!.Value.IsBugcheck);
        Assert.Equal("0x000000D1", result.Value.BugcheckCode);
    }

    [Fact]
    public void BugcheckOutput_ZeroCode_IsHealthy()
    {
        var result = DbgEngManager.ParseBugcheckOutput(
            "Bugcheck code 00000000\nArguments 00000000`00000000 00000000`00000000 00000000`00000000 00000000`00000000\n");
        Assert.NotNull(result);
        Assert.False(result!.Value.IsBugcheck);
        Assert.Null(result.Value.BugcheckCode);
    }

    [Fact]
    public void BugcheckOutput_HexPrefixedCode_IsParsed()
    {
        var result = DbgEngManager.ParseBugcheckOutput("Bugcheck code 0x7E\n");
        Assert.NotNull(result);
        Assert.True(result!.Value.IsBugcheck);
        Assert.Equal("0x0000007E", result.Value.BugcheckCode);
    }

    [Fact]
    public void BugcheckOutput_Unrecognised_ReturnsNull()
    {
        Assert.Null(DbgEngManager.ParseBugcheckOutput("Unable to read KiBugCheckData\n"));
        Assert.Null(DbgEngManager.ParseBugcheckOutput(""));
    }

    [Fact]
    public void LastEventFallback_BreakInstruction_IsNotBugcheck()
    {
        var result = DbgEngManager.ParseLastEventFallback(
            "Last event: Break instruction exception - code 80000003 (first chance)\n  debugger time: Mon Oct  6 12:00:00.000 2026\n");
        Assert.False(result.IsBugcheck);
    }

    [Fact]
    public void LastEventFallback_DoesNotMatchHexLikeWords()
    {
        Assert.False(DbgEngManager.ParseLastEventFallback("Last event: Bugcheck data unavailable").IsBugcheck);
        Assert.False(DbgEngManager.ParseLastEventFallback("Last event: Bugcheck code pending").IsBugcheck);
    }

    [Fact]
    public void LastEventFallback_DumpStyleBugcheck_IsBugcheck()
    {
        var result = DbgEngManager.ParseLastEventFallback("Last event: Bugcheck 7E (ffffffffc0000005, ...)");
        Assert.True(result.IsBugcheck);
        Assert.Equal("0x0000007E", result.BugcheckCode);
    }
}
