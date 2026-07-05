using Microsoft.Extensions.Logging.Abstractions;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.State;

namespace WinDbgMCP.Tests;

/// <summary>
/// Tests the precondition gate for every tool in the system.
/// StateCoordinator is the heart — if preconditions are wrong, tools misbehave.
/// </summary>
public class StateCoordinatorTests : IDisposable
{
    private readonly ServerConfig _config;
    private readonly StateCoordinator _coordinator;

    // Controllable state returned by delegates
    private VmPowerState _vmPower = VmPowerState.Running;
    private bool _toolsRunning = true;
    private DebugExecutionStatus _execStatus = DebugExecutionStatus.NoDebuggee;
    private bool _dbgEngConnected = false;
    private int _pendingEventCount = 0;
    private bool _fridaAttached = false;
    private string? _fridaTarget = null;
    private bool _dbgsrvConnected = false;
    private uint? _dbgsrvPid = null;

    public StateCoordinatorTests()
    {
        _config = new ServerConfig
        {
            Vm = new VmConfig { VmxPath = @"C:\test.vmx" },
            Timeouts = new TimeoutConfig { VmToolsCheckSeconds = 5 }
        };

        var logger = NullLogger<StateCoordinator>.Instance;
        _coordinator = new StateCoordinator(_config, logger);

        // Wire up delegates to return our controllable state
        _coordinator.GetVmPowerStateAsync = () => Task.FromResult(_vmPower);
        _coordinator.AreToolsRunningAsync = _ => Task.FromResult(_toolsRunning);
        _coordinator.IsDbgEngConnected = () => _dbgEngConnected;
        _coordinator.GetDbgEngExecutionStatus = () => _execStatus;
        _coordinator.GetPendingEventCount = () => _pendingEventCount;
        _coordinator.IsFridaAttached = () => _fridaAttached;
        _coordinator.GetFridaTargetName = () => _fridaTarget;
        _coordinator.IsDbgsrvConnected = () => _dbgsrvConnected;
        _coordinator.GetDbgsrvAttachedPid = () => _dbgsrvPid;
    }

    public void Dispose() { }

    // ═══════════════════════════════════════════════════════════════
    //  HELPER: simulate common states
    // ═══════════════════════════════════════════════════════════════

    private void SetVmOff()
    {
        _vmPower = VmPowerState.Off;
        _toolsRunning = false;
    }

    private void SetVmRunning()
    {
        _vmPower = VmPowerState.Running;
        _toolsRunning = true;
    }

    private void SetVmPaused()
    {
        _vmPower = VmPowerState.Paused;
        _toolsRunning = false;
    }

    private void SetKdConnectedBroken()
    {
        _dbgEngConnected = true;
        _execStatus = DebugExecutionStatus.Break;
        _coordinator.SetKdConnected(KdTransport.KDNET);
    }

    private void SetKdConnectedRunning()
    {
        _dbgEngConnected = true;
        _execStatus = DebugExecutionStatus.Go;
        _coordinator.SetKdConnected(KdTransport.KDNET);
        // Override the state that SetKdConnected sets to Break
        _coordinator.State.KdExecStatus = DebugExecutionStatus.Go;
    }

    private void SetFridaAttached()
    {
        _fridaAttached = true;
        _fridaTarget = "notepad.exe";
    }

    private void SetDbgsrvConnected()
    {
        _dbgsrvConnected = true;
        _dbgsrvPid = 1234;
    }

    // ═══════════════════════════════════════════════════════════════
    //  VM TOOL PRECONDITIONS
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task VmStart_RequiresVmOff()
    {
        SetVmOff();
        var result = await _coordinator.ValidatePreconditionsAsync("vm_start");
        Assert.Null(result); // passes
    }

    [Fact]
    public async Task VmStart_FailsWhenVmRunning()
    {
        SetVmRunning();
        var result = await _coordinator.ValidatePreconditionsAsync("vm_start");
        Assert.NotNull(result);
        Assert.False(result!.IsSuccess);
        Assert.Contains("vm_stop", result.Message);
    }

