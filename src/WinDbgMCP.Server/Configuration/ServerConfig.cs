namespace WinDbgMCP.Server.Configuration;

/// <summary>
/// Root configuration model for the WinDbgMCP server.
/// Loaded from appsettings.json.
/// </summary>
public sealed class ServerConfig
{
    public VmConfig Vm { get; set; } = new();
    public KernelDebugConfig KernelDebug { get; set; } = new();
    public GuestConfig Guest { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();
    public TimeoutConfig Timeouts { get; set; } = new();
}

public sealed class VmConfig
{
    public string VmxPath { get; set; } = string.Empty;
    public string VmrunPath { get; set; } = @"C:\Program Files (x86)\VMware\VMware Workstation\vmrun.exe";

    /// <summary>
    /// vmrun host type (-T flag): "ws" (local Workstation, default), "esx" (ESXi),
    /// "vc" (vCenter), "ws-shared" (shared Workstation), "fusion", "player".
    /// </summary>
    public string HostType { get; set; } = "ws";

    /// <summary>
    /// Remote hypervisor URL (-h flag), e.g. "https://esxi-host/sdk".
    /// Leave empty for local Workstation. For esx/vc, VmxPath must be a
    /// datastore path like "[datastore1] win10/win10.vmx".
    /// </summary>
    public string HostUrl { get; set; } = string.Empty;

    /// <summary>
    /// Hypervisor login (-u / -p flags). Required when HostUrl is set.
    /// Note: vmrun only accepts these on the command line, so the password
    /// is visible in the local process list while a vmrun command runs.
    /// </summary>
    public string HostUsername { get; set; } = string.Empty;
    public string HostPassword { get; set; } = string.Empty;
    /// <summary>
    /// VM encryption password (for encrypted VMs). Used as -vp flag in vmrun.
    /// </summary>
    public string VmPassword { get; set; } = string.Empty;
    public string GuestUsername { get; set; } = string.Empty;
    public string GuestPassword { get; set; } = string.Empty;
    public bool Headless { get; set; } = true;
}

public sealed class KernelDebugConfig
{
    /// <summary>
    /// "kdnet" or "serial"
    /// </summary>
    public string Transport { get; set; } = "kdnet";
    public KdnetConfig Kdnet { get; set; } = new();
    public SerialConfig Serial { get; set; } = new();
    public string SymbolPath { get; set; } = @"srv*C:\Symbols*https://msdl.microsoft.com/download/symbols";
}

public sealed class KdnetConfig
{
    public int Port { get; set; } = 50000;
    public string Key { get; set; } = string.Empty;
}

public sealed class SerialConfig
{
    public string PipeName { get; set; } = @"\\.\pipe\com_1";
}

public sealed class GuestConfig
{
    public int FridaPort { get; set; } = 27042;
    public int DbgsrvPort { get; set; } = 5064;
}

public sealed class SecurityConfig
{
    /// <summary>
    /// Snapshot deletion is DISABLED by default. Must be explicitly enabled.
    /// </summary>
    public bool SnapshotDeleteEnabled { get; set; } = false;

    /// <summary>
    /// Snapshots in this list cannot be deleted or overwritten.
    /// Acts as a safety net to protect known-good states.
    /// </summary>
    public List<string> ProtectedSnapshots { get; set; } = new();

    /// <summary>
    /// If true, refuse to delete/restore-over when only one snapshot remains.
    /// Prevents accidentally losing the last recovery point. Default: true.
    /// </summary>
    public bool PreventLastSnapshotDeletion { get; set; } = true;

    /// <summary>
    /// Default snapshot name for recovery/restore operations.
    /// This is the "known-good" snapshot that the LLM should revert to when needed.
    /// </summary>
    public string DefaultSnapshotName { get; set; } = string.Empty;
}

public sealed class TimeoutConfig
{
    // VM operations (seconds)
    public int VmStartSeconds { get; set; } = 60;
    public int VmStopSeconds { get; set; } = 30;
    public int VmPauseResumeSeconds { get; set; } = 10;
    public int VmSnapshotCreateSeconds { get; set; } = 120;
    public int VmSnapshotRestoreSeconds { get; set; } = 60;
    public int VmScreenshotSeconds { get; set; } = 10;
    public int VmToolsCheckSeconds { get; set; } = 5;
    public int VmGetIpSeconds { get; set; } = 10;

    // Kernel debug operations (seconds)
    public int KdConnectSeconds { get; set; } = 30;
    public int KdInitialBreakSeconds { get; set; } = 15;
    public int KdBreakSeconds { get; set; } = 10;
    public int KdStepSeconds { get; set; } = 10;
    public int KdCommandExecuteSeconds { get; set; } = 30;
    public int KdMemoryReadSeconds { get; set; } = 10;
    public int KdMemoryWriteSeconds { get; set; } = 10;
    public int KdWaitForBreakpointSeconds { get; set; } = 10;

    // Guest operations (seconds)
    public int GuestCommandSeconds { get; set; } = 60;
    public int GuestFileTransferSeconds { get; set; } = 120;
    public int GuestListProcessesSeconds { get; set; } = 15;
    public int GuestKillProcessSeconds { get; set; } = 10;

    // User-mode debug (seconds)
    public int FridaAttachSeconds { get; set; } = 15;
    public int FridaScriptSeconds { get; set; } = 30;
    public int DbgsrvConnectSeconds { get; set; } = 15;
    public int TtdRecordMinutes { get; set; } = 5;
}
