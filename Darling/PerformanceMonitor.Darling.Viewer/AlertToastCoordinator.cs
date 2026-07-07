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

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// Decides which polled alert-history rows become in-app tray toasts — the headless viewer's stand-in for
/// Lite's in-process alert engine, which fires a toast at the moment it detects a condition. The viewer has
/// no engine: alerts already fired SERVICE-side (written to <c>config_alert_log</c> and delivered by
/// email/Teams/Slack), so the MainWindow re-reads recent history on a timer and this coordinator turns that
/// stream into non-duplicated, non-spammy toasts. Pure and clock-injected so the two load-bearing seams —
/// the "new since last-seen" watermark and the per-condition cooldown gate — are unit-testable without WPF,
/// Postgres, or a real clock.
///
/// <para>Two-stage gate, per polled row:</para>
/// <list type="number">
/// <item><b>Seen-once watermark.</b> Each distinct alert row is identified by the same triple the dismiss
/// path keys on — (alert_time, server_id, metric_name) — so a row that has already been offered as a toast
/// (or was present at startup, via <see cref="Prime"/>) is never re-toasted, even though the poll re-reads
/// an overlapping window each cycle.</item>
/// <item><b>Per-condition cooldown.</b> A genuinely-new row still toasts only if the same condition —
/// (server_id, metric_name) — has not toasted within the cooldown window, so a recurring condition the
/// service logs every cycle (e.g. High CPU) nudges at most once per cooldown instead of storming the tray.
/// Mirrors the spirit of the shared <c>IncidentCooldown</c> keyed on a fingerprint.</item>
/// </list>
/// <para>Muted rows never toast (Lite parity: the service still logs a muted row, flagged, with channels
/// skipped). Bookkeeping is pruned each call so neither map grows without bound over a long-lived viewer.</para>
/// </summary>
public sealed class AlertToastCoordinator
{
    /// <summary>How long a seen-row key is retained. Must be at least the poll fetch window so a row still
    /// inside that window is never pruned early and re-toasted.</summary>
    private readonly TimeSpan _seenRetention;

    /// <summary>rowKey (alert_time|server_id|metric) → the row's alert_time, for seen-once + pruning.</summary>
    private readonly Dictionary<string, DateTime> _seen = new(StringComparer.Ordinal);

    /// <summary>condKey (server_id|metric) → the last toast's nowUtc, for the cooldown gate + pruning.</summary>
    private readonly Dictionary<string, DateTime> _lastToast = new(StringComparer.Ordinal);

    /// <param name="seenRetention">
    /// How long to remember a row so the overlapping poll window doesn't re-toast it. Pass at least the
    /// poll fetch window (the MainWindow passes fetch-window + a margin).
    /// </param>
    public AlertToastCoordinator(TimeSpan seenRetention)
    {
        if (seenRetention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(seenRetention), "Retention must be positive.");
        }

        _seenRetention = seenRetention;
    }

    /// <summary>The distinct-row identity (matches the dismiss key: alert_time + server_id + metric_name).</summary>
    private static string RowKey(ViewerAlertRow row) =>
        string.Concat(row.AlertTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), "|",
            row.ServerId.ToString(System.Globalization.CultureInfo.InvariantCulture), "|", row.MetricName);

    /// <summary>The condition identity the cooldown throttles (server + metric).</summary>
    private static string ConditionKey(ViewerAlertRow row) =>
        string.Concat(row.ServerId.ToString(System.Globalization.CultureInfo.InvariantCulture), "|", row.MetricName);

    /// <summary>
    /// Seeds the seen-set from rows that already existed when the viewer opened, so startup does NOT replay
    /// the whole alert history as a toast storm — only alerts that arrive AFTER priming can toast. Does not
    /// arm the cooldown (a post-startup recurrence of a primed condition is free to toast once).
    /// </summary>
    public void Prime(IEnumerable<ViewerAlertRow> existingRows)
    {
        ArgumentNullException.ThrowIfNull(existingRows);

        foreach (var row in existingRows)
        {
            _seen[RowKey(row)] = row.AlertTime;
        }
    }

    /// <summary>
    /// Filters a freshly-polled batch to the rows that should toast now, updating the seen-set and cooldown
    /// state. Rows are considered oldest-first so, within a burst that shares a condition, the EARLIEST row
    /// wins the cooldown slot (the rest are suppressed until the window elapses). A row is emitted only when
    /// it is new (not seen/primed), not muted, and its condition is outside the cooldown window; every
    /// processed row is marked seen regardless of outcome so it is considered exactly once.
    /// </summary>
    /// <param name="polledRows">The latest read of recent, non-dismissed alert rows (any order).</param>
    /// <param name="nowUtc">The current time (injected for testability).</param>
    /// <param name="cooldown">
    /// The per-condition cooldown window (the "Tray notification cooldown" setting). <see cref="TimeSpan.Zero"/>
    /// or negative disables the cooldown so every new, unmuted row toasts.
    /// </param>
    public IReadOnlyList<ViewerAlertRow> SelectToasts(
        IEnumerable<ViewerAlertRow> polledRows, DateTime nowUtc, TimeSpan cooldown)
    {
        ArgumentNullException.ThrowIfNull(polledRows);

        var toasts = new List<ViewerAlertRow>();

        foreach (var row in polledRows.OrderBy(r => r.AlertTime))
        {
            var rowKey = RowKey(row);
            if (_seen.ContainsKey(rowKey))
            {
                continue; /* already offered (or primed at startup) — never re-toast */
            }

            _seen[rowKey] = row.AlertTime; /* processed exactly once, whatever we decide below */

            if (row.Muted)
            {
                continue; /* Lite parity: a muted row is logged but never toasted */
            }

            var conditionKey = ConditionKey(row);
            if (cooldown > TimeSpan.Zero
                && _lastToast.TryGetValue(conditionKey, out var last)
                && nowUtc - last < cooldown)
            {
                continue; /* same condition toasted too recently */
            }

            _lastToast[conditionKey] = nowUtc;
            toasts.Add(row);
        }

        Prune(nowUtc, cooldown);
        return toasts;
    }

    /// <summary>
    /// Drops bookkeeping that can no longer affect a decision: a seen row older than the retention window
    /// (the poll can never re-read it) and a cooldown entry older than the effective cooldown window.
    /// </summary>
    private void Prune(DateTime nowUtc, TimeSpan cooldown)
    {
        var seenCutoff = nowUtc - _seenRetention;
        foreach (var key in _seen.Where(kv => kv.Value < seenCutoff).Select(kv => kv.Key).ToList())
        {
            _seen.Remove(key);
        }

        /* Keep a cooldown entry until it can no longer block anything — retain for the longer of the
           cooldown window and the seen-retention so a slow poll can't forget an active cooldown early. */
        var cooldownWindow = cooldown > _seenRetention ? cooldown : _seenRetention;
        var toastCutoff = nowUtc - cooldownWindow;
        foreach (var key in _lastToast.Where(kv => kv.Value < toastCutoff).Select(kv => kv.Key).ToList())
        {
            _lastToast.Remove(key);
        }
    }
}
