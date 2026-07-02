/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PerformanceMonitor.Notifications;
using PerformanceMonitorLite.Services;
/* The whole per-check loop (CPU → blocking → deadlocks → poison waits → long-running queries →
   tempdb → volume free space → anomalous jobs → failed jobs) lives in the shared
   PerformanceMonitor.Alerting.AlertEngine (Phase-5); this file only builds the snapshot, forwards,
   and applies the sweep's badge outcomes. The app's own CpuAlertMode enum is never referenced
   here, so the namespace import cannot collide. */
using PerformanceMonitor.Alerting;

namespace PerformanceMonitorLite;

public partial class MainWindow : Window
{
    /// <summary>
    /// Phase-5 forwarding: one alert sweep for one overview summary. The evaluation —
    /// thresholds, edge triggers, cooldowns, mutes, watermark persistence, resolution
    /// transitions — runs in the shared <see cref="AlertEngine"/> (the same code the headless
    /// Darling service runs); Lite-specific delivery (tray toasts, the #1141/#1236 per-event
    /// split, the one-combined-history-row send) lives behind <see cref="LiteAlertDeliverer"/>,
    /// and this method keeps only what never left the app: the suppression input
    /// (<see cref="AlertStateService.ShouldShowAlerts"/>) and the #754/#749 server-tab badge
    /// writes fed by the engine's sweep result.
    /// </summary>
    private async void CheckPerformanceAlerts(ServerSummaryItem summary)
    {
        /* Same early return as the pre-forwarding loop: alerts off or no tray yet — leave badge
           state untouched. (_alertEngine is wired right after the tray in MainWindow_Loaded.) */
        if (!App.AlertsEnabled || _trayService == null || _alertEngine == null) return;

        var key = summary.ServerId.ToString();

        /* Resolve the ServerConnection (the GUID identity used by the tabs/badges) for this summary,
           which carries the int DuckDB server_id. Drives the server tab badge for the low-disk /
           failed-job conditions (#754/#749); null when the server isn't in the list. */
        var badgeServer = _serverManager.GetAllServers().FirstOrDefault(s =>
            RemoteCollectorService.GetDeterministicHashCode(RemoteCollectorService.GetServerNameForStorage(s)).ToString() == key);

        /* #1128 review fix: snapshot the prior badge flags, take this sweep's values from the
           engine's result, and write them ONCE at the end — so a disabled feature / offline server
           clears a stale badge (not just the active branch), and a false->true transition clears
           the ack. */
        bool prevBadgeLowDisk = badgeServer != null && _badgeLowDisk.TryGetValue(badgeServer.Id, out var _pBadgeLd) && _pBadgeLd;
        bool prevBadgeFailedJob = badgeServer != null && _badgeFailedJob.TryGetValue(badgeServer.Id, out var _pBadgeFj) && _pBadgeFj;

        /* Skip popup/email alerts if user has acknowledged or silenced this server — suppression
           is an INPUT to the shared engine (evaluate-but-don't-deliver, gates don't advance). */
        bool suppressPopups = !_alertStateService.ShouldShowAlerts(key);

        /* The failed-jobs check needs the live connection status (online + engine edition); the
           fetcher re-checks it fresh at fetch time, exactly where the old loop's gate sat. */
        var connStatus = badgeServer != null ? _serverManager.GetConnectionStatus(badgeServer.Id) : null;

        var snapshot = new AlertServerSnapshot(
            key,
            summary.DisplayName,
            IsOnline: connStatus?.IsOnline == true,
            SqlCpuPercent: summary.CpuPercent,
            TotalCpuPercent: summary.TotalCpuPercent,
            IsAzureSqlDb: connStatus?.SqlEngineEdition == 5,
            Suppressed: suppressPopups);

        AlertSweepResult sweep;
        try
        {
            sweep = await _alertEngine.EvaluateServerAsync(snapshot);
        }
        catch (Exception ex)
        {
            /* The engine absorbs per-check failures itself; this catches the truly unexpected so
               an async-void sweep can never take down the dispatcher. Badges stay untouched. */
            AppLogger.Error("Alerts", $"Alert sweep failed for {summary.DisplayName}: {ex.Message}");
            return;
        }

        /* Master switch flipped off between our gate and the engine's — no sweep ran, so leave
           badge state untouched (the old loop's early return). */
        if (!sweep.Evaluated) return;

        /* #1128 review fix: write the badge's low-disk / failed-job flags ONCE per sweep from the
           standing conditions the engine observed. Doing it here (not only inside the
           feature-enabled / online / msdb branches) means a disabled feature or an offline server
           clears a previously-lit badge instead of leaving it stale. A false->true transition is a
           genuinely new condition, so it clears any acknowledgement — matching the Dashboard, whose
           IsWorseThanBaseline re-shows on a new disk/job condition. RefreshServerBadgeExtras
           re-renders once (no-op without a tab). */
        bool curBadgeLowDisk = sweep.LowDiskConditionPresent;
        bool curBadgeFailedJob = sweep.FailedJobConditionPresent;
        if (badgeServer != null)
        {
            _badgeLowDisk[badgeServer.Id] = curBadgeLowDisk;
            _badgeFailedJob[badgeServer.Id] = curBadgeFailedJob;
            if ((curBadgeLowDisk && !prevBadgeLowDisk) || (curBadgeFailedJob && !prevBadgeFailedJob))
                _alertStateService.ClearAcknowledgementForNewCondition(badgeServer.Id);
            RefreshServerBadgeExtras(badgeServer.Id);
        }
    }

