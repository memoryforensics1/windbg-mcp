using Microsoft.Extensions.Logging.Abstractions;
using WinDbgMCP.Server.KernelDebug;

namespace WinDbgMCP.Tests;

/// <summary>
/// The engine thread runs one item at a time and cannot cancel a running one.
/// A caller whose item times out while another is still running must be told
/// what is running (EngineBusyException), not given a bare timeout.
/// </summary>
public class DbgEngThreadTests
{
    [Fact]
    public async Task ItemQueuedBehindLongRunner_GetsEngineBusy()
    {
        using var thread = new DbgEngThread(NullLogger.Instance);
        var gate = new ManualResetEventSlim(false);

        var longRunner = thread.ExecuteAsync(() => { gate.Wait(); return 1; },
            TimeSpan.FromSeconds(30), "kd_execute !analyze -v");

        var ex = await Assert.ThrowsAsync<EngineBusyException>(() =>
            thread.ExecuteAsync(() => 2, TimeSpan.FromMilliseconds(300), "kd_continue"));
        Assert.Contains("!analyze -v", ex.Message);
        Assert.Contains("Nothing was executed", ex.Message);
        Assert.Contains("!analyze -v", thread.BusyDescription);

        gate.Set();
        Assert.Equal(1, await longRunner);
        await Task.Delay(50);
        Assert.Null(thread.BusyDescription);
    }

    [Fact]
    public async Task OwnItemOutlivingTimeout_IsCancelled_AndReportedBusy()
    {
        using var thread = new DbgEngThread(NullLogger.Instance);
        var gate = new ManualResetEventSlim(false);

        var slow = thread.ExecuteAsync(() => { gate.Wait(); return 1; },
            TimeSpan.FromMilliseconds(200), "kd_execute slow");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => slow);
        Assert.Contains("slow", thread.BusyDescription);
        gate.Set();
    }

    [Fact]
    public async Task IdleThread_RunsItemsInOrder_NoBusy()
    {
        using var thread = new DbgEngThread(NullLogger.Instance);
        Assert.Null(thread.BusyDescription);
        Assert.Equal(3, await thread.ExecuteAsync(() => 3, TimeSpan.FromSeconds(5)));
        Assert.Null(thread.BusyDescription);
    }
}
