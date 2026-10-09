namespace WinDbgMCP.Server.KernelDebug.Models;

/// <summary>
/// Outcome of <see cref="DbgEngManager.DisconnectAsync"/>. <see cref="Detached"/>
/// is false when the engine still owns a live session (the engine thread could
/// not be reached), in which case the caller must NOT report the debugger as
/// disconnected or do anything that assumes the target is free.
/// </summary>
public readonly record struct DetachResult(bool Detached, string Message);
