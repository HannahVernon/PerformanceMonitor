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
/// watching a dashboard. Four conditions — the first three reframe a Dashboard health check onto Darling's
/// own signals; the fourth (Store Disk Pressure) is net-new and guards the service's OWN store — all routed
/// through the SAME <see cref="IAlertDeliverer"/> the shared alert engine uses (so they inherit its
/// email/webhook delivery, per-fingerprint delivery cooldown, and restart replay) and the SAME
/// <c>config_alert_log</c> history store:
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
/// <item><b>Store Disk Pressure</b> — the volume hosting the Darling store is nearly full. Unlike the
///   other three (per monitored server), this is a FLEET-level condition polled once per sweep from the
///   store itself (<c>pg_database_size</c> for context) and the store volume's free space: when a headless
///   service's disk fills, collection and every write stop for the WHOLE fleet, and nobody is watching. The
///   flagship-appropriate maintenance backstop the daily time-based purge otherwise lacks — deliberately
///   NOT Lite's 512MB archive-then-reset (Postgres has no single-file INSERT cliff, and a blanket reset
///   would nuke every tenant of the shared store).</item>
/// </list>
/// Each condition is EDGE-TRIGGERED (in-memory active flag + the shared alert cooldown for the polled
/// conditions; a per-server connection state machine for the connect edge) so it fires once on the
/// transition, not every sweep — exactly the Dashboard's <c>_activeXAlert</c>/<c>_lastXAlert</c> shape.
/// On recovery a "…Resumed"/"…Restored"/"…Resolved" row is written to alert history (closing the audit loop
/// the same way the engine's resolution callback now does — see <see cref="BuildResolutionRecord"/>).
/// Gated on the master <c>alerts.enabled</c> switch — plus, for the connect edge, the V20
/// <c>notify_connection_changes</c> toggle (Lite's <c>App.NotifyConnectionChanges</c> twin); the
/// collection-stopped / capture-down / disk-pressure thresholds stay sensible hardcoded defaults.
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

    /* Store Disk Pressure fires when the store volume drops below this percent free — a percentage (not an
       absolute floor) so it scales from a small managed box to a large fleet disk; 10% is the universal DBA
       "act now" threshold for a database volume, and mirrors the shared engine's target-server low-disk
       percent (LowDiskThresholdPercent). Percent-only by design (defaults over speculative config — a GB
       floor is a trivial follow-up if an operator ever wants one). The condition no-ops when free space is
       undeterminable (a remote BYO store), so it never false-alarms; the managed store's own volume is the
       case it exists to protect. */
    internal const double DiskFreeWarnPercent = 10.0;

    private readonly IAlertEngineSettings _settings;
    private readonly IAlertDeliverer _deliverer;
    private readonly IAlertHistoryStore _historyStore;
    private readonly Func<AlertMuteContext, bool> _isAlertMuted;
    private readonly ILogger? _logger;
    private readonly Func<DateTime> _utcNow;

    /* The connection-change notify gate (V20), read live so a store reload takes effect on the next connect
       edge. A Func rather than a settings-interface member because NotifyConnectionChanges is a Darling-specific
       concrete DarlingAlertSettings knob, not on the shared IAlertEngineSettings (the DeliveryMode precedent);
       the Func seam also keeps the test fakes — which implement only IAlertEngineSettings — untouched. Defaults
       to always-on when unsupplied (preserving the pre-V20 behavior). */
    private readonly Func<bool> _notifyConnectionChanges;

    /* Edge state, keyed by the engine's serverKey (the server_id as an invariant string — the same
       identity the deliverer/history/watermark stores use). In-memory only, exactly like the shared
       engine's active-condition flags; the restart replay protection is the deliverer's own
       history-seeded email/webhook cooldown, not these. */
    private readonly ConcurrentDictionary<string, bool> _activeCollectionStopped = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastCollectionStoppedAlert = new();
    private readonly ConcurrentDictionary<string, bool> _activeCaptureDown = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastCaptureDownAlert = new();
    private readonly ConcurrentDictionary<string, ConnectionState> _connectionState = new();

    /* Store Disk Pressure edge state. FLEET-level (one shared store, not per server), so it is keyed by a
       single fixed sentinel (DiskKey) rather than a serverId — never dropped by Forget (that is per-server).
       Dictionaries (not a plain bool) purely to reuse the same TryRemove-recovery + CooldownElapsed helpers
       the per-server conditions use. */
    private readonly ConcurrentDictionary<string, bool> _activeDiskPressure = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastDiskPressureAlert = new();

    /// <summary>The fixed key for the fleet-level Store Disk Pressure edge (not a real server).</summary>
    private const string DiskKey = "store";

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
        Func<DateTime>? utcNow = null,
        Func<bool>? notifyConnectionChanges = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _deliverer = deliverer ?? throw new ArgumentNullException(nameof(deliverer));
        _historyStore = historyStore ?? throw new ArgumentNullException(nameof(historyStore));
        _isAlertMuted = isAlertMuted ?? throw new ArgumentNullException(nameof(isAlertMuted));
        _logger = logger;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _notifyConnectionChanges = notifyConnectionChanges ?? (() => true);
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
    /// resumes from the correct baseline; only delivery is gated — on the master switch AND the V20
    /// connection-change notify toggle (<see cref="DarlingAlertSettings.NotifyConnectionChanges"/>).
    /// The delivery portion is failure-isolated (a throwing mute-check can't propagate out of the un-guarded
    /// sweep loop and stop the fleet); the state machine advances first, so isolation never corrupts the edge.
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

        /* Delivery is gated on the master switch AND the connection-change notify toggle (V20); the state
           machine above already advanced, so toggling either off then back on resumes from the correct
           baseline rather than replaying a stale edge — the same "track always, deliver conditionally"
           posture the master switch already had. */
        if (!_settings.AlertsEnabled || !_notifyConnectionChanges())
        {
            return;
        }

        /* Isolate the DELIVERY portion (the state machine already advanced above, so wrapping only the fire can
           never corrupt the edge). FireAsync's pre-deliver mute-check seam (_isAlertMuted → a mute rule's
           Matches()) is NOT internally isolated, and this method is called straight from the un-guarded sweep
           loop (DarlingWorker.TryConnectAsync) — whose OWN catch RE-CALLS this with online:false — so a throwing
           mute-check here would propagate out of that catch and stop collection for the whole fleet. Same
           isolation the sibling self-alerts use (EvaluateStoreAlertsAsync / EvaluateDiskPressureAsync).
           Cancellation still propagates. */
        try
        {
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("[{Server}] Connection-change self-alert delivery failed: {Message}", serverName, ex.Message);
        }
    }

    /* ---------------- store disk pressure (fleet-level, polled) ---------------- */

    /// <summary>
    /// Pure disk-pressure decision: the store volume is under pressure when its FREE space is below
    /// <see cref="DiskFreeWarnPercent"/> of the volume total. No I/O, so it pins directly. A non-positive
    /// total is treated as "can't tell" (false — the caller also guards this). The percentage scales across
    /// disk sizes; see the constant for why it is percent-only.
    /// </summary>
    internal static bool IsDiskPressure(long freeBytes, long totalBytes, out string reason)
    {
        if (totalBytes <= 0)
        {
            reason = "";
            return false;
        }

        double percentFree = (double)freeBytes / totalBytes * 100.0;
        if (percentFree < DiskFreeWarnPercent)
        {
            reason = $"The monitor store's disk volume has only {percentFree.ToString("0.#", CultureInfo.InvariantCulture)}% free ({FormatGb(freeBytes)} of {FormatGb(totalBytes)}).";
            return true;
        }

        reason = "";
        return false;
    }

    /// <summary>
    /// The isolating entry point the worker's disk-pressure sweep calls — the fleet-level twin of
    /// <see cref="EvaluateStoreAlertsAsync"/> for the store-polled conditions. Wraps
    /// <see cref="ApplyDiskPressureAsync"/> in the SAME failure isolation the sibling store-alerts use, so a
    /// throwing seam — most notably the pre-deliver mute check (<c>_isAlertMuted</c> → a mute rule's
    /// <c>Matches</c>), which unlike Deliver/RecordResolution is NOT internally isolated — can never propagate
    /// out of the (otherwise un-guarded) collection sweep loop and stop collection for the whole fleet.
    /// Cancellation still propagates.
    /// </summary>
    public async Task EvaluateDiskPressureAsync(
        long? freeBytes, long? totalBytes, long? storeSizeBytes, CancellationToken cancellationToken)
    {
        try
        {
            await ApplyDiskPressureAsync(freeBytes, totalBytes, storeSizeBytes, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogError("Store disk-pressure self-alert failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Edge-applies the fleet-level Store Disk Pressure condition from a store-volume sample: fire once on
    /// entry, re-fire only after the alert cooldown while it persists, and write ONE "Store Disk Pressure
    /// Resolved" history row on recovery (mirrors the per-server conditions' edge shape). Gated on the master
    /// alerts switch. NO-OPS when free/total are null — a remote BYO store whose volume the service can't see —
    /// so it never false-alarms; the managed store's own volume is what it exists to protect.
    /// <paramref name="storeSizeBytes"/> (pg_database_size) is context for the alert text only, never the
    /// trigger. Internal (tested directly, like the sibling Apply methods); the worker calls the isolating
    /// <see cref="EvaluateDiskPressureAsync"/>. Testable directly with a recording deliverer + a controllable clock.
    /// </summary>
    internal async Task ApplyDiskPressureAsync(
        long? freeBytes, long? totalBytes, long? storeSizeBytes, CancellationToken cancellationToken)
    {
        if (!_settings.AlertsEnabled)
        {
            return;
        }

        /* Can't determine the store volume's free space (remote BYO store, or the drive was not ready) — no
           signal, so neither fire nor clear a standing alert. */
        if (freeBytes is not long free || totalBytes is not long total || total <= 0)
        {
            return;
        }

        var now = _utcNow();
        bool pressure = IsDiskPressure(free, total, out var reason);

        if (pressure)
        {
            _activeDiskPressure[DiskKey] = true;
            if (CooldownElapsed(_lastDiskPressureAlert, DiskKey, now))
            {
                _lastDiskPressureAlert[DiskKey] = now;
                var storeText = storeSizeBytes is long size ? $" The store currently holds {FormatGb(size)}." : "";
                await FireAsync(
                    DiskKey, "Monitor Store", "Store Disk Pressure", reason,
                    $"{DiskFreeWarnPercent.ToString("0.#", CultureInfo.InvariantCulture)}% free",
                    detail: reason + storeText + " When the store volume fills, collection and every write stop " +
                        "for the WHOLE fleet, and a headless service has no dashboard to warn you. Free space on the " +
                        "store volume, shorten retention (config_collector_schedules), enable TimescaleDB compression, " +
                        "or move the store to a larger disk.",
                    severity: AlertSeverityLevel.Critical,
                    shortMessage: reason, cancellationToken);
            }
        }
        else if (_activeDiskPressure.TryRemove(DiskKey, out var was) && was)
        {
            await RecordResolutionAsync(new AlertResolution(
                DiskKey, "Monitor Store", "Store Disk Pressure",
                "Store Disk Pressure Resolved", "Monitor store volume free space recovered"), cancellationToken);
        }
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
AND   x.status = 'SESSION_MISSING'
ORDER BY x.collector_name", connection);
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

    /// <summary>Human-readable GiB for disk-pressure alert text (binary GiB, 2 dp).</summary>
    private static string FormatGb(long bytes) =>
        (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.##", CultureInfo.InvariantCulture) + " GB";
}