    [Fact]
    public async Task VmStop_RequiresVmNotOff()
    {
        SetVmRunning();
        var result = await _coordinator.ValidatePreconditionsAsync("vm_stop");
        Assert.Null(result);
    }

    [Fact]
    public async Task VmStop_FailsWhenVmOff()
    {
        SetVmOff();
        var result = await _coordinator.ValidatePreconditionsAsync("vm_stop");
        Assert.NotNull(result);
        Assert.False(result!.IsSuccess);
    }

    [Fact]
    public async Task VmStop_WarnsWhenKdAttached()
    {
        SetVmRunning();
        SetKdConnectedBroken();
        var result = await _coordinator.ValidatePreconditionsAsync("vm_stop");
        Assert.NotNull(result);
        Assert.True(result!.IsSuccess); // Warning, not error
        Assert.Contains("WARNING", result.Message);
    }

    [Fact]
    public async Task VmPause_RequiresVmRunning()
    {
        SetVmRunning();
        var result = await _coordinator.ValidatePreconditionsAsync("vm_pause");
        Assert.Null(result);
    }

    [Fact]
    public async Task VmPause_FailsWhenVmOff()
    {
        SetVmOff();
        var result = await _coordinator.ValidatePreconditionsAsync("vm_pause");
        Assert.NotNull(result);
        Assert.False(result!.IsSuccess);
    }

    [Fact]
    public async Task VmResume_RequiresVmPaused()
    {
        SetVmPaused();
        var result = await _coordinator.ValidatePreconditionsAsync("vm_resume");
        Assert.Null(result);
    }

    [Fact]
    public async Task VmResume_FailsWhenVmRunning()
    {
        SetVmRunning();
        var result = await _coordinator.ValidatePreconditionsAsync("vm_resume");
        Assert.NotNull(result);
        Assert.False(result!.IsSuccess);
    }

