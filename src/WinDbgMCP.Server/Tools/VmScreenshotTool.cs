using System.ComponentModel;
using ModelContextProtocol.Server;
using WinDbgMCP.Server.State;
using WinDbgMCP.Server.Vmware;

namespace WinDbgMCP.Server.Tools;

/// <summary>
/// Separated from VmTools so it can be conditionally registered.
/// vm_screenshot requires an unencrypted VM — encrypted VMs need manual GUI unlock
/// for captureScreen to work, which defeats the purpose of automation.
/// </summary>
[McpServerToolType]
public static class VmScreenshotTool
{
    [McpServerTool(Name = "vm_screenshot"), Description(
        "Capture a screenshot of the VM display. Useful for checking guest OS state " +
        "(boot screen, BSOD, login screen, etc).")]
    public static Task<string> VmScreenshot(
        StateCoordinator state,
        VmwareManager vmware,
        [Description("Host path to save the screenshot PNG")] string outputPath = @"C:\MCP_Logs\screenshot.png",
        CancellationToken ct = default)
    {
        return state.RunToolAsync("vm_screenshot", async () =>
        {
            try
            {
                var result = await vmware.CaptureScreenAsync(outputPath, ct);
                if (!result.Success)
                    return $"vm_screenshot failed: {result.Message}";

                return result.Message;
            }
            catch (TimeoutException)
            {
                return ErrorMessages.OperationTimedOut("vm_screenshot", 10);
            }
        });
    }
}
