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

    [Theory]
    [InlineData("Last event: Access violation - code c0000005 (!!! second chance !!!)\n  debugger time: ...",
                "Access violation - code c0000005 (!!! second chance !!!)")]
    [InlineData("Last event: Break instruction exception - code 80000003 (first chance)",
                "Break instruction exception - code 80000003 (first chance)")]
    [InlineData("  \n\nLast event: Bugcheck 1E", "Bugcheck 1E")]
    [InlineData("some other line\nLast event: X", "some other line")]
    public void FirstLastEventLine_StripsPrefix(string output, string expected)
        => Assert.Equal(expected, DbgEngManager.FirstLastEventLine(output));

    [Fact]
    public void FirstLastEventLine_Empty_IsNull()
        => Assert.Null(DbgEngManager.FirstLastEventLine("   \n  \n"));

    [Fact]
    public void BugcheckOutput_CarriesArguments()
    {
        var result = DbgEngManager.ParseBugcheckOutput(
            "Bugcheck code 0000001E\nArguments ffffffff`c0000005 00000000`00000000 00000000`00000008 00000000`00000000\n");
        Assert.NotNull(result);
        Assert.True(result!.Value.IsBugcheck);
        Assert.Equal("0x0000001E", result.Value.BugcheckCode);
        Assert.Equal("ffffffff`c0000005 00000000`00000000 00000000`00000008 00000000`00000000", result.Value.Arguments);
    }

    [Fact]
    public void BugcheckOutput_WithoutArgumentsLine_HasNullArguments()
    {
        var result = DbgEngManager.ParseBugcheckOutput("Bugcheck code 000000D1\n");
        Assert.NotNull(result);
        Assert.True(result!.Value.IsBugcheck);
        Assert.Null(result.Value.Arguments);
    }
}
