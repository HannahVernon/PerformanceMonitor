/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The per-server "needs attention" summary the badge derivation produces for one server from the polled
/// alert-history rows: how many actionable alerts are active, whether any is critical (drives the badge color),
/// the newest alert instant (feeds the ack-until-worse clear in <see cref="ViewerAlertStateService"/>), and a
/// human breakdown for the tooltip. A <c>default</c> value (all zero/false/null) is the "no attention" state,
/// so a server absent from the derivation dictionary reads as quiet without a special case.
/// </summary>
public readonly record struct ServerAttentionState(
    int ServerId,
    int AlertCount,
    bool IsCritical,
    DateTime? LatestEventUtc,
    string Summary);

/// <summary>
/// Turns a flat batch of polled <see cref="ViewerAlertRow"/> (the same <c>config_alert_log</c> read that feeds
/// the tray toasts) into per-server attention state for the sidebar + tab badges — the headless viewer's
/// stand-in for Lite's per-tab badge counts. Pure and clock-free so the grouping/filter rules are unit-testable
/// without WPF or Postgres.
///
/// <para>Two rows never light a badge, matching the toast path and Lite's silence semantics:</para>
/// <list type="bullet">
/// <item><b>Muted rows.</b> A server the operator silenced (a store-side whole-server mute rule) logs its later
/// alerts flagged <c>muted</c>; excluding them here is how the viewer mirrors Lite's "a silenced server shows no
/// badge" — no separate viewer-local silence flag needed.</item>
/// <item><b>Resolution notices.</b> A "&#8230; Cleared / Resolved / Restored" row (<see cref="ViewerAlertRow.IsResolved"/>,
/// via the shared <c>AlertMetricClassifier</c>) is good news, not an actionable alert, so it never counts toward
/// the badge.</item>
/// </list>
/// </summary>
public static class ServerAttentionDeriver
{
    /// <summary>
    /// Groups the actionable (non-muted, non-resolved) rows by server and summarizes each. Servers with no
    /// actionable rows are simply absent from the result (their attention state is <c>default</c>).
    /// </summary>
    public static Dictionary<int, ServerAttentionState> Derive(IEnumerable<ViewerAlertRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var result = new Dictionary<int, ServerAttentionState>();

        foreach (var group in rows
            .Where(r => !r.Muted && !r.IsResolved)
            .GroupBy(r => r.ServerId))
        {
            var list = group.ToList();
            result[group.Key] = new ServerAttentionState(
                ServerId: group.Key,
                AlertCount: list.Count,
                IsCritical: list.Any(r => r.IsCritical),
                LatestEventUtc: list.Max(r => r.AlertTime),
                Summary: BuildSummary(list));
        }

        return result;
    }

    /// <summary>
    /// A compact metric breakdown for the badge tooltip, most-frequent first — e.g.
    /// <c>"Blocking Detected ×2, High CPU, Deadlocks Detected"</c>.
    /// </summary>
    private static string BuildSummary(IReadOnlyList<ViewerAlertRow> rows)
    {
        return string.Join(", ",
            rows.GroupBy(r => r.MetricName)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => g.Count() > 1
                    ? string.Create(CultureInfo.InvariantCulture, $"{g.Key} ×{g.Count()}")
                    : g.Key));
    }
}