    [Fact]
    public async Task VmSnapshotRestore_AlwaysAllowed()
    {
        SetVmOff();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("vm_snapshot_restore"));
    }

    [Fact]
    public async Task VmSnapshotList_AlwaysAllowed()
    {
        SetVmOff();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("vm_snapshot_list"));
    }

    [Fact]
    public async Task VmScreenshot_AllowedWhenVmRunning()
    {
        SetVmRunning();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("vm_screenshot"));
    }

    [Fact]
    public async Task VmScreenshot_FailsWhenVmOff()
    {
        SetVmOff();
        var result = await _coordinator.ValidatePreconditionsAsync("vm_screenshot");
        Assert.NotNull(result);
        Assert.False(result!.IsSuccess);
    }

    // ═══════════════════════════════════════════════════════════════
    //  KERNEL DEBUG TOOL PRECONDITIONS
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task KdConnect_RequiresVmRunning_KdNotConnected()
    {
        SetVmRunning();
        var result = await _coordinator.ValidatePreconditionsAsync("kd_connect");
        Assert.Null(result);
    }

    [Fact]
    public async Task KdConnect_FailsWhenVmOff()
    {
        SetVmOff();
        var result = await _coordinator.ValidatePreconditionsAsync("kd_connect");
        Assert.NotNull(result);
        Assert.Contains("vm_start", result!.Message);
    }

    [Fact]
    public async Task KdConnect_FailsWhenAlreadyConnected()
    {
        SetVmRunning();
        SetKdConnectedBroken();
        var result = await _coordinator.ValidatePreconditionsAsync("kd_connect");
        Assert.NotNull(result);
        Assert.Contains("already connected", result!.Message);
    }

    [Fact]
    public async Task KdDisconnect_RequiresKdConnected()
    {
        SetVmRunning();
        SetKdConnectedBroken();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("kd_disconnect"));
    }

    [Fact]
    public async Task KdDisconnect_FailsWhenNotConnected()
    {
        SetVmRunning();
        var result = await _coordinator.ValidatePreconditionsAsync("kd_disconnect");
        Assert.NotNull(result);
        Assert.Contains("not connected", result!.Message);
    }

    [Fact]
    public async Task KdBreak_RequiresKdConnected_TargetRunning()
    {
        SetVmRunning();
        SetKdConnectedRunning();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("kd_break"));
    }

    [Fact]
    public async Task KdBreak_FailsWhenTargetAlreadyBroken()
    {
        SetVmRunning();
        SetKdConnectedBroken();
        var result = await _coordinator.ValidatePreconditionsAsync("kd_break");
        Assert.NotNull(result);
        Assert.Contains("already halted", result!.Message);
    }

    [Fact]
    public async Task KdBreak_FailsOnBsod()
    {
        SetVmRunning();
        SetKdConnectedBroken();
        _coordinator.SetBsodDetected("0x0000007E");
        var result = await _coordinator.ValidatePreconditionsAsync("kd_break");
        Assert.NotNull(result);
        Assert.Contains("BSOD", result!.Message);
    }

    [Fact]
    public async Task KdContinue_RequiresKdConnected_TargetBroken()
    {
        SetVmRunning();
        SetKdConnectedBroken();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("kd_continue"));
    }

    [Fact]
    public async Task KdContinue_FailsWhenTargetRunning()
    {
        SetVmRunning();
        SetKdConnectedRunning();
        var result = await _coordinator.ValidatePreconditionsAsync("kd_continue");
        Assert.NotNull(result);
        Assert.Contains("already running", result!.Message);
    }

    [Fact]
    public async Task KdContinue_FailsOnBsod()
    {
        SetVmRunning();
        SetKdConnectedBroken();
        _coordinator.SetBsodDetected("0x0000007E");
        var result = await _coordinator.ValidatePreconditionsAsync("kd_continue");
        Assert.NotNull(result);
        Assert.Contains("BSOD", result!.Message);
        Assert.Contains("!analyze", result.Message);
    }

    [Fact]
    public async Task KdContinue_FailsWhenWaitPending()
    {
        SetVmRunning();
        SetKdConnectedBroken();
        _coordinator.State.KdWaitPending = true;
        var result = await _coordinator.ValidatePreconditionsAsync("kd_continue");
        Assert.NotNull(result);
        Assert.Contains("pending", result!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KdStep_RequiresKdConnected_TargetBroken()
    {
        SetVmRunning();
        SetKdConnectedBroken();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("kd_step"));
    }

    [Fact]
    public async Task KdExecute_RequiresKdConnected_TargetBroken()
    {
        SetVmRunning();
        SetKdConnectedBroken();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("kd_execute"));
    }

    [Fact]
    public async Task KdExecute_FailsWhenTargetRunning()
    {
        SetVmRunning();
        SetKdConnectedRunning();
        var result = await _coordinator.ValidatePreconditionsAsync("kd_execute");
        Assert.NotNull(result);
        Assert.Contains("kd_break", result!.Message);
    }

    [Fact]
    public async Task KdWaitForEvent_RequiresKdConnected()
    {
        SetVmRunning();
        SetKdConnectedRunning();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("kd_wait_for_event"));
    }

    [Fact]
    public async Task KdWaitForEvent_FailsWhenNotConnected()
    {
        SetVmRunning();
        var result = await _coordinator.ValidatePreconditionsAsync("kd_wait_for_event");
        Assert.NotNull(result);
    }

    // ═══════════════════════════════════════════════════════════════
    //  GUEST TOOL PRECONDITIONS
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("guest_run_command")]
    [InlineData("guest_transfer_to_vm")]
    [InlineData("guest_transfer_from_vm")]
    [InlineData("guest_list_processes")]
    [InlineData("guest_kill_process")]
    public async Task GuestTools_RequireGuestOpsAvailable(string tool)
    {
        SetVmRunning();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync(tool));
    }

    [Theory]
    [InlineData("guest_run_command")]
    [InlineData("guest_transfer_to_vm")]
    [InlineData("guest_transfer_from_vm")]
    [InlineData("guest_list_processes")]
    [InlineData("guest_kill_process")]
    public async Task GuestTools_FailWhenVmOff(string tool)
    {
        SetVmOff();
        var result = await _coordinator.ValidatePreconditionsAsync(tool);
        Assert.NotNull(result);
        Assert.False(result!.IsSuccess);
    }

    [Theory]
    [InlineData("guest_run_command")]
    [InlineData("guest_transfer_to_vm")]
    public async Task GuestTools_FailWhenKdFrozen(string tool)
    {
        SetVmRunning();
        SetKdConnectedBroken();
        var result = await _coordinator.ValidatePreconditionsAsync(tool);
        Assert.NotNull(result);
        Assert.Contains("kd_continue", result!.Message);
    }

    [Theory]
    [InlineData("guest_run_command")]
    [InlineData("guest_transfer_to_vm")]
    public async Task GuestTools_FailWhenVmPaused(string tool)
    {
        SetVmPaused();
        var result = await _coordinator.ValidatePreconditionsAsync(tool);
        Assert.NotNull(result);
        Assert.Contains("vm_resume", result!.Message);
    }

    [Theory]
    [InlineData("guest_run_command")]
    public async Task GuestTools_FailWhenToolsNotResponding(string tool)
    {
        SetVmRunning();
        _toolsRunning = false;
        var result = await _coordinator.ValidatePreconditionsAsync(tool);
        Assert.NotNull(result);
        Assert.Contains("VMware Tools", result!.Message);
    }

    [Theory]
    [InlineData("guest_run_command")]
    public async Task GuestTools_BsodBlocksWithSpecificMessage(string tool)
    {
        SetVmRunning();
        SetKdConnectedBroken();
        _coordinator.SetBsodDetected("0x0000007E");
        var result = await _coordinator.ValidatePreconditionsAsync(tool);
        Assert.NotNull(result);
        Assert.Contains("BSOD", result!.Message);
        Assert.Contains("crashed", result.Message);
    }

    // ═══════════════════════════════════════════════════════════════
    //  USER-MODE DEBUG TOOL PRECONDITIONS
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UmdFridaAttach_RequiresGuestOps()
    {
        SetVmRunning();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("umd_frida_attach"));
    }

    [Fact]
    public async Task UmdFrida_RequiresFridaAttached()
    {
        SetVmRunning();
        SetFridaAttached();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("umd_frida"));
    }

    [Fact]
    public async Task UmdFrida_AllowedWithoutAttach()
    {
        // umd_frida only requires guest ops, not an attached session:
        // action="list" is documented to work without attaching, and each
        // eval/inject spawns a fresh frida process anyway.
        SetVmRunning();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("umd_frida"));
    }

    [Fact]
    public async Task UmdDbgsrvConnect_RequiresGuestOps()
    {
        SetVmRunning();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("umd_dbgsrv_connect"));
    }

    [Fact]
    public async Task UmdDbgsrvExecute_RequiresDbgsrvConnected()
    {
        SetDbgsrvConnected();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("umd_dbgsrv_execute"));
    }

    [Fact]
    public async Task UmdDbgsrvExecute_FailsWhenNotConnected()
    {
        SetVmRunning();
        var result = await _coordinator.ValidatePreconditionsAsync("umd_dbgsrv_execute");
        Assert.NotNull(result);
        Assert.Contains("umd_dbgsrv_connect", result!.Message);
    }

    [Fact]
    public async Task UmdTtd_RequiresGuestOps()
    {
        SetVmRunning();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("umd_ttd"));
    }

    [Fact]
    public async Task UmdTtdQuery_AlwaysAllowed()
    {
        SetVmOff();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("umd_ttd_query"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  META TOOLS
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetSystemState_AlwaysAllowed()
    {
        SetVmOff();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("get_system_state"));

        SetVmRunning();
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("get_system_state"));
    }

    [Fact]
    public async Task UnknownTool_PassesThrough()
    {
        Assert.Null(await _coordinator.ValidatePreconditionsAsync("some_unknown_tool"));
    }

    // ═══════════════════════════════════════════════════════════════
    //  STATE TRANSITION TESTS
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SetKdConnected_SetsCorrectState()
    {
        _coordinator.SetKdConnected(KdTransport.KDNET);
        var state = _coordinator.State;
        Assert.True(state.KdConnected);
        Assert.Equal(KdTransport.KDNET, state.KdTransportType);
        Assert.Equal(DebugExecutionStatus.Break, state.KdExecStatus);
    }

    [Fact]
    public void SetKdDisconnected_ClearsAllKdState()
    {
        _coordinator.SetKdConnected(KdTransport.KDNET);
        _coordinator.State.KdBreakReason = "Initial breakpoint";
        _coordinator.State.KdWaitPending = true;
        _coordinator.SetBsodDetected("0x7E");

        _coordinator.SetKdDisconnected();
        var state = _coordinator.State;
        Assert.False(state.KdConnected);
        Assert.Equal(KdTransport.None, state.KdTransportType);
        Assert.Equal(DebugExecutionStatus.NoDebuggee, state.KdExecStatus);
        Assert.Null(state.KdBreakReason);
        Assert.False(state.KdWaitPending);
        Assert.False(state.IsBugcheck);
        Assert.Null(state.BugcheckCode);
    }

    [Fact]
    public void SetBsodDetected_SetsState()
    {
        _coordinator.SetBsodDetected("0x0000007E");
        Assert.True(_coordinator.State.IsBugcheck);
        Assert.Equal("0x0000007E", _coordinator.State.BugcheckCode);
    }

    [Fact]
    public void ResetAllState_ClearsEverything()
    {
        SetKdConnectedBroken();
        SetFridaAttached();
        SetDbgsrvConnected();
        _coordinator.SetBsodDetected("0x7E");

        bool kdCleaned = false, fridaCleaned = false, dbgsrvCleaned = false;
        _coordinator.CleanupKdSession = () => kdCleaned = true;
        _coordinator.CleanupFridaSession = () => fridaCleaned = true;
        _coordinator.CleanupDbgsrvSession = () => dbgsrvCleaned = true;

        _coordinator.ResetAllState();

        Assert.True(kdCleaned);
        Assert.True(fridaCleaned);
        Assert.True(dbgsrvCleaned);

        var state = _coordinator.State;
        Assert.False(state.KdConnected);
        Assert.Equal(VmPowerState.Running, state.VmPower);
        Assert.Equal(VmToolsState.Unknown, state.VmTools);
        Assert.False(state.IsBugcheck);
        Assert.Null(state.FridaState);
        Assert.Null(state.DbgsrvState);
    }

    // ═══════════════════════════════════════════════════════════════
    //  REFRESH STATE TESTS
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task RefreshState_DetectsConnectionLoss()
    {
        SetVmRunning();
        SetKdConnectedBroken();

        // Now simulate DbgEng reporting NoDebuggee (connection lost)
        _execStatus = DebugExecutionStatus.NoDebuggee;

        await _coordinator.RefreshStateAsync();

        Assert.False(_coordinator.State.KdConnected);
    }

    [Fact]
    public async Task RefreshState_UpdatesFridaState()
    {
        SetVmRunning();
        _fridaAttached = true;
        _fridaTarget = "calc.exe";

        await _coordinator.RefreshStateAsync();

        Assert.NotNull(_coordinator.State.FridaState);
        Assert.True(_coordinator.State.FridaState!.Connected);
        Assert.Equal("calc.exe", _coordinator.State.FridaState.ProcessName);
    }

    [Fact]
    public async Task RefreshState_UpdatesDbgsrvState()
    {
        SetVmRunning();
        _dbgsrvConnected = true;
        _dbgsrvPid = 5678;

        await _coordinator.RefreshStateAsync();

        Assert.NotNull(_coordinator.State.DbgsrvState);
        Assert.True(_coordinator.State.DbgsrvState!.Connected);
        Assert.Equal(5678, _coordinator.State.DbgsrvState.AttachedPid);
    }

    [Fact]
    public async Task RefreshState_DerivesGuestOpsAvailable()
    {
        SetVmRunning();
        await _coordinator.RefreshStateAsync();
        Assert.True(_coordinator.State.GuestOpsAvailable);
    }

    [Fact]
    public async Task RefreshState_GuestOpsUnavailableWhenKdBroken()
    {
        SetVmRunning();
        SetKdConnectedBroken();
        await _coordinator.RefreshStateAsync();
        Assert.False(_coordinator.State.GuestOpsAvailable);
    }
}
