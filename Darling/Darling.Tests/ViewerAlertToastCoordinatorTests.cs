/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the two load-bearing seams of the viewer's headless in-app alert toasts — the "new since last-seen"
/// watermark and the per-condition cooldown gate in <see cref="AlertToastCoordinator"/> — without WPF,
/// Postgres, or a real clock. The tray rendering itself (Hardcodet balloons) is not unit-testable; this
/// covers all the decision logic that decides IF and WHICH polled alert rows become toasts.
/// </summary>
public sealed class ViewerAlertToastCoordinatorTests
{
    private static readonly DateTime T0 = new(2026, 7, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);

    private static ViewerAlertRow Row(
        DateTime alertTime, int serverId, string metric, bool muted = false, string? detail = null) => new()
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
        DetailText = detail,
    };

    [Fact]
    public void Ctor_RejectsNonPositiveRetention()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertToastCoordinator(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AlertToastCoordinator(TimeSpan.FromMinutes(-1)));
    }

    [Fact]
    public void Prime_SuppressesRowsThatExistedAtStartup()
    {
        var coordinator = new AlertToastCoordinator(Retention);
        var existing = new[] { Row(T0, 1, "High CPU"), Row(T0.AddMinutes(1), 2, "Blocking Detected") };
        coordinator.Prime(existing);

        /* Re-polling the exact rows that were present at startup must produce no toasts. */
        var toasts = coordinator.SelectToasts(existing, T0.AddMinutes(2), Cooldown);

        Assert.Empty(toasts);
    }

    [Fact]
    public void SelectToasts_NewRowAfterPrime_IsToasted()
    {
        var coordinator = new AlertToastCoordinator(Retention);
        coordinator.Prime(new[] { Row(T0, 1, "High CPU") });

        var fresh = Row(T0.AddMinutes(1), 2, "Deadlocks Detected");
        var toasts = coordinator.SelectToasts(
            new[] { Row(T0, 1, "High CPU"), fresh }, T0.AddMinutes(1), Cooldown);

        Assert.Single(toasts);
        Assert.Same(fresh, toasts[0]);
    }

    [Fact]
    public void SelectToasts_SameRowAcrossTwoPolls_ToastsExactlyOnce()
    {
        var coordinator = new AlertToastCoordinator(Retention);
        var row = Row(T0, 1, "High CPU");

        var first = coordinator.SelectToasts(new[] { row }, T0, Cooldown);
        var second = coordinator.SelectToasts(new[] { row }, T0.AddMinutes(1), Cooldown);

        Assert.Single(first);
        Assert.Empty(second);
    }

    [Fact]
    public void SelectToasts_MutedRow_IsNeverToasted()
    {
        var coordinator = new AlertToastCoordinator(Retention);
        var muted = Row(T0, 1, "High CPU", muted: true);

        var toasts = coordinator.SelectToasts(new[] { muted }, T0, Cooldown);

        Assert.Empty(toasts);
    }

    [Fact]
    public void SelectToasts_MutedRow_IsMarkedSeen_SoAnUnmuteInTheStoreDoesNotReplay()
    {
        var coordinator = new AlertToastCoordinator(Retention);
        var muted = Row(T0, 1, "High CPU", muted: true);
        coordinator.SelectToasts(new[] { muted }, T0, Cooldown);

        /* Same identity re-read later (even if it came back unmuted) stays suppressed — the row was
           already considered. */
        var unmutedSameIdentity = Row(T0, 1, "High CPU");
        var toasts = coordinator.SelectToasts(new[] { unmutedSameIdentity }, T0.AddMinutes(1), Cooldown);

        Assert.Empty(toasts);
    }

    [Fact]
    public void SelectToasts_TwoDistinctRowsOfSameCondition_WithinCooldown_ToastsOnlyTheFirst()
    {
        var coordinator = new AlertToastCoordinator(Retention);
        var earlier = Row(T0, 1, "High CPU");
        var later = Row(T0.AddMinutes(1), 1, "High CPU"); /* distinct row, same server+metric */

        var toasts = coordinator.SelectToasts(new[] { earlier, later }, T0.AddMinutes(1), Cooldown);

        Assert.Single(toasts);
        Assert.Same(earlier, toasts[0]); /* oldest-first: the earliest wins the cooldown slot */
    }

    [Fact]
    public void SelectToasts_ProcessesOldestFirst_RegardlessOfInputOrder()
    {
        var coordinator = new AlertToastCoordinator(Retention);
        var earlier = Row(T0, 1, "High CPU");
        var later = Row(T0.AddMinutes(1), 1, "High CPU");

        /* Newest-first input (the read's ORDER BY alert_time DESC) must still admit the OLDEST. */
        var toasts = coordinator.SelectToasts(new[] { later, earlier }, T0.AddMinutes(1), Cooldown);

        Assert.Single(toasts);
        Assert.Same(earlier, toasts[0]);
    }

    [Fact]
    public void SelectToasts_DifferentConditions_BothToastWithinCooldown()
    {
        var coordinator = new AlertToastCoordinator(Retention);
        var cpuOnA = Row(T0, 1, "High CPU");
        var cpuOnB = Row(T0, 2, "High CPU");             /* different server */
        var blockingOnA = Row(T0, 1, "Blocking Detected"); /* different metric */

        var toasts = coordinator.SelectToasts(new[] { cpuOnA, cpuOnB, blockingOnA }, T0, Cooldown);

        Assert.Equal(3, toasts.Count);
    }

    [Fact]
    public void SelectToasts_CooldownElapsed_ReToastsSameCondition()
    {
        var coordinator = new AlertToastCoordinator(Retention);
        var first = coordinator.SelectToasts(new[] { Row(T0, 1, "High CPU") }, T0, Cooldown);

        /* A distinct later row of the same condition, polled after the cooldown window, toasts again. */
        var later = Row(T0.AddMinutes(6), 1, "High CPU");
        var second = coordinator.SelectToasts(new[] { later }, T0.AddMinutes(6), Cooldown);

        Assert.Single(first);
        Assert.Single(second);
        Assert.Same(later, second[0]);
    }

    [Fact]
    public void SelectToasts_ZeroCooldown_AdmitsEveryNewRowOfAConditionInOnePoll()
    {
        var coordinator = new AlertToastCoordinator(Retention);
        var a = Row(T0, 1, "High CPU");
        var b = Row(T0.AddMinutes(1), 1, "High CPU");
        var c = Row(T0.AddMinutes(2), 1, "High CPU");

        var toasts = coordinator.SelectToasts(new[] { a, b, c }, T0.AddMinutes(2), TimeSpan.Zero);

        Assert.Equal(3, toasts.Count);
    }

    [Fact]
    public void SelectToasts_ResolvedRow_IsSeverityAgnostic_AndStillSelected()
    {
        /* The coordinator does not filter by severity — the MainWindow renders a resolved row as a green
           styled card. A "Restored"/"Cleared"/"Resolved" row (unmuted) must be admitted. */
        var coordinator = new AlertToastCoordinator(Retention);
        var resolved = Row(T0, 1, "Server Restored");

        var toasts = coordinator.SelectToasts(new[] { resolved }, T0, Cooldown);

        Assert.Single(toasts);
        Assert.True(toasts[0].IsResolved);
    }

    [Fact]
    public void SelectToasts_PrimedRowWithinRetention_StaysSuppressed()
    {
        var coordinator = new AlertToastCoordinator(Retention);
        coordinator.Prime(new[] { Row(T0, 1, "High CPU") });

        /* Well inside the retention window: the primed row is still remembered, so it never toasts. */
        var toasts = coordinator.SelectToasts(new[] { Row(T0, 1, "High CPU") }, T0.AddMinutes(10), Cooldown);

        Assert.Empty(toasts);
    }

    [Fact]
    public void SelectToasts_SeenRowBeyondRetention_IsPruned_ThenReToasts()
    {
        var coordinator = new AlertToastCoordinator(Retention);
        coordinator.Prime(new[] { Row(T0, 1, "High CPU") });

        /* A poll past the retention window prunes the stale seen key (an out-of-window row can never be
           re-read, so remembering it forever would leak). */
        coordinator.SelectToasts(Array.Empty<ViewerAlertRow>(), T0.AddMinutes(21), Cooldown);

        /* If that identity somehow re-appears it is treated as new again (bounded: retention exceeds the
           poll fetch window, so an in-window row is never pruned early). */
        var toasts = coordinator.SelectToasts(new[] { Row(T0, 1, "High CPU") }, T0.AddMinutes(21), Cooldown);

        Assert.Single(toasts);
    }
}
