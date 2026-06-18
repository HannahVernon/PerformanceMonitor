using System.Collections.Generic;
using System.Linq;
using PerformanceMonitor.Notifications;
using Xunit;

namespace PerformanceMonitorLite.Tests;

/// <summary>
/// Guards <see cref="PerEventNotification.Split"/> (#1141): one message per incident, capped, with a
/// trailing overflow batch so no #1140 fingerprint is ever dropped.
/// </summary>
public class PerEventNotificationTests
{
    private static AlertContext WithIncidents(int n)
    {
        var incidents = new List<AlertIncident>();
        for (int i = 0; i < n; i++)
            incidents.Add(new AlertIncident($"key{i}", new[] { $"db.dbo.T{i}" }, OccurrenceCount: i + 1));
        var ctx = new AlertContext();
        AlertIncidentRenderer.Apply(ctx, incidents);
        return ctx;
    }

    [Fact]
    public void Split_NoIncidents_ReturnsEmpty()
    {
        Assert.Empty(PerEventNotification.Split(new AlertContext(), 10));
    }

    [Fact]
    public void Split_WithinCap_OneMessagePerIncident()
    {
        var messages = PerEventNotification.Split(WithIncidents(3), 10);
        Assert.Equal(3, messages.Count);
        Assert.All(messages, m => Assert.False(m.IsOverflow));
        Assert.All(messages, m => Assert.Single(m.Context.Incidents!));
        Assert.Equal("db.dbo.T0", messages[0].CurrentValue);
    }

    [Fact]
    public void Split_OverCap_CapsIndividualAndBatchesOverflow()
    {
        var messages = PerEventNotification.Split(WithIncidents(5), 2);
        Assert.Equal(3, messages.Count); // 2 individual + 1 overflow
        Assert.False(messages[0].IsOverflow);
        Assert.False(messages[1].IsOverflow);
        Assert.True(messages[2].IsOverflow);
        Assert.Equal(3, messages[2].Context.Incidents!.Count); // overflow carries the remaining 3
        Assert.Contains("+3 more", messages[2].CurrentValue);
    }

    [Fact]
    public void Split_PreservesEveryFingerprint()
    {
        var source = WithIncidents(5);
        var messages = PerEventNotification.Split(source, 2);
        var emitted = messages.SelectMany(m => m.Context.Incidents!).Select(i => i.DedupKey).ToHashSet();
        var expected = source.Incidents!.Select(i => i.DedupKey).ToHashSet();
        Assert.Equal(expected, emitted);
    }

    [Fact]
    public void Split_CapZero_TreatedAsOne()
    {
        var messages = PerEventNotification.Split(WithIncidents(3), 0);
        Assert.Equal(2, messages.Count); // 1 individual + overflow of 2
        Assert.True(messages[1].IsOverflow);
    }
}
