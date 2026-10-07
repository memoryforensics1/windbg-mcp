namespace WinDbgMCP.Server.KernelDebug;

/// <summary>
/// Thrown to a caller whose engine work item timed out before it could start
/// because another item is still running on the single engine thread (dbgeng
/// cannot cancel a running command). The message tells the model what is
/// running and that waiting, not retrying, is the way through.
/// </summary>
public sealed class EngineBusyException : InvalidOperationException
{
    public EngineBusyException(string busyWith, long runningSeconds)
        : base($"The debugger engine is busy: {busyWith} has been running for {runningSeconds} s and must finish " +
               "before any other kernel command can run (a running engine command cannot be cancelled; symbol " +
               "loading such as !analyze -v can take minutes the first time). Nothing was executed. Wait, poll " +
               "get_system_state (it shows ENGINE BUSY until the command completes), then retry.")
    {
    }
}
