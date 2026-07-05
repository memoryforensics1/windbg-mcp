using Microsoft.Extensions.Logging.Abstractions;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.Vmware;

namespace WinDbgMCP.Tests;

/// <summary>
/// Host/remote-hypervisor configuration behavior of VmwareManager.
/// Uses the test assembly itself as a stand-in vmrun path so the
/// constructor's File.Exists check passes without VMware installed.
/// </summary>
public class VmwareManagerHostConfigTests
{
    private static readonly string FakeVmrunPath =
        typeof(VmwareManagerHostConfigTests).Assembly.Location;

    private static ServerConfig MakeConfig(
        string hostType = "ws", string hostUrl = "",
        string hostUser = "", string hostPass = "")
    {
        return new ServerConfig
        {
            Vm = new VmConfig
            {
                VmrunPath = FakeVmrunPath,
                VmxPath = @"C:\vms\test\test.vmx",
                GuestUsername = "guest",
                GuestPassword = "pass",
                HostType = hostType,
                HostUrl = hostUrl,
                HostUsername = hostUser,
                HostPassword = hostPass
            }
        };
    }

    private static VmwareManager Make(ServerConfig config) =>
        new(config, NullLogger<VmwareManager>.Instance);

    [Fact]
    public void DefaultConfig_IsLocalWorkstation()
    {
        var vmware = Make(MakeConfig());
        Assert.Equal("ws", vmware.HostType);
        Assert.False(vmware.IsRemoteHost);
    }

    [Fact]
    public void EmptyHostType_DefaultsToWs()
    {
        var vmware = Make(MakeConfig(hostType: ""));
        Assert.Equal("ws", vmware.HostType);
    }

    [Fact]
    public void RemoteHostType_WithoutUrl_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Make(MakeConfig(hostType: "esx")));
        Assert.Contains("HostUrl", ex.Message);
    }

    [Fact]
    public void HostUrl_WithoutUsername_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Make(MakeConfig(hostType: "esx", hostUrl: "https://esxi/sdk")));
        Assert.Contains("HostUsername", ex.Message);
    }

    [Fact]
    public void HostUrl_WithLocalHostType_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Make(MakeConfig(hostType: "ws", hostUrl: "https://esxi/sdk", hostUser: "root")));
        Assert.Contains("local host type", ex.Message);
    }

    [Fact]
    public void ValidRemoteConfig_IsRemoteHost()
    {
        var vmware = Make(MakeConfig(
            hostType: "esx", hostUrl: "https://esxi/sdk", hostUser: "root", hostPass: "secret"));
        Assert.True(vmware.IsRemoteHost);
        Assert.Equal("esx", vmware.HostType);
        Assert.Equal("https://esxi/sdk", vmware.HostUrl);
    }

    [Fact]
    public void UpdateTarget_WithoutHostParams_KeepsCurrentHost()
    {
        var vmware = Make(MakeConfig(
            hostType: "esx", hostUrl: "https://esxi/sdk", hostUser: "root"));

        vmware.UpdateTarget("[ds1] other/other.vmx", "user2", "pass2");

        Assert.Equal("[ds1] other/other.vmx", vmware.VmxPath);
        Assert.Equal("esx", vmware.HostType);
        Assert.Equal("https://esxi/sdk", vmware.HostUrl);
    }

    [Fact]
    public void UpdateTarget_CanSwitchLocalToRemote()
    {
        var vmware = Make(MakeConfig());

        vmware.UpdateTarget("[ds1] vm/vm.vmx", "user", "pass",
            hostType: "esx", hostUrl: "https://esxi/sdk", hostUser: "root", hostPass: "secret");

        Assert.True(vmware.IsRemoteHost);
        Assert.Equal("esx", vmware.HostType);
    }

    [Fact]
    public void UpdateTarget_CanSwitchRemoteBackToLocal()
    {
        var vmware = Make(MakeConfig(
            hostType: "esx", hostUrl: "https://esxi/sdk", hostUser: "root"));

        vmware.UpdateTarget(@"C:\vms\local\local.vmx", "user", "pass",
            hostType: "ws", hostUrl: "");

        Assert.False(vmware.IsRemoteHost);
        Assert.Equal("ws", vmware.HostType);
    }

    [Fact]
    public void UpdateTarget_InvalidHostConfig_ThrowsAndKeepsOldTarget()
    {
        var vmware = Make(MakeConfig());
        var originalVmx = vmware.VmxPath;

        Assert.Throws<InvalidOperationException>(() =>
            vmware.UpdateTarget("[ds1] vm/vm.vmx", "user", "pass",
                hostType: "esx")); // remote type but no URL

        Assert.Equal(originalVmx, vmware.VmxPath);
        Assert.Equal("ws", vmware.HostType);
        Assert.False(vmware.IsRemoteHost);
    }
}
