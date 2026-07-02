/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PerformanceMonitor.Notifications;
using PerformanceMonitorLite.Controls;
using PerformanceMonitorLite.Database;
using PerformanceMonitorLite.Mcp;
using PerformanceMonitorLite.Models;
using PerformanceMonitorLite.Services;
using PerformanceMonitorLite.Windows;
using PerformanceMonitor.Common;
using PerformanceMonitor.Ui;
/* Type alias (not a namespace import) so PerformanceMonitor.Alerting's CpuAlertMode enum can never
   collide with this app's own CpuAlertMode used below. */
using AlertContextBuilders = PerformanceMonitor.Alerting.AlertContextBuilders;

namespace PerformanceMonitorLite;

public partial class MainWindow : Window
{
    private async void CheckPerformanceAlerts(ServerSummaryItem summary)
    {
        if (!App.AlertsEnabled || _trayService == null) return;

        var key = summary.ServerId.ToString();
        var now = DateTime.UtcNow;

        /* Resolve the ServerConnection (the GUID identity used by the tabs/badges) for this summary,
           which carries the int DuckDB server_id. Drives the server tab badge for the low-disk /
           failed-job conditions (#754/#749); null when the server isn't in the list. */
        var badgeServer = _serverManager.GetAllServers().FirstOrDefault(s =>
            RemoteCollectorService.GetDeterministicHashCode(RemoteCollectorService.GetServerNameForStorage(s)).ToString() == key);

        /* #1128 review fix: snapshot the prior badge flags, recompute them as locals through the
           sweep, and write them ONCE at the end — so a disabled feature / offline server clears a
           stale badge (not just the active branch), and a false->true transition clears the ack. */
        bool prevBadgeLowDisk = badgeServer != null && _badgeLowDisk.TryGetValue(badgeServer.Id, out var _pBadgeLd) && _pBadgeLd;
        bool prevBadgeFailedJob = badgeServer != null && _badgeFailedJob.TryGetValue(badgeServer.Id, out var _pBadgeFj) && _pBadgeFj;
        bool curBadgeLowDisk = false;
        bool curBadgeFailedJob = false;

        var alertCooldown = TimeSpan.FromMinutes(App.AlertCooldownMinutes);

        /* Skip popup/email alerts if user has acknowledged or silenced this server */
        bool suppressPopups = !_alertStateService.ShouldShowAlerts(key);

        /* CPU alerts — uses the metric the user selected (Total non-idle CPU by default, or SQL Server only). */
        var alertCpuValue = summary.CpuPercentForAlert;
        string cpuMetricLabel = App.AlertCpuMode == CpuAlertMode.Total ? "Total CPU" : "SQL CPU";
        bool cpuExceeded = App.AlertCpuEnabled
            && alertCpuValue.HasValue
            && alertCpuValue.Value >= App.AlertCpuThreshold;

        if (cpuExceeded)
        {
            _activeCpuAlert[key] = true;
            if (!suppressPopups && (!_lastCpuAlert.TryGetValue(key, out var lastCpu) || now - lastCpu >= alertCooldown))
            {
                var muteCtx = new AlertMuteContext { ServerName = summary.DisplayName, MetricName = "High CPU" };
                bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
                _lastCpuAlert[key] = now;

                if (!isMuted)
                {
                    _trayService.ShowSnoozableNotification(
                        "High CPU",
                        $"{summary.DisplayName}: {cpuMetricLabel} at {alertCpuValue:F0}% (threshold: {App.AlertCpuThreshold}%)",
                        Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning,
                        summary.DisplayName,
                        "High CPU",
                        _muteRuleService);
                }

                var cpuDetailText = $"  {cpuMetricLabel}: {alertCpuValue:F0}%\n  Threshold: {App.AlertCpuThreshold}%";

                await _emailAlertService.TrySendAlertEmailAsync(
                    "High CPU",
                    summary.DisplayName,
                    $"{alertCpuValue:F0}% ({cpuMetricLabel})",
                    $"{App.AlertCpuThreshold}%",
                    summary.ServerId,
                    muted: isMuted,
                    detailText: cpuDetailText);
            }
        }
        else if (_activeCpuAlert.TryGetValue(key, out var wasCpu) && wasCpu)
        {
            _activeCpuAlert[key] = false;
            /* Only announce "resolved" if the user is still watching this alert and the server
               isn't silenced. Disabling the alert flips cpuExceeded false (it includes the enabled
               flag) and silencing sets suppressPopups — neither means CPU actually recovered. */
            if (!suppressPopups && App.AlertCpuEnabled)
            {
                _trayService.ShowStyledNotification(
                    "CPU Resolved",
                    $"{summary.DisplayName}: {cpuMetricLabel} back to {alertCpuValue:F0}%",
                    ToastSeverity.Success);
            }
        }

        /* Blocking alerts */
        var effectiveBlockingCount = summary.BlockingCount;
        if (App.AlertBlockingEnabled && App.AlertExcludedDatabases.Count > 0
            && summary.BlockingCount >= App.AlertBlockingThreshold && _alertReadAdapter != null)
        {
            try
            {
                var blockingRows = await _alertReadAdapter.GetRecentBlockedProcessReportsAsync(key, hoursBack: 1);
                effectiveBlockingCount = blockingRows
                    .Count(r => string.IsNullOrEmpty(r.DatabaseName) ||
                        !App.AlertExcludedDatabases.Any(e =>
                            string.Equals(e, r.DatabaseName, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex)
            {
                AppLogger.Error("Alerts", $"Failed to filter blocking count for {summary.DisplayName}: {ex.Message}");
            }
        }

        /* Edge-trigger the rolling 1-hour blocking count so the same blocked-process reports
           don't re-alert every cooldown for the whole hour they linger in the window (#1091).
           See RollingCountAlertGate for the watermark semantics. */
        int blockingWatermark = _lastAlertedBlockingCount.TryGetValue(key, out var labc) ? labc : 0;
        bool blockingCooldownElapsed = !_lastBlockingAlert.TryGetValue(key, out var lastBlocking) || now - lastBlocking >= alertCooldown;
        var blockingDecision = App.AlertBlockingEnabled
            ? RollingCountAlertGate.Evaluate(effectiveBlockingCount, App.AlertBlockingThreshold, blockingWatermark, blockingCooldownElapsed, suppressPopups)
            : new RollingCountAlertGate.Decision(false, false, 0);
        _lastAlertedBlockingCount[key] = blockingDecision.Watermark;
        /* Persist the watermark across restart so the same blocked-process reports aren't
           re-alerted (and re-posted to Teams/Slack) on the first post-restart sweep (#1145).
           On-change only — the gate returns the same watermark on most sweeps. */
        if (blockingDecision.Watermark != blockingWatermark)
        {
            await _alertHistoryStore.SaveEdgeTriggerWatermarkAsync(summary.ServerId, BlockingWatermarkMetric, blockingDecision.Watermark);
        }

        bool wasBlockingActive = _activeBlockingAlert.TryGetValue(key, out var wasBlocking) && wasBlocking;
        _activeBlockingAlert[key] = blockingDecision.Active;

        if (blockingDecision.Fire)
        {
            var muteCtx = new AlertMuteContext { ServerName = summary.DisplayName, MetricName = "Blocking Detected" };
            bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
            _lastBlockingAlert[key] = now;

            if (!isMuted)
            {
                _trayService.ShowSnoozableNotification(
                    "Blocking Detected",
                    $"{summary.DisplayName}: {effectiveBlockingCount} blocking session(s)",
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning,
                    summary.DisplayName,
                    "Blocking Detected",
                    _muteRuleService);
            }

            var blockingContext = await BuildBlockingContextAsync(summary.ServerId, summary.DisplayName);
            var detailText = AlertContextBuilders.ContextToDetailText(blockingContext);

            await SendDetectedAlertAsync(
                "Blocking Detected",
                summary.DisplayName,
                effectiveBlockingCount.ToString(),
                App.AlertBlockingThreshold.ToString(),
                summary.ServerId,
                blockingContext,
                isMuted,
                detailText);
        }
        else if (!blockingDecision.Active && wasBlockingActive)
        {
            if (!suppressPopups && App.AlertBlockingEnabled)
            {
                _trayService.ShowStyledNotification(
                    "Blocking Cleared",
                    $"{summary.DisplayName}: No active blocking",
                    ToastSeverity.Success);
            }
        }

        /* Deadlock alerts */
        var effectiveDeadlockCount = summary.DeadlockCount;
        if (App.AlertDeadlockEnabled && App.AlertExcludedDatabases.Count > 0
            && summary.DeadlockCount >= App.AlertDeadlockThreshold && _alertReadAdapter != null)
        {
            try
            {
                var deadlockRows = await _alertReadAdapter.GetRecentDeadlocksAsync(key, hoursBack: 1);
                effectiveDeadlockCount = deadlockRows
                    .Count(r => !AlertContextBuilders.IsDeadlockExcluded(r, App.AlertExcludedDatabases));
            }
            catch (Exception ex)
            {
                AppLogger.Error("Alerts", $"Failed to filter deadlock count for {summary.DisplayName}: {ex.Message}");
            }
        }

        /* Edge-trigger the rolling 1-hour deadlock count so the same deadlocks don't re-alert
           every cooldown for the whole hour they linger in the window (#1091). See
           RollingCountAlertGate for the watermark semantics. */
        int deadlockWatermark = _lastAlertedDeadlockCount.TryGetValue(key, out var ladc) ? ladc : 0;
        bool deadlockCooldownElapsed = !_lastDeadlockAlert.TryGetValue(key, out var lastDeadlock) || now - lastDeadlock >= alertCooldown;
        var deadlockDecision = App.AlertDeadlockEnabled
            ? RollingCountAlertGate.Evaluate(effectiveDeadlockCount, App.AlertDeadlockThreshold, deadlockWatermark, deadlockCooldownElapsed, suppressPopups)
            : new RollingCountAlertGate.Decision(false, false, 0);
        _lastAlertedDeadlockCount[key] = deadlockDecision.Watermark;
        /* Persist the watermark across restart so the same deadlocks aren't re-alerted (and
           re-posted to Teams/Slack) on the first post-restart sweep (#1145). On-change only. */
        if (deadlockDecision.Watermark != deadlockWatermark)
        {
            await _alertHistoryStore.SaveEdgeTriggerWatermarkAsync(summary.ServerId, DeadlockWatermarkMetric, deadlockDecision.Watermark);
        }

        bool wasDeadlockActive = _activeDeadlockAlert.TryGetValue(key, out var wasDeadlock) && wasDeadlock;
        _activeDeadlockAlert[key] = deadlockDecision.Active;

        if (deadlockDecision.Fire)
        {
            var muteCtx = new AlertMuteContext { ServerName = summary.DisplayName, MetricName = "Deadlocks Detected" };
            bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
            _lastDeadlockAlert[key] = now;

            if (!isMuted)
            {
                _trayService.ShowSnoozableNotification(
                    "Deadlocks Detected",
                    $"{summary.DisplayName}: {effectiveDeadlockCount} deadlock(s) in the last hour",
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error,
                    summary.DisplayName,
                    "Deadlocks Detected",
                    _muteRuleService);
            }

            var deadlockContext = await BuildDeadlockContextAsync(summary.ServerId, summary.DisplayName);
            var detailText = AlertContextBuilders.ContextToDetailText(deadlockContext);

            await SendDetectedAlertAsync(
                "Deadlocks Detected",
                summary.DisplayName,
                effectiveDeadlockCount.ToString(),
                App.AlertDeadlockThreshold.ToString(),
                summary.ServerId,
                deadlockContext,
                isMuted,
                detailText);
        }
        else if (!deadlockDecision.Active && wasDeadlockActive)
        {
            if (!suppressPopups && App.AlertDeadlockEnabled)
            {
                _trayService.ShowStyledNotification(
                    "Deadlocks Cleared",
                    $"{summary.DisplayName}: No deadlocks in the last hour",
                    ToastSeverity.Success);
            }
        }

        /* Poison wait alerts */
        if (App.AlertPoisonWaitEnabled && _alertReadAdapter != null)
        {
            try
            {
                var triggered = await _alertReadAdapter.GetPoisonWaitDeltasAsync(key, App.AlertPoisonWaitThresholdMs);

                if (triggered.Count > 0)
                {
                    _activePoisonWaitAlert[key] = true;
                    if (!suppressPopups && (!_lastPoisonWaitAlert.TryGetValue(key, out var lastPoisonWait) || now - lastPoisonWait >= alertCooldown))
                    {
                        var worst = triggered[0];
                        var allWaitNames = string.Join(", ", triggered.ConvertAll(w => $"{w.WaitType} ({w.AvgMsPerWait:F0}ms)"));

                        /* Poison wait mute check uses the worst (highest avg ms/wait) triggered wait type.
                           Limitation: if a user mutes a specific wait type that isn't the worst, the alert
                           still fires. Conversely, muting the worst type suppresses the entire alert even
                           if other unmuted poison waits are present. */
                        var muteCtx = new AlertMuteContext { ServerName = summary.DisplayName, MetricName = "Poison Wait", WaitType = worst.WaitType };
                        bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
                        _lastPoisonWaitAlert[key] = now;

                        if (!isMuted)
                        {
                            _trayService.ShowSnoozableNotification(
                                "Poison Wait",
                                $"{summary.DisplayName}: {worst.WaitType} avg {worst.AvgMsPerWait:F0}ms/wait",
                                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error,
                                summary.DisplayName,
                                "Poison Wait",
                                _muteRuleService);
                        }

                        var poisonContext = AlertContextBuilders.BuildPoisonWaitContext(triggered);
                        var detailText = AlertContextBuilders.ContextToDetailText(poisonContext);

                        await _emailAlertService.TrySendAlertEmailAsync(
                            "Poison Wait",
                            summary.DisplayName,
                            allWaitNames,
                            $"{App.AlertPoisonWaitThresholdMs}ms avg",
                            summary.ServerId,
                            poisonContext,
                            numericCurrentValue: worst.AvgMsPerWait,
                            numericThresholdValue: App.AlertPoisonWaitThresholdMs,
                            muted: isMuted,
                            detailText: detailText);
                    }
                }
                else if (_activePoisonWaitAlert.TryGetValue(key, out var wasPoisonWait) && wasPoisonWait)
                {
                    _activePoisonWaitAlert[key] = false;
                    if (!suppressPopups)
                    {
                        _trayService.ShowStyledNotification(
                            "Poison Waits Cleared",
                            $"{summary.DisplayName}: Poison wait avg below threshold",
                            ToastSeverity.Success);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Alerts", $"Failed to check poison waits for {summary.DisplayName}: {ex.Message}");
            }
        }

        /* Long-running query alerts */
        if (App.AlertLongRunningQueryEnabled && _alertReadAdapter != null)
        {
            try
            {
                var longRunning = await _alertReadAdapter.GetLongRunningQueriesAsync(key, App.AlertLongRunningQueryThresholdMinutes, App.AlertLongRunningQueryMaxResults, App.AlertLongRunningQueryExcludeSpServerDiagnostics, App.AlertLongRunningQueryExcludeWaitFor, App.AlertLongRunningQueryExcludeBackups, App.AlertLongRunningQueryExcludeMiscWaits, App.AlertLongRunningQueryExcludeCdc, App.AlertExcludedDatabases);

                if (longRunning.Count > 0)
                {
                    _activeLongRunningQueryAlert[key] = true;
                    if (!suppressPopups && (!_lastLongRunningQueryAlert.TryGetValue(key, out var lastLrq) || now - lastLrq >= alertCooldown))
                    {
                        var worst = longRunning[0];
                        var elapsedMinutes = worst.ElapsedSeconds / 60;
                        var preview = AlertContextBuilders.TruncateText(worst.QueryText, 80);
                        var previewSuffix = string.IsNullOrEmpty(preview) ? "" : $" — {preview}";

                        var muteCtx = new AlertMuteContext
                        {
                            ServerName = summary.DisplayName,
                            MetricName = "Long-Running Query",
                            DatabaseName = worst.DatabaseName,
                            QueryText = worst.QueryText
                        };
                        bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
                        _lastLongRunningQueryAlert[key] = now;

                        if (!isMuted)
                        {
                            _trayService.ShowSnoozableNotification(
                                "Long-Running Query",
                                $"{summary.DisplayName}: Session #{worst.SessionId} running {elapsedMinutes}m{previewSuffix}",
                                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning,
                                summary.DisplayName,
                                "Long-Running Query",
                                _muteRuleService);
                        }

                        var lrqContext = AlertContextBuilders.BuildLongRunningQueryContext(summary.DisplayName, longRunning);
                        var detailText = AlertContextBuilders.ContextToDetailText(lrqContext);

                        await _emailAlertService.TrySendAlertEmailAsync(
                            "Long-Running Query",
                            summary.DisplayName,
                            $"{longRunning.Count} query(s), longest {elapsedMinutes}m",
                            $"{App.AlertLongRunningQueryThresholdMinutes}m",
                            summary.ServerId,
                            lrqContext,
                            numericCurrentValue: elapsedMinutes,
                            numericThresholdValue: App.AlertLongRunningQueryThresholdMinutes,
                            muted: isMuted,
                            detailText: detailText);
                    }
                }
                else if (_activeLongRunningQueryAlert.TryGetValue(key, out var wasLongRunning) && wasLongRunning)
                {
                    _activeLongRunningQueryAlert[key] = false;
                    if (!suppressPopups)
                    {
                        _trayService.ShowStyledNotification(
                            "Long-Running Queries Cleared",
                            $"{summary.DisplayName}: No queries over threshold",
                            ToastSeverity.Success);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Alerts", $"Failed to check long-running queries for {summary.DisplayName}: {ex.Message}");
            }
        }

        /* TempDB space alerts */
        if (App.AlertTempDbSpaceEnabled && _alertReadAdapter != null)
        {
            try
            {
                var tempDb = await _alertReadAdapter.GetTempDbSpaceAsync(key);

                if (tempDb != null && tempDb.UsedPercent >= App.AlertTempDbSpaceThresholdPercent)
                {
                    _activeTempDbSpaceAlert[key] = true;
                    if (!suppressPopups && (!_lastTempDbSpaceAlert.TryGetValue(key, out var lastTempDb) || now - lastTempDb >= alertCooldown))
                    {
                        var muteCtx = new AlertMuteContext { ServerName = summary.DisplayName, MetricName = "tempdb Space" };
                        bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
                        _lastTempDbSpaceAlert[key] = now;

                        if (!isMuted)
                        {
                            _trayService.ShowSnoozableNotification(
                                "tempdb Space",
                                $"{summary.DisplayName}: tempdb {tempDb.UsedPercent:F0}% used",
                                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning,
                                summary.DisplayName,
                                "tempdb Space",
                                _muteRuleService);
                        }

                        var tempDbContext = AlertContextBuilders.BuildTempDbSpaceContext(tempDb);
                        var detailText = AlertContextBuilders.ContextToDetailText(tempDbContext);

                        await _emailAlertService.TrySendAlertEmailAsync(
                            "tempdb Space",
                            summary.DisplayName,
                            $"{tempDb.UsedPercent:F0}% used ({tempDb.TotalReservedMb:F0} MB)",
                            $"{App.AlertTempDbSpaceThresholdPercent}%",
                            summary.ServerId,
                            tempDbContext,
                            numericCurrentValue: tempDb.UsedPercent,
                            numericThresholdValue: App.AlertTempDbSpaceThresholdPercent,
                            muted: isMuted,
                            detailText: detailText);
                    }
                }
                else if (_activeTempDbSpaceAlert.TryGetValue(key, out var wasTempDb) && wasTempDb)
                {
                    _activeTempDbSpaceAlert[key] = false;
                    if (!suppressPopups)
                    {
                        var pct = tempDb != null ? $"{tempDb.UsedPercent:F0}%" : "N/A";
                        _trayService.ShowStyledNotification(
                            "tempdb Space Resolved",
                            $"{summary.DisplayName}: tempdb usage back to {pct}",
                            ToastSeverity.Success);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Alerts", $"Failed to check TempDB space for {summary.DisplayName}: {ex.Message}");
            }
        }

        /* Low volume free space alerts — not applicable to Azure SQL DB (no volume stats collected) */
        if (App.AlertLowDiskEnabled && _alertReadAdapter != null)
        {
            try
            {
                var volumes = await _alertReadAdapter.GetVolumeFreeSpaceAsync(key);
                var breached = AlertContextBuilders.GetBreachedVolumes(volumes, App.AlertLowDiskThresholdPercent, App.AlertLowDiskThresholdGb);

                /* Drive the server tab badge — a breached volume is a standing condition (#754).
                   Recorded as a local; the flags are written once at the end of the sweep (#1128 review). */
                curBadgeLowDisk = breached.Count > 0;

                if (breached.Count > 0)
                {
                    var worst = breached[0];
                    _activeLowDiskAlert[key] = true;
                    double? lastLowDiskPercent =
                        _lastAlertedLowDiskPercent.TryGetValue(key, out var lowDiskPct) ? lowDiskPct : (double?)null;
                    /* #754 follow-up: notify only on a fresh or worsening breach, not every cooldown for a
                       standing full volume (which also re-recorded a history row and made Dismiss feel broken). */
                    if (!suppressPopups
                        && LowDiskAlertGate.ShouldAlert(worst.FreePercent, lastLowDiskPercent)
                        && (!_lastLowDiskAlert.TryGetValue(key, out var lastLowDisk) || now - lastLowDisk >= alertCooldown))
                    {
                        var muteCtx = new AlertMuteContext { ServerName = summary.DisplayName, MetricName = "Volume Free Space" };
                        bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
                        _lastLowDiskAlert[key] = now;
                        _lastAlertedLowDiskPercent[key] = worst.FreePercent;

                        if (!isMuted)
                        {
                            _trayService.ShowSnoozableNotification(
                                "Volume Free Space",
                                $"{summary.DisplayName}: {worst.MountPoint} {worst.FreePercent:F0}% free ({worst.FreeGb:F1} GB)",
                                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning,
                                summary.DisplayName,
                                "Volume Free Space",
                                _muteRuleService);
                        }

                        var lowDiskContext = AlertContextBuilders.BuildVolumeFreeSpaceContext(summary.DisplayName, breached);
                        /* #1136: grade the alert — WARNING normally, CRITICAL when the worst volume is
                           critically low — so the email/webhook badge reflects how dire the breach is.
                           (lowDiskContext is non-null here — breached.Count > 0 — but typed nullable.) */
                        if (lowDiskContext is not null && LowDiskAlertGate.IsCriticallyLow(worst.FreePercent, worst.FreeGb))
                        {
                            lowDiskContext.SeverityOverride = AlertSeverityLevel.Critical;
                        }
                        var detailText = AlertContextBuilders.ContextToDetailText(lowDiskContext);

                        await _emailAlertService.TrySendAlertEmailAsync(
                            "Volume Free Space",
                            summary.DisplayName,
                            $"{worst.MountPoint} {worst.FreePercent:F0}% free ({worst.FreeGb:F1} GB)",
                            AlertContextBuilders.FormatLowDiskThreshold(App.AlertLowDiskThresholdPercent, App.AlertLowDiskThresholdGb),
                            summary.ServerId,
                            lowDiskContext,
                            numericCurrentValue: worst.FreePercent,
                            numericThresholdValue: App.AlertLowDiskThresholdPercent,
                            muted: isMuted,
                            detailText: detailText);
                    }
                }
                else if (_activeLowDiskAlert.TryGetValue(key, out var wasLowDisk) && wasLowDisk)
                {
                    _activeLowDiskAlert[key] = false;
                    _lastAlertedLowDiskPercent.Remove(key);
                    if (!suppressPopups)
                    {
                        _trayService.ShowStyledNotification(
                            "Volume Free Space Resolved",
                            $"{summary.DisplayName}: All volumes back above threshold",
                            ToastSeverity.Success);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Alerts", $"Failed to check volume free space for {summary.DisplayName}: {ex.Message}");
            }
        }

        /* Anomalous Agent job alerts */
        if (App.AlertLongRunningJobEnabled && _alertReadAdapter != null)
        {
            try
            {
                var anomalousJobs = await _alertReadAdapter.GetAnomalousJobsAsync(key, App.AlertLongRunningJobMultiplier);

                /* _lastLongRunningJobAlert is keyed per job *run* ({server}:{jobId}:{startTime}),
                   so unlike the per-server cooldown dicts it grows without bound. Drop entries
                   that have aged past the cooldown each pass. */
                foreach (var staleJobKey in _lastLongRunningJobAlert
                             .Where(kv => now - kv.Value >= alertCooldown)
                             .Select(kv => kv.Key)
                             .ToList())
                {
                    _lastLongRunningJobAlert.Remove(staleJobKey);
                }

                if (anomalousJobs.Count > 0)
                {
                    _activeLongRunningJobAlert[key] = true;
                    var worst = anomalousJobs[0];
                    var jobKey = $"{key}:{worst.JobId}:{worst.StartTime:O}";

                    if (!suppressPopups && (!_lastLongRunningJobAlert.TryGetValue(jobKey, out var lastJob) || now - lastJob >= alertCooldown))
                    {
                        var currentMinutes = worst.CurrentDurationSeconds / 60;

                        var muteCtx = new AlertMuteContext { ServerName = summary.DisplayName, MetricName = "Long-Running Job", JobName = worst.JobName };
                        bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
                        _lastLongRunningJobAlert[jobKey] = now;

                        if (!isMuted)
                        {
                            _trayService.ShowSnoozableNotification(
                                "Long-Running Job",
                                $"{summary.DisplayName}: {worst.JobName} at {worst.PercentOfAverage:F0}% of avg ({currentMinutes}m)",
                                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning,
                                summary.DisplayName,
                                "Long-Running Job",
                                _muteRuleService);
                        }

                        var jobContext = AlertContextBuilders.BuildAnomalousJobContext(summary.DisplayName, anomalousJobs);
                        var detailText = AlertContextBuilders.ContextToDetailText(jobContext);

                        await _emailAlertService.TrySendAlertEmailAsync(
                            "Long-Running Job",
                            summary.DisplayName,
                            $"{anomalousJobs.Count} job(s) exceeding {App.AlertLongRunningJobMultiplier}x average",
                            $"{App.AlertLongRunningJobMultiplier}x historical avg",
                            summary.ServerId,
                            jobContext,
                            numericCurrentValue: (double)(worst.PercentOfAverage ?? 0),
                            numericThresholdValue: App.AlertLongRunningJobMultiplier * 100,
                            muted: isMuted,
                            detailText: detailText);
                    }
                }
                else if (_activeLongRunningJobAlert.TryGetValue(key, out var wasJob) && wasJob)
                {
                    _activeLongRunningJobAlert[key] = false;
                    if (!suppressPopups)
                    {
                        _trayService.ShowStyledNotification(
                            "Long-Running Jobs Cleared",
                            $"{summary.DisplayName}: No jobs exceeding threshold",
                            ToastSeverity.Success);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Alerts", $"Failed to check anomalous jobs for {summary.DisplayName}: {ex.Message}");
            }
        }

        /* Failed Agent job alerts — live msdb query for runs that failed in the lookback window.
           Failure outcomes aren't part of the collected running_jobs snapshot, so this queries the
           monitored server directly (mirrors the long-running-query live pattern). Failures are
           point-in-time events, so there is no "cleared" notification; the per-server watermark
           dedups so the same failure never re-fires. */
        if (App.AlertFailedJobEnabled && _collectorService != null)
        {
            try
            {
                var server = _serverManager.GetAllServers().FirstOrDefault(s =>
                    RemoteCollectorService.GetDeterministicHashCode(RemoteCollectorService.GetServerNameForStorage(s)).ToString() == key);
                var connStatus = server != null ? _serverManager.GetConnectionStatus(server.Id) : null;

                /* Only query online, non-Azure-SQL-DB servers whose login has msdb access.
                   Azure SQL DB has no SQL Agent; a login without msdb can't read sysjobhistory. */
                if (server != null
                    && connStatus != null
                    && connStatus.IsOnline == true
                    && connStatus.SqlEngineEdition != 5
                    && connStatus.HasMsdbAccess)
                {
                    /* Live msdb read via the collector's connection path (async SqlClient, already
                       off the UI thread; MFA serialization / throttle / retry handled inside). */
                    var failedJobs = await _collectorService.GetRecentlyFailedJobsAsync(server, App.AlertFailedJobLookbackMinutes);

                    /* Drive the server tab badge — a failure in the lookback window (#749). Recorded
                       as a local; written once at the end of the sweep (#1128 review). */
                    curBadgeFailedJob = failedJobs.Count > 0;

                    if (failedJobs.Count > 0)
                    {
                        var newestFailure = failedJobs.Max(j => j.RunDateTime);
                        bool hasWatermark = _lastAlertedFailedJobTime.TryGetValue(key, out var lastFailure);
                        bool hasNewFailure = !hasWatermark || newestFailure > lastFailure;

                        if (hasNewFailure && !suppressPopups &&
                            (!_lastFailedJobAlert.TryGetValue(key, out var lastFailedAlert) || now - lastFailedAlert >= alertCooldown))
                        {
                            var mostRecent = failedJobs[0]; // ORDER BY run_datetime DESC
                            var jobNames = string.Join(", ", failedJobs.Select(j => j.JobName).Distinct().Take(3));

                            var muteCtx = new AlertMuteContext { ServerName = summary.DisplayName, MetricName = "Failed Agent Job", JobName = mostRecent.JobName };
                            bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
                            _lastFailedJobAlert[key] = now;
                            _lastAlertedFailedJobTime[key] = newestFailure;
                            /* Persist the watermark so the same failures don't re-fire on the first
                               post-restart sweep while they linger in the lookback window (#1145
                               parity). On-change only — only when a failed-job toast actually fires. */
                            await _alertHistoryStore.SaveFailedJobWatermarkAsync(summary.ServerId, newestFailure);

                            if (!isMuted)
                            {
                                _trayService.ShowSnoozableNotification(
                                    "Failed Agent Job",
                                    $"{summary.DisplayName}: {failedJobs.Count} job failure(s) — {jobNames}",
                                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning,
                                    summary.DisplayName,
                                    "Failed Agent Job",
                                    _muteRuleService);
                            }

                            var failedJobContext = AlertContextBuilders.BuildFailedJobContext(summary.DisplayName, failedJobs);
                            var detailText = AlertContextBuilders.ContextToDetailText(failedJobContext);

                            await _emailAlertService.TrySendAlertEmailAsync(
                                "Failed Agent Job",
                                summary.DisplayName,
                                $"{failedJobs.Count} job failure(s) in last {App.AlertFailedJobLookbackMinutes}m — {jobNames}",
                                $"last {App.AlertFailedJobLookbackMinutes}m",
                                summary.ServerId,
                                failedJobContext,
                                numericCurrentValue: failedJobs.Count,
                                numericThresholdValue: 0,
                                muted: isMuted,
                                detailText: detailText);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Alerts", $"Failed to check failed jobs for {summary.DisplayName}: {ex.Message}");
            }
        }

        /* #1128 review fix: write the badge's low-disk / failed-job flags ONCE per sweep from the
           values computed above. Doing it here (not only inside the feature-enabled / online / msdb
           branches) means a disabled feature or an offline server clears a previously-lit badge
           instead of leaving it stale. A false->true transition is a genuinely new condition, so it
           clears any acknowledgement — matching the Dashboard, whose IsWorseThanBaseline re-shows on
           a new disk/job condition. RefreshServerBadgeExtras re-renders once (no-op without a tab). */
        if (badgeServer != null)
        {
            _badgeLowDisk[badgeServer.Id] = curBadgeLowDisk;
            _badgeFailedJob[badgeServer.Id] = curBadgeFailedJob;
            if ((curBadgeLowDisk && !prevBadgeLowDisk) || (curBadgeFailedJob && !prevBadgeFailedJob))
                _alertStateService.ClearAcknowledgementForNewCondition(badgeServer.Id);
            RefreshServerBadgeExtras(badgeServer.Id);
        }
    }

        /* #1141: in Per-event mode, deliver one notification per distinct incident (capped at
           AlertPerEventMaxPerCycle, with a trailing "+N more" that still carries the remaining
           fingerprints) instead of one batched summary card. Falls back to the single summary send in
           Summary mode or when there are no incidents. The existing edge-triggered gating still decides
           whether to fire; this only shapes delivery. */
        private async Task SendDetectedAlertAsync(
            string metricName, string serverName, string summaryCurrentValue, string thresholdValue,
            int serverId, AlertContext? context, bool isMuted, string? summaryDetailText)
        {
            /* #1236: a per-server override (Manage Servers -> Edit) wins over the global delivery mode;
               null inherits App.AlertDeliveryMode. */
            var deliveryMode = AlertDeliveryModeResolver.Resolve(ResolveServerDeliveryOverride(serverId), App.AlertDeliveryMode);
            if (deliveryMode == AlertNotificationMode.PerEvent && context?.Incidents is { Count: > 0 })
            {
                foreach (var msg in PerEventNotification.Split(context, App.AlertPerEventMaxPerCycle))
                {
                    await _emailAlertService.TrySendAlertEmailAsync(
                        metricName, serverName, msg.CurrentValue, thresholdValue, serverId,
                        msg.Context, muted: isMuted, detailText: AlertContextBuilders.ContextToDetailText(msg.Context));
                }
                return;
            }

            await _emailAlertService.TrySendAlertEmailAsync(
                metricName, serverName, summaryCurrentValue, thresholdValue, serverId,
                context, muted: isMuted, detailText: summaryDetailText);
        }

        /* #1236: the per-server delivery-mode override for serverId, or null to inherit the global
           App.AlertDeliveryMode. serverId is the deterministic hash of the storage name (the same
           mapping the alert loop uses), so map it back to the ServerConnection to read its override. */
        private AlertNotificationMode? ResolveServerDeliveryOverride(int serverId) =>
            _serverManager.GetAllServers().FirstOrDefault(s =>
                RemoteCollectorService.GetDeterministicHashCode(RemoteCollectorService.GetServerNameForStorage(s)) == serverId)
                ?.AlertDeliveryModeOverride;

        /* Phase-5 slice B: thin fetch-and-delegate wrappers. The rendering bodies moved verbatim to
           the shared AlertContextBuilders (Lite's grouped rendering is canonical); the store reads
           moved behind the IAlertReadAdapter seam (which includes the XE→DMV blocking fallback). */
        private async Task<AlertContext?> BuildBlockingContextAsync(int serverId, string serverName)
        {
            try
            {
                if (_alertReadAdapter == null) return null;

                var events = await _alertReadAdapter.GetRecentBlockedProcessReportsAsync(serverId.ToString(), hoursBack: 1);
                return AlertContextBuilders.BuildBlockingContext(serverName, events, App.AlertExcludedDatabases);
            }
            catch (Exception ex)
            {
                AppLogger.Error("EmailAlert", $"Failed to fetch blocking detail for email: {ex.Message}");
                return null;
            }
        }

        private async Task<AlertContext?> BuildDeadlockContextAsync(int serverId, string serverName)
        {
            try
            {
                if (_alertReadAdapter == null) return null;

                var deadlocks = await _alertReadAdapter.GetRecentDeadlocksAsync(serverId.ToString(), hoursBack: 1);
                return AlertContextBuilders.BuildDeadlockContext(serverName, deadlocks, App.AlertExcludedDatabases);
            }
            catch (Exception ex)
            {
                AppLogger.Error("EmailAlert", $"Failed to fetch deadlock detail for email: {ex.Message}");
                return null;
            }
        }

        /* The eight pure alert-context builders (poison wait, long-running query, volume free space,
           tempdb space, anomalous job, failed job, blocking, deadlock) and their pure helpers
           (ContextToDetailText, TruncateText, GetBreachedVolumes, FormatLowDiskThreshold,
           IsDeadlockExcluded) live in the shared PerformanceMonitor.Alerting.AlertContextBuilders
           (Phase-5 slices A + B); the collected-store reads live behind LiteAlertReadAdapter. */
}
