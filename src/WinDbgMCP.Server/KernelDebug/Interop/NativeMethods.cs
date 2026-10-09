using System.Runtime.InteropServices;
using ClrDebug;

namespace WinDbgMCP.Server.KernelDebug.Interop;

/// <summary>
/// P/Invoke declarations for loading dbgeng.dll and creating the debug client.
/// </summary>
public static class NativeMethods
{
    private const string Kernel32 = "kernel32.dll";

    [DllImport(Kernel32, SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "LoadLibraryW")]
    public static extern IntPtr LoadLibrary(string lpLibFileName);

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetDllDirectory(string lpPathName);

    /// <summary>True when an instance of the named pipe can take a client within the timeout.</summary>
    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "WaitNamedPipeW")]
    public static extern bool WaitNamedPipe(string lpNamedPipeName, int nTimeOut);
}

/// <summary>
/// Delegate matching the DebugCreate export from dbgeng.dll.
/// </summary>
public delegate HRESULT DebugCreateDelegate(
    [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
    out IntPtr ppDebugObject);
