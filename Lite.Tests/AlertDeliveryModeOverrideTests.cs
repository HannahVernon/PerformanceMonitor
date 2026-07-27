using System.Text.Json;
using PerformanceMonitor.Notifications;
using PerformanceMonitorLite.Models;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// #1236 per-server alert delivery override. Two concerns: the shared precedence rule
/// (<see cref="AlertDeliveryModeResolver"/> — a per-server override wins, a null override inherits the
/// global default) and that the override persists on <see cref="ServerConnection"/> with the same
/// WriteIndented options ServerManager uses (a pre-#1236 servers.json with no field inherits the global).
/// </summary>
public class AlertDeliveryModeOverrideTests
{
    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    [Fact]
    public void Resolve_NullOverride_InheritsGlobalSummary()
        => Assert.Equal(AlertNotificationMode.Summary, AlertDeliveryModeResolver.Resolve(null, AlertNotificationMode.Summary));

    [Fact]
    public void Resolve_NullOverride_InheritsGlobalPerEvent()
        => Assert.Equal(AlertNotificationMode.PerEvent, AlertDeliveryModeResolver.Resolve(null, AlertNotificationMode.PerEvent));

    [Fact]
    public void Resolve_PerEventOverride_WinsOverGlobalSummary()
        => Assert.Equal(AlertNotificationMode.PerEvent, AlertDeliveryModeResolver.Resolve(AlertNotificationMode.PerEvent, AlertNotificationMode.Summary));

    [Fact]
    public void Resolve_SummaryOverride_WinsOverGlobalPerEvent()
        => Assert.Equal(AlertNotificationMode.Summary, AlertDeliveryModeResolver.Resolve(AlertNotificationMode.Summary, AlertNotificationMode.PerEvent));

    [Fact]
    public void Override_RoundTripsThroughJson()
    {
        var server = new ServerConnection { ServerName = "S1", AlertDeliveryModeOverride = AlertNotificationMode.PerEvent };
        var back = JsonSerializer.Deserialize<ServerConnection>(JsonSerializer.Serialize(server, s_jsonOptions), s_jsonOptions);
        Assert.Equal(AlertNotificationMode.PerEvent, back!.AlertDeliveryModeOverride);
    }

    [Fact]
    public void NullOverride_RoundTripsAsNull()
    {
        var server = new ServerConnection { ServerName = "S1", AlertDeliveryModeOverride = null };
        var back = JsonSerializer.Deserialize<ServerConnection>(JsonSerializer.Serialize(server, s_jsonOptions), s_jsonOptions);
        Assert.Null(back!.AlertDeliveryModeOverride);
    }

    [Fact]
    public void LegacyServersJson_WithoutField_InheritsGlobal()
    {
        // A servers.json written before #1236 has no AlertDeliveryModeOverride property -> null -> inherit.
        var back = JsonSerializer.Deserialize<ServerConnection>("{\"ServerName\":\"S1\",\"DisplayName\":\"S1\"}", s_jsonOptions);
        Assert.Null(back!.AlertDeliveryModeOverride);
    }
}
