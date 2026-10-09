using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol.Messages;
using ModelContextProtocol.Protocol.Types;
using ModelContextProtocol.Server;
using WinDbgMCP.Server.Configuration;
using WinDbgMCP.Server.KernelDebug;
using WinDbgMCP.Server.KernelDebug.Models;

namespace WinDbgMCP.Server.Notifications;

/// <summary>
/// Pushes important debug events to the MCP client as notifications/message
/// the moment they are queued. Secondary channel for whoever watches the
/// client log; the model itself is informed via the tool-result banner.
/// </summary>
public sealed class McpEventNotifier : IHostedService
{
    private static readonly HashSet<DebugEventKind> PushedKinds = new()
    {
        DebugEventKind.Bugcheck,
        DebugEventKind.BreakpointHit,
        DebugEventKind.BreakIn,
        DebugEventKind.ExceptionSecondChance,
        DebugEventKind.SystemError,
        DebugEventKind.TargetRebooted,
        DebugEventKind.SessionEnded,
        DebugEventKind.Error,
    };

    private readonly DbgEngManager _dbgEng;
    private readonly ServerConfig _config;
    private readonly IServiceProvider _services;
    private readonly ILogger<McpEventNotifier> _logger;

    public McpEventNotifier(
        DbgEngManager dbgEng, ServerConfig config, IServiceProvider services, ILogger<McpEventNotifier> logger)
    {
        _dbgEng = dbgEng;
        _config = config;
        _services = services;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_config.Notifications.PushDebugEvents)
            _dbgEng.EventRaised += OnEvent;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _dbgEng.EventRaised -= OnEvent;
        return Task.CompletedTask;
    }

    // Runs on the DbgEng thread inside an engine callback: hand off immediately.
    private void OnEvent(DebugEvent evt)
    {
        if (!PushedKinds.Contains(evt.Type))
            return;

        _ = Task.Run(() => PushAsync(evt));
    }

    private async Task PushAsync(DebugEvent evt)
    {
        try
        {
            var server = _services.GetService<IMcpServer>();
            if (server == null)
                return;

            var level = evt.Type switch
            {
                DebugEventKind.Bugcheck or DebugEventKind.ExceptionSecondChance
                    or DebugEventKind.SystemError or DebugEventKind.Error => LoggingLevel.Error,
                DebugEventKind.TargetRebooted or DebugEventKind.SessionEnded => LoggingLevel.Warning,
                _ => LoggingLevel.Notice,
            };

            // Honour the client's logging/setLevel (null = client never set one)
            if (server.LoggingLevel is { } minimum && level < minimum)
                return;

            await server.SendNotificationAsync(
                NotificationMethods.LoggingMessageNotification,
                new LoggingMessageNotificationParams
                {
                    Level = level,
                    Logger = "windbg-mcp.debug-events",
                    Data = JsonSerializer.SerializeToElement($"{evt.Type}: {evt.Details}"),
                });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to push debug event notification");
        }
    }
}
