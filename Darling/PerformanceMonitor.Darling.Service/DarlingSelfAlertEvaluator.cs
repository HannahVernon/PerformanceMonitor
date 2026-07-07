/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using PerformanceMonitor.Alerting;
using PerformanceMonitor.Notifications;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// Stage 4 of the Darling control plane — the SERVICE's self-alerts: the "is my collection actually
/// working" conditions that matter most for an unattended 24/7 headless service where nobody is
/// watching a dashboard. Three conditions, each a reframe of a Dashboard health check onto Darling's
/// own signals, all routed through the SAME <see cref="IAlertDeliverer"/> the shared alert engine
/// uses (so they inherit its email/webhook delivery, per-fingerprint delivery cooldown, and restart
/// replay) and the SAME <c>config_alert_log</c> history store:
/// <list type="number">
/// <item><b>Collection Stopped / collector failure</b> — the server's <c>collection_log</c> shows no
///   SUCCESS within a staleness window OR the last N runs all failed (reframes the Dashboard's
///   "Collection Stopped", <c>MainWindow.AlertEngine.cs</c> + <c>NocHealth.GetCollectionStoppedAsync</c>,
///   onto Darling's collection_log instead of msdb Agent-job state).</item>
/// <item><b>Server Unreachable / Restored</b> — fired on the connect edge in
///   <see cref="DarlingWorker"/>'s loop (online→offline and back), the headless twin of the
///   Dashboard's <c>NotifyOnConnectionLost</c>/<c>Restored</c> (<c>MainWindow.xaml.cs</c>). Uses the
///   Dashboard's exact metric names ("Server Unreachable" / "Server Restored") so the alert history
///   vocabulary and the shared <c>AlertSeverity</c> map (Critical / green RESOLVED) match cross-app.</item>
/// <item><b>Capture Down</b> — a missing/denied blocking-deadlock XE session, surfaced from the
///   <c>SESSION_MISSING</c> collection_log status the tolerant XE readers now write (reframes the
///   Dashboard's #1086 "Capture Down", <c>NocHealth.GetMissingCaptureSessionsAsync</c>).</item>
/// </list>
/// Each condition is EDGE-TRIGGERED (in-memory active flag + the shared alert cooldown for the polled
/// conditions; a per-server connection state machine for the connect edge) so it fires once on the
/// transition, not every sweep — exactly the Dashboard's <c>_activeXAlert</c>/<c>_lastXAlert</c> shape.
/// On recovery a "…Resumed"/"…Restored" row is written to alert history (closing the audit loop the
/// same way the engine's resolution callback now does — see <see cref="BuildResolutionRecord"/>).
/// Gated on the master <c>alerts.enabled</c> switch; thresholds are sensible hardcoded defaults
/// (defaults over speculative config — no new config knobs, no migration).
/// </summary>
internal sealed class DarlingSelfAlertEvaluator
{
    /* No successful collection within this window (a server that HAS collected before) reads as
       stopped — matches the Dashboard's CollectionStaleThresholdMinutes (NocHealth.cs). The frequent
       Darling collectors run every ~1 minute, so 30 minutes of no success is unambiguously dead; the
       connection-lost alert covers the fast path for an unreachable server, and the consecutive-failure
       check below covers the fast path for a server that is connected but erroring every collector. */
    internal static readonly TimeSpan StaleWindow = TimeSpan.FromMinutes(30);

    /* The last N logged runs all failing (no SUCCESS/SKIPPED among them) fires "Collection Stopped"
       faster than the staleness backstop when a connected server's collectors are erroring on every
       cycle. 10 spans a couple of minutes of total failure across the frequently-scheduled collectors. */
    internal const int ConsecutiveFailureThreshold = 10;

    private readonly IAlertEngineSettings _settings;
    private readonly IAlertDeliverer _deliverer;
    private readonly IAlertHistoryStore _historyStore;
    private readonly Func<AlertMuteContext, bool> _isAlertMuted;
    private readonly ILogger? _logger;
    private readonly Func<DateTime> _utcNow;

    /* Edge state, keyed by the engine's serverKey (the server_id as an invariant string — the same
       identity the deliverer/history/watermark stores use). In-memory only, exactly like the shared
       engine's active-condition flags; the restart replay protection is the deliverer's own
       history-seeded email/webhook cooldown, not these. */
    private readonly ConcurrentDictionary<string, bool> _activeCollectionStopped = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastCollectionStoppedAlert = new();
    private readonly ConcurrentDictionary<string, bool> _activeCaptureDown = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastCaptureDownAlert = new();
    private readonly ConcurrentDictionary<string, ConnectionState> _connectionState = new();

