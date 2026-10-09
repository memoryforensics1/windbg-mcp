namespace WinDbgMCP.Server.KernelDebug;

/// <summary>
/// kd_connect ran out of budget while the attempt is still waiting for the kernel.
/// The attempt is not cancelled (it cannot be) and finishes on its own.
/// </summary>
public sealed class KdConnectPendingException(string message) : Exception(message);