    /// <summary>
    /// The engine's live-msdb failed-jobs fetcher. Failure outcomes aren't part of the collected
    /// running_jobs snapshot, so this queries the monitored server directly at alert-check time via
    /// the collector's connection path (async SqlClient, already off the UI thread; MFA
    /// serialization / throttle / retry handled inside). The pre-forwarding gate moved here intact:
    /// only online, non-Azure-SQL-DB servers whose login has msdb access are queried — Azure SQL DB
    /// has no SQL Agent; a login without msdb can't read sysjobhistory — every other case degrades
    /// to an empty list (per the engine's fetcher contract), which also keeps the #749 badge dark.
    /// </summary>
    private async Task<List<FailedJobInfo>> FetchFailedJobsForAlertAsync(
        string serverKey, int lookbackMinutes, CancellationToken cancellationToken)
    {
        var server = _serverManager.GetAllServers().FirstOrDefault(s =>
            RemoteCollectorService.GetDeterministicHashCode(RemoteCollectorService.GetServerNameForStorage(s)).ToString() == serverKey);
        var connStatus = server != null ? _serverManager.GetConnectionStatus(server.Id) : null;

        if (server == null
            || _collectorService == null
            || connStatus == null
            || connStatus.IsOnline != true
            || connStatus.SqlEngineEdition == 5
            || !connStatus.HasMsdbAccess)
        {
            return new List<FailedJobInfo>();
        }

        /* GetRecentlyFailedJobsAsync degrades every read failure (permissions, transient) to an
           empty list itself, so a broken msdb read can't fail the sweep. */
        return await _collectorService.GetRecentlyFailedJobsAsync(server, lookbackMinutes, cancellationToken);
    }

    /// <summary>
    /// The engine's resolution callback: Lite's tray-only "Resolved/Cleared" toasts. The engine
    /// supplies the pre-forwarding loop's exact title/message strings and already applied the
    /// old gates (only on an active→inactive transition, never suppressed/disabled); every
    /// resolution toast was Success severity. No history row is recorded — exactly the old loop.
    /// </summary>
    private Task ShowAlertResolutionToastAsync(AlertResolution resolution, CancellationToken cancellationToken)
    {
        var tray = _trayService;
        if (tray != null)
        {
            if (Dispatcher.CheckAccess())
            {
                tray.ShowStyledNotification(resolution.Title, resolution.Message, ToastSeverity.Success);
            }
            else
            {
                Dispatcher.Invoke(() => tray.ShowStyledNotification(resolution.Title, resolution.Message, ToastSeverity.Success));
            }
        }
        return Task.CompletedTask;
    }
}