    /* Whether the service has successfully connected to this server at least once THIS process-run. Guards
       collection-stopped: unlike the Dashboard (whose target-side collection_log keeps filling regardless of
       the app), Darling IS the collector, so the service's own downtime makes collection_log stale. Without
       this guard a service restart after >30 min of downtime would false-alarm "Collection Stopped" on a
       perfectly healthy server before its first fresh collection lands. Gating on a prior successful connect
       makes collection-stopped a clean "was collecting, then stopped" transition (the same philosophy as the
       connection-lost edge and the Dashboard's skip-first-check) rather than a judgement on pre-restart data. */
    private readonly ConcurrentDictionary<string, bool> _hasBeenOnline = new();

    public DarlingSelfAlertEvaluator(
        IAlertEngineSettings settings,
        IAlertDeliverer deliverer,
        IAlertHistoryStore historyStore,
        Func<AlertMuteContext, bool> isAlertMuted,
        ILogger? logger = null,
        Func<DateTime>? utcNow = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _deliverer = deliverer ?? throw new ArgumentNullException(nameof(deliverer));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _isAlertMuted = isAlertMuted ?? throw new ArgumentNullException(nameof(isAlertMuted));
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    private enum ConnectionState
    {
        /* Never yet observed — the baseline. Unknown→online/offline never fires (mirrors the
           Dashboard's "skip the first check" so a server that is simply down at startup does not
           page; only a transition FROM a known state does). */
        Unknown,
        Online,
        Offline
    }

    /* ---------------- store-polled self-alerts (collection-stopped + capture-down) ---------------- */

    /// <summary>
    /// Evaluates the store-polled self-alerts for one server from its <c>collection_log</c>. Gated on the
    /// master alerts switch (mirrors the engine's early return). Collection-stopped runs for EVERY server
    /// whether or not it is currently connected (an unreachable server has stopped collecting — that is
    /// exactly the case to catch); capture-down runs only for a connected server (its XE collectors only
    /// run then). Failure-isolated per condition so a bad store read never breaks the loop.
    /// </summary>
    public async Task EvaluateStoreAlertsAsync(
        NpgsqlDataSource postgres, int serverId, string serverName, bool connected, CancellationToken cancellationToken)
    {
        if (!_settings.AlertsEnabled)
        {
            return;
        }

        /* Only judge collection-stopped once the service has actually collected from this server this run
           (see _hasBeenOnline) — otherwise pre-restart / pre-re-add stale rows would false-alarm before the
           first fresh collection lands. */
        if (_hasBeenOnline.ContainsKey(Key(serverId)))
        {
            try
            {
                var (lastSuccess, recentRuns, recentSuccess) =
                    await ReadCollectionSignalsAsync(postgres, serverId, ConsecutiveFailureThreshold, cancellationToken);
                bool stopped = IsCollectionStopped(lastSuccess, recentRuns, recentSuccess, _utcNow(), out var reason);
                await ApplyCollectionStoppedAsync(serverId, serverName, stopped, reason, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.LogError("[{Server}] Collection-health self-alert failed: {Message}", serverName, ex.Message);
            }
        }

        if (!connected)
        {
            return;
        }

        try
        {
            var missing = await ReadMissingCaptureSessionsAsync(postgres, serverId, cancellationToken);
            await ApplyCaptureDownAsync(serverId, serverName, missing, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("[{Server}] Capture-down self-alert failed: {Message}", serverName, ex.Message);
        }
    }

    /// <summary>
    /// Pure collection-stopped decision from the three store signals — no I/O, so it pins directly.
    /// A NEVER-succeeded server (<paramref name="lastSuccessUtc"/> null) is deliberately NOT flagged by
    /// the staleness backstop: a freshly-added or never-connected server must not be misread as "stopped"
    /// (the connection-lost alert covers that). It IS flagged by the consecutive-failure path if it has
    /// actually run and failed N times.
    /// </summary>
    internal static bool IsCollectionStopped(
        DateTime? lastSuccessUtc, int recentRunCount, int recentSuccessCount, DateTime nowUtc, out string reason)
    {
        /* Fast path: the most-recent N runs all failed. */
        if (recentRunCount >= ConsecutiveFailureThreshold && recentSuccessCount == 0)
        {
            reason = $"The last {recentRunCount.ToString(CultureInfo.InvariantCulture)} collector runs all failed — no data is landing.";
            return true;
        }

        /* Backstop: a server that HAS collected before but hasn't succeeded within the staleness window. */
        if (lastSuccessUtc.HasValue && nowUtc - lastSuccessUtc.Value >= StaleWindow)
        {
            int minutes = (int)(nowUtc - lastSuccessUtc.Value).TotalMinutes;
            reason = $"No successful collection in {minutes.ToString(CultureInfo.InvariantCulture)} minutes — the collectors are failing or the server is unreachable.";
            return true;
        }

        reason = "";
        return false;
    }

    /// <summary>
    /// Edge-applies the collection-stopped decision (mirrors the Dashboard's
    /// <c>_activeCollectionStoppedAlert</c>/<c>_lastCollectionStoppedAlert</c>): fire once on entry, re-fire
    /// only after the alert cooldown while it persists, and write ONE "Collection Resumed" history row on
    /// recovery. Testable directly with a recording deliverer + a controllable clock.
    /// </summary>
    internal async Task ApplyCollectionStoppedAsync(
        int serverId, string serverName, bool stopped, string reason, CancellationToken cancellationToken)
    {
        var key = Key(serverId);
        var now = _utcNow();

        if (stopped)
        {
            _activeCollectionStopped[key] = true;
            if (CooldownElapsed(_lastCollectionStoppedAlert, key, now))
            {
                _lastCollectionStoppedAlert[key] = now;
                await FireAsync(
                    key, serverName, "Collection Stopped", reason, "collecting",
                    detail: reason + " A headless service has no dashboard to watch, so this is the primary " +
                        "signal that a server's data has gone stale. Check the service log and the server's " +
                        "reachability, credentials, and collector permissions.",
                    severity: AlertSeverityLevel.Critical,
                    shortMessage: reason, cancellationToken);
            }
        }
        else if (_activeCollectionStopped.TryRemove(key, out var was) && was)
        {
            await RecordResolutionAsync(new AlertResolution(
                key, serverName, "Collection Stopped",
                "Collection Resumed", $"{serverName}: Data collection is running again"), cancellationToken);
        }
    }

    /// <summary>
    /// Edge-applies capture-down (mirrors the Dashboard's <c>_activeCaptureDownAlert</c>): gated on
    /// blocking OR deadlock alerts being enabled (the alerts this protects — if the operator wants those,
    /// they need to know when the data feeding them stops existing). Fire once on entry, re-fire only after
    /// the cooldown, write ONE "Capture Restored" row on recovery.
    /// </summary>
    internal async Task ApplyCaptureDownAsync(
        int serverId, string serverName, IReadOnlyList<string> missing, CancellationToken cancellationToken)
    {
        if (!_settings.BlockingEnabled && !_settings.DeadlockEnabled)
        {
            return;
        }

        var key = Key(serverId);
        var now = _utcNow();

        if (missing.Count > 0)
        {
            _activeCaptureDown[key] = true;
            if (CooldownElapsed(_lastCaptureDownAlert, key, now))
            {
                _lastCaptureDownAlert[key] = now;
                var list = string.Join(" and ", missing);
                await FireAsync(
                    key, serverName, "Capture Down", list, "session running",
                    detail: $"The {list} Extended Events session(s) are missing and could not be created. " +
                        "Blocking/deadlock data is NOT being captured, so those alerts can never fire. Check the " +
                        "collection log for the SESSION_MISSING detail (usually a permissions problem: " +
                        "ALTER ANY EVENT SESSION on-prem, CREATE ANY DATABASE EVENT SESSION on Azure SQL DB).",
                    severity: AlertSeverityLevel.Critical,
                    shortMessage: $"{list} capture is not running — XE session missing", cancellationToken);
            }
        }
        else if (_activeCaptureDown.TryRemove(key, out var was) && was)
        {
            await RecordResolutionAsync(new AlertResolution(
                key, serverName, "Capture Down",
                "Capture Restored", $"{serverName}: Blocking/deadlock capture is running again"), cancellationToken);
        }
    }

    /* ---------------- connection lost / restored (connect-edge driven) ---------------- */

    /// <summary>
    /// Applies a connect-attempt outcome for one server and fires the connection edge. Called from the
    /// loop's <see cref="DarlingWorker.TryConnectAsync"/> success (online) and failure (offline) branches.
    /// The per-server state machine fires "Server Unreachable" only on a genuine online→offline transition
    /// and "Server Restored" only on offline→online — a repeated failed reconnect (offline→offline) does
    /// NOT re-fire, and the first-ever outcome (Unknown→online/offline) is a silent baseline, mirroring the
    /// Dashboard's skip-first-check. Both edges are FULL alerts (email/webhook, Dashboard parity — the
    /// restore is not a silent resolution). State is tracked even while alerts are disabled so re-enabling
    /// resumes from the correct baseline; only delivery is gated on the master switch.
    /// </summary>
    public async Task ApplyConnectionOutcomeAsync(
        int serverId, string serverName, bool online, string? error, CancellationToken cancellationToken)
    {
        var key = Key(serverId);
        var previous = _connectionState.TryGetValue(key, out var s) ? s : ConnectionState.Unknown;
        _connectionState[key] = online ? ConnectionState.Online : ConnectionState.Offline;

        /* Record that collection is now possible for this server this run — arms the collection-stopped
           check (tracked regardless of the alerts switch, so re-enabling has a correct baseline). */
        if (online)
        {
            _hasBeenOnline[key] = true;
        }

        if (!_settings.AlertsEnabled)
        {
            return;
        }

        if (online && previous == ConnectionState.Offline)
        {
            /* Severity null → the shared AlertSeverity map renders "Server Restored" green/RESOLVED. */
            await FireAsync(
                key, serverName, "Server Restored", "Online", "Online",
                detail: $"{serverName}: connection restored",
                severity: null,
                shortMessage: "connection restored", cancellationToken);
        }
        else if (!online && previous == ConnectionState.Online)
        {
            var reason = string.IsNullOrWhiteSpace(error) ? "Connection failed" : error!;
            await FireAsync(
                key, serverName, "Server Unreachable", reason, "Online",
                detail: reason,
                severity: AlertSeverityLevel.Critical,
                shortMessage: reason, cancellationToken);
        }
        /* previous == Unknown → baseline only (no fire). */
    }

    /// <summary>Drops all edge state for a server removed from the monitored set (reconcile), so a later
    /// re-add starts fresh at the Unknown baseline rather than inheriting a stale connection/active flag.</summary>
    public void Forget(int serverId)
    {
        var key = Key(serverId);
        _activeCollectionStopped.TryRemove(key, out _);
        _lastCollectionStoppedAlert.TryRemove(key, out _);
        _activeCaptureDown.TryRemove(key, out _);
        _lastCaptureDownAlert.TryRemove(key, out _);
        _connectionState.TryRemove(key, out _);
        _hasBeenOnline.TryRemove(key, out _);
    }

    /* ---------------- store reads ---------------- */

    /// <summary>
    /// One round trip for the collection-stopped signals: the newest SUCCESS/SKIPPED time across all of the
    /// server's collectors (a SKIPPED is a healthy no-op, matching the viewer's GetCollectionHealthAsync),
    /// plus — over the most recent <paramref name="recentWindow"/> logged runs — the run count and the
    /// success count (feeding the consecutive-failure fast path). Static + parameterized so the gated live
    /// test can seed rows and assert the raw signals directly.
    /// </summary>
    internal static async Task<(DateTime? LastSuccessUtc, int RecentRunCount, int RecentSuccessCount)> ReadCollectionSignalsAsync(
        NpgsqlDataSource postgres, int serverId, int recentWindow, CancellationToken cancellationToken)
    {
        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        using var command = new NpgsqlCommand(@"
SELECT
    (SELECT MAX(collection_time)
     FROM collection_log
     WHERE server_id = $1
     AND   status IN ('SUCCESS', 'SKIPPED'))                                        AS last_success,
    (SELECT COUNT(*)
     FROM (SELECT log_id FROM collection_log WHERE server_id = $1 ORDER BY log_id DESC LIMIT $2) r) AS recent_runs,
    (SELECT COUNT(*)
     FROM (SELECT status FROM collection_log WHERE server_id = $1 ORDER BY log_id DESC LIMIT $2) r
     WHERE r.status IN ('SUCCESS', 'SKIPPED'))                                       AS recent_success", connection);
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(recentWindow);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (null, 0, 0);
        }

        DateTime? lastSuccess = reader.IsDBNull(0)
            ? null
            : DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc);
        int recentRuns = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1), CultureInfo.InvariantCulture);
        int recentSuccess = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture);
        return (lastSuccess, recentRuns, recentSuccess);
    }

    /// <summary>
    /// The blocking/deadlock XE collectors whose LATEST run logged <c>SESSION_MISSING</c> — the session is
    /// absent and couldn't be created, so capture is non-functional even though the tolerant reader "succeeds"
    /// with zero rows. The Darling twin of the Dashboard's <c>GetMissingCaptureSessionsAsync</c>, on Darling's
    /// collector names. Returns the friendly capture names ("Blocking" / "Deadlock").
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ReadMissingCaptureSessionsAsync(
        NpgsqlDataSource postgres, int serverId, CancellationToken cancellationToken)
    {
        var missing = new List<string>();

        await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
        using var command = new NpgsqlCommand(@"
SELECT x.collector_name
FROM
(
    SELECT
        cl.collector_name,
        cl.status,
        ROW_NUMBER() OVER (PARTITION BY cl.collector_name ORDER BY cl.log_id DESC) AS n
    FROM collection_log AS cl
    WHERE cl.server_id = $1
    AND   cl.collector_name IN ('deadlocks', 'blocked_process_report')
) AS x
WHERE x.n = 1
AND   x.status = 'SESSION_MISSING'", connection);
        command.Parameters.AddWithValue(serverId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var collectorName = reader.GetString(0);
            missing.Add(collectorName == "deadlocks" ? "Deadlock" : "Blocking");
        }

        return missing;
    }

    /* ---------------- shared helpers ---------------- */

    /// <summary>
    /// Builds a resolved-flavored history row for a recovered condition — the Darling twin of the
    /// Dashboard's explicit "…Cleared/Resolved/Restored" <c>RecordAlert</c> rows. Used by BOTH this
    /// evaluator's self-alert recoveries (Collection Resumed / Capture Restored) and the shared alert
    /// engine's resolution callback (CPU Resolved, Blocking Cleared, …) so an operator reviewing alert
    /// history sees the paired "Detected" then "Cleared" entries. Never email/webhook (a resolution has no
    /// send channel — the deliverer's fire path is untouched): recorded as a delivered tray/history row.
    /// </summary>
    public static AlertHistoryRecord BuildResolutionRecord(AlertResolution resolution) => new(
        resolution.ServerKey, resolution.ServerName, resolution.Title,
        CurrentValueText: "resolved", ThresholdValueText: "",
        NumericCurrentValue: null, NumericThresholdValue: null,
        AlertSent: true, NotificationType: "tray", SendError: null,
        Muted: false, DetailText: resolution.Message, ContextJson: null);

    private async Task FireAsync(
        string serverKey, string serverName, string metricName, string currentValue, string thresholdValue,
        string detail, AlertSeverityLevel? severity, string shortMessage, CancellationToken cancellationToken)
    {
        /* Same mute treatment as the engine: a muted self-alert is still recorded (flagged muted) but its
           channels are skipped — the deliverer honors AlertOutcome.Muted. */
        bool muted = _isAlertMuted(new AlertMuteContext { ServerName = serverName, MetricName = metricName });

        await _deliverer.DeliverAsync(new AlertOutcome(
            serverKey, serverName, metricName, currentValue, thresholdValue,
            Context: null, DetailText: detail,
            NumericCurrentValue: null, NumericThresholdValue: null,
            Muted: muted, Severity: severity, ShortMessage: shortMessage), cancellationToken);
    }

    private async Task RecordResolutionAsync(AlertResolution resolution, CancellationToken cancellationToken)
    {
        _logger?.LogInformation("[{Server}] {Title}: {Message}",
            resolution.ServerName, resolution.Title, resolution.Message);
        try
        {
            await _historyStore.RecordAlertAsync(BuildResolutionRecord(resolution));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            /* An audit-row write must never break the loop (RecordAlertAsync is already failure-isolated;
               this is belt-and-suspenders for any other IAlertHistoryStore). */
            _logger?.LogError("Failed to record resolution '{Title}': {Message}", resolution.Title, ex.Message);
        }
    }

    private bool CooldownElapsed(ConcurrentDictionary<string, DateTime> lastFired, string key, DateTime now) =>
        !lastFired.TryGetValue(key, out var last)
        || now - last >= TimeSpan.FromMinutes(_settings.CooldownMinutes);

    private static string Key(int serverId) => serverId.ToString(CultureInfo.InvariantCulture);
}
