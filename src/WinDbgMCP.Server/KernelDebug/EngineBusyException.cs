namespace WinDbgMCP.Server.KernelDebug;

/// <summary>
/// Thrown to a caller whose engine work item timed out before it could start
/// because another item is still running on the single engine thread (dbgeng
/// cannot cancel a running command). The message tells the model what is
/// running, why that takes time, and that waiting, not retrying, is the way through.
/// </summary>
public sealed class EngineBusyException : InvalidOperationException
{
    public EngineBusyException(string busyWith, long runningSeconds)
        : base($"The debugger engine is busy: {busyWith} has been running for {runningSeconds} s and must finish " +
               "before any other kernel command can run. " + Explain(busyWith) +
               " Nothing was executed. Wait, poll get_system_state (it shows ENGINE BUSY until the engine is free), then retry.")
    {
    }

    /// <summary>Why the running item takes long, by what it is.</summary>
    public static string Explain(string busyWith) =>
        busyWith.StartsWith("kd_execute", StringComparison.Ordinal)
            ? "A running engine command cannot be cancelled; symbol loading such as !analyze -v can take minutes the first time."
            : "It is waiting for the target to answer a break-in or to come back: the kernel is rebooting, booting " +
              "(module loads in bulk), writing a crash dump, or the VM is paused. It frees itself when the target answers.";
}
