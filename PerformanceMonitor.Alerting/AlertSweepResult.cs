/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

namespace PerformanceMonitor.Alerting;

/// <summary>
/// What one <see cref="AlertEngine.EvaluateServerAsync"/> sweep OBSERVED, as opposed to what it
/// delivered. Fired alerts flow through <see cref="IAlertDeliverer"/> and recoveries through the
/// resolution callback; this result carries the two STANDING conditions an interactive host's
/// server-tab badge is computed from (Lite #754/#749) — a badge lights whenever the condition is
/// present this sweep, independent of the edge-trigger/cooldown/suppression/mute gates that decide
/// whether anything is delivered, so it cannot be reconstructed from outcomes alone (a suppressed
/// or cooldown-gated breach still lights it; a recovered/disabled check clears it). Headless hosts
/// ignore the result.
/// </summary>
/// <param name="Evaluated">
/// False when the sweep did not run at all (the <see cref="IAlertEngineSettings.AlertsEnabled"/>
/// master switch is off) — mirroring Lite's early return, after which the host leaves its badge
/// flags untouched rather than clearing them.
/// </param>
/// <param name="LowDiskConditionPresent">
/// True when the low-disk check ran and saw at least one breached volume
/// (<c>GetBreachedVolumes(...).Count &gt; 0</c> — Lite's <c>curBadgeLowDisk</c>). False when the
/// check is disabled or its read failed, so a stale badge clears exactly like Lite's #1128
/// sweep-end write.
/// </param>
/// <param name="FailedJobConditionPresent">
/// True when the failed-jobs check ran and the fetcher returned at least one failure in the
/// lookback window (Lite's <c>curBadgeFailedJob</c>) — before the watermark/cooldown gates, so a
/// failure the user was already notified about keeps the badge lit for the whole window. False
/// when the check is disabled, the server is offline/Azure SQL DB, or the fetch failed.
/// </param>
public sealed record AlertSweepResult(
    bool Evaluated,
    bool LowDiskConditionPresent,
    bool FailedJobConditionPresent)
{
    /// <summary>The master-switch-off result: nothing ran, hosts leave badge state untouched.</summary>
    public static readonly AlertSweepResult NotEvaluated = new(false, false, false);
}
