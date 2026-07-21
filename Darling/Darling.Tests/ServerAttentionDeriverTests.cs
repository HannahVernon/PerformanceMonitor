/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the pure per-server "needs attention" badge derivation (<see cref="ServerAttentionDeriver"/>): the
/// grouping + count, the muted/resolved exclusions (so a silenced server and an all-clear notice never light a
/// badge), the critical-severity flag that drives the badge colour, the newest-event instant that feeds the
/// ack-until-worse clear, and the tooltip breakdown. No WPF, no Postgres, no clock.
/// </summary>
public sealed class ServerAttentionDeriverTests
{
    private static readonly DateTime T0 = new(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);

    private static ViewerAlertRow Row(int serverId, string metric, DateTime alertTime, bool muted = false) => new()
    {
        AlertTime = alertTime,
        ServerId = serverId,
        ServerName = $"Server{serverId}",
        MetricName = metric,
        CurrentValue = 90,
        ThresholdValue = 80,
        AlertSent = true,
        NotificationType = "tray",
        Muted = muted,
    };

    [Fact]
    public void EmptyInput_YieldsEmptyResult()
    {
        Assert.Empty(ServerAttentionDeriver.Derive(Array.Empty<ViewerAlertRow>()));
    }

    [Fact]
    public void GroupsByServer_AndCountsActionableRows()
    {
        var rows = new[]
        {
            Row(1, "High CPU", T0),
            Row(1, "Blocking Detected", T0.AddMinutes(1)),
            Row(2, "High CPU", T0),
        };

        var result = ServerAttentionDeriver.Derive(rows);

        Assert.Equal(2, result.Count);
        Assert.Equal(2, result[1].AlertCount);
        Assert.Equal(1, result[2].AlertCount);
    }

    [Fact]
    public void ExcludesMutedRows_SoASilencedServerShowsNoBadge()
    {
        var rows = new[]
        {
            Row(1, "High CPU", T0, muted: true),
            Row(1, "Blocking Detected", T0.AddMinutes(1), muted: true),
        };

        /* Every row for server 1 is muted -> the server is absent from the result (quiet). */
        Assert.False(ServerAttentionDeriver.Derive(rows).ContainsKey(1));
    }

    [Fact]
    public void ExcludesResolutionNotices()
    {
        var rows = new[]
        {
            Row(1, "Blocking Cleared", T0),
            Row(1, "Server Restored", T0.AddMinutes(1)),
            Row(1, "High CPU", T0.AddMinutes(2)),
        };

        var result = ServerAttentionDeriver.Derive(rows);

        /* Only the actionable High CPU row counts; the two resolution notices are good news, not attention. */
        Assert.Equal(1, result[1].AlertCount);
    }

    [Fact]
    public void IsCritical_WhenAnyRowIsCritical()
    {
        var rows = new[]
        {
            Row(1, "High CPU", T0),
            Row(1, "Deadlocks Detected", T0.AddMinutes(1)),
        };

        Assert.True(ServerAttentionDeriver.Derive(rows)[1].IsCritical);
    }

    [Fact]
    public void IsCritical_False_WhenOnlyWarnings()
    {
        var rows = new[]
        {
            Row(1, "High CPU", T0),
            Row(1, "Blocking Detected", T0.AddMinutes(1)),
        };

        Assert.False(ServerAttentionDeriver.Derive(rows)[1].IsCritical);
    }

    [Fact]
    public void LatestEventUtc_IsTheNewestAlertTime()
    {
        var rows = new[]
        {
            Row(1, "High CPU", T0.AddMinutes(3)),
            Row(1, "Blocking Detected", T0.AddMinutes(1)),
            Row(1, "High CPU", T0.AddMinutes(7)),
        };

        Assert.Equal(T0.AddMinutes(7), ServerAttentionDeriver.Derive(rows)[1].LatestEventUtc);
    }

    [Fact]
    public void Summary_ListsMetrics_MostFrequentFirst_WithCounts()
    {
        var rows = new[]
        {
            Row(1, "High CPU", T0),
            Row(1, "High CPU", T0.AddMinutes(1)),
            Row(1, "Blocking Detected", T0.AddMinutes(2)),
        };

        var summary = ServerAttentionDeriver.Derive(rows)[1].Summary;

        Assert.Equal("High CPU ×2, Blocking Detected", summary);
    }
}
