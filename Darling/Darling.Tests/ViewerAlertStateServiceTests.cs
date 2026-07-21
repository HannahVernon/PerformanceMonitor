/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.IO;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the viewer's per-server alert-badge acknowledgement state machine (<see cref="ViewerAlertStateService"/>) —
/// the ported "ack-until-worse" half of Lite's <c>AlertStateService</c> (mirrors Lite.Tests/AlertBadgeAckTests):
/// an acknowledgement suppresses the badge until a genuinely NEWER alert arrives, at which point it clears and the
/// badge re-lights. The clock and the state file are injected so the timestamp seam is deterministic and no test
/// leaks state to another (each uses a unique temp file).
/// </summary>
public sealed class ViewerAlertStateServiceTests : IDisposable
{
    private static readonly DateTime AckAt = new(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _statePath = Path.Combine(Path.GetTempPath(), $"darling-ack-{Guid.NewGuid():N}.json");

    private ViewerAlertStateService NewService(DateTime? ackClock = null) =>
        new(_statePath, () => ackClock ?? AckAt);

    public void Dispose()
    {
        try { File.Delete(_statePath); } catch { /* best effort */ }
    }

    [Fact]
    public void FreshServer_WithAlerts_ShowsBadge()
    {
        var svc = NewService();

        Assert.True(svc.ShouldShowAlerts(1));
        Assert.True(svc.UpdateAlertState(1, alertCount: 2, latestEventTimeUtc: AckAt));
    }

    [Fact]
    public void NoAlerts_NeverShowsBadge()
    {
        var svc = NewService();

        Assert.False(svc.UpdateAlertState(1, alertCount: 0, latestEventTimeUtc: null));
    }

    [Fact]
    public void Acknowledge_SuppressesBadge_ForTheSameAndOlderEvents()
    {
        var svc = NewService();

        /* The badge is lit... */
        Assert.True(svc.UpdateAlertState(1, alertCount: 3, latestEventTimeUtc: AckAt));

        /* ...the operator acknowledges it. */
        svc.AcknowledgeAlert(1);
        Assert.False(svc.ShouldShowAlerts(1));
        Assert.True(svc.IsAcknowledged(1));

        /* A re-read of the SAME rows (latest event == the ack instant) stays suppressed... */
        Assert.False(svc.UpdateAlertState(1, alertCount: 3, latestEventTimeUtc: AckAt));

        /* ...and so does an OLDER event still lingering in the rolling window. */
        Assert.False(svc.UpdateAlertState(1, alertCount: 3, latestEventTimeUtc: AckAt.AddMinutes(-5)));
    }

    [Fact]
    public void Acknowledge_ThenNewerAlert_ClearsAck_AndRelights()
    {
        var svc = NewService();

        svc.AcknowledgeAlert(1);
        Assert.False(svc.ShouldShowAlerts(1));

        /* A genuinely newer alert (alert_time after the ack) worsens the condition -> ack clears, badge shows. */
        Assert.True(svc.UpdateAlertState(1, alertCount: 1, latestEventTimeUtc: AckAt.AddMinutes(1)));
        Assert.True(svc.ShouldShowAlerts(1));
        Assert.False(svc.IsAcknowledged(1));
    }

    [Fact]
    public void Acknowledge_IsPerServer_AndDoesNotSuppressOthers()
    {
        var svc = NewService();

        svc.AcknowledgeAlert(1);

        Assert.False(svc.UpdateAlertState(1, alertCount: 2, latestEventTimeUtc: AckAt));
        Assert.True(svc.UpdateAlertState(2, alertCount: 2, latestEventTimeUtc: AckAt));
    }

    [Fact]
    public void AcknowledgeAlert_RaisesSuppressionStateChanged()
    {
        var svc = NewService();
        var fired = false;
        svc.SuppressionStateChanged += (_, _) => fired = true;

        svc.AcknowledgeAlert(1);

        Assert.True(fired);
    }

    [Fact]
    public void ClearAcknowledgement_WhenAcknowledged_Fires_AndRelights()
    {
        var svc = NewService();
        svc.AcknowledgeAlert(1);

        var fired = false;
        svc.SuppressionStateChanged += (_, _) => fired = true;

        svc.ClearAcknowledgement(1);

        Assert.True(fired);
        Assert.True(svc.ShouldShowAlerts(1));
        Assert.True(svc.UpdateAlertState(1, alertCount: 1, latestEventTimeUtc: AckAt));
    }

    [Fact]
    public void ClearAcknowledgement_WhenNothingAcknowledged_DoesNotFire()
    {
        var svc = NewService();
        var fired = false;
        svc.SuppressionStateChanged += (_, _) => fired = true;

        svc.ClearAcknowledgement(1);

        Assert.False(fired);
    }

    [Fact]
    public void RemoveServerState_ClearsTheAcknowledgement()
    {
        var svc = NewService();
        svc.AcknowledgeAlert(1);
        Assert.True(svc.IsAcknowledged(1));

        svc.RemoveServerState(1);

        Assert.False(svc.IsAcknowledged(1));
        Assert.True(svc.UpdateAlertState(1, alertCount: 1, latestEventTimeUtc: AckAt));
    }

    [Fact]
    public void Acknowledgement_SurvivesRestart_ViaTheStateFile()
    {
        /* First instance acknowledges and persists. */
        var first = new ViewerAlertStateService(_statePath, () => AckAt);
        first.AcknowledgeAlert(1);

        /* A fresh instance over the SAME file loads the ack, so a restart doesn't re-light an acknowledged badge
           for events still in the rolling window. */
        var second = new ViewerAlertStateService(_statePath, () => AckAt);
        Assert.True(second.IsAcknowledged(1));
        Assert.False(second.UpdateAlertState(1, alertCount: 2, latestEventTimeUtc: AckAt));

        /* ...but a newer event after the restart still clears it. */
        Assert.True(second.UpdateAlertState(1, alertCount: 2, latestEventTimeUtc: AckAt.AddMinutes(10)));
    }
}
