/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// One per-minute bucket of blocking SEVERITY (the Blocking Stats sub-tab): the count of blocked-process
/// events in the bucket plus the total / max / avg blocking DURATION (ms) over them. The on-the-fly
/// Darling equivalent of the Dashboard's pre-aggregated <c>collect.blocking_deadlock_stats</c>
/// (blocking_event_count / total_blocking_duration_ms / max_blocking_duration_ms / avg_blocking_duration_ms)
/// — computed from the raw <c>blocked_process_reports</c> rows the store already keeps, so no collector or
/// schema change is needed. The duration source is each blocked process's <c>wait_time_ms</c> (bigint) —
/// the same column the Blocked Process Reports grid and the alert path read as the block's wait time.
/// </summary>
public sealed record BlockingDurationStatsPoint(
    DateTime Time, int EventCount, long TotalDurationMs, long MaxDurationMs, double AvgDurationMs);

public sealed partial class ViewerDataService
{
    /// <summary>
    /// Blocking-duration aggregate per minute — the SEVERITY companion to <see cref="BlockingTrendSql"/>'s
    /// count-only trend, built on the SAME XE-preferred / DMV-fallback source selection so the two
    /// reconcile exactly. XE blocked-process reports (<c>v_blocked_process_reports</c>) are the primary
    /// source, bucketed on <c>event_time</c>; the always-on DMV snapshot (<c>v_dmv_blocking_snapshots</c>)
    /// contributes only when the XE source has no rows in the window (<c>WHERE NOT EXISTS</c>), so a server
    /// with both sources never double-counts (identical exclusion to the count trend). Each bucket carries
    /// COUNT(*) events and the SUM / MAX / AVG of <c>wait_time_ms</c> (the per-blocked-process block
    /// duration). Postgres dialect notes mirror the sibling reads: COUNT(*) is <c>bigint</c> (read via
    /// GetInt64, narrowed to int); SUM of a bigint column is <c>numeric</c>, CAST back to <c>bigint</c> for
    /// the typed GetInt64 reader (the same adaptation the waiting-task read makes); AVG is <c>numeric</c>,
    /// CAST to <c>double precision</c> for GetDouble. $1 server_id, $2 window start, $3 window end (naive
    /// UTC).
    /// </summary>
    public const string BlockingDurationStatsSql = """
        WITH bpr AS (
            SELECT
                DATE_TRUNC('minute', event_time) AS bucket,
                COUNT(*) AS event_count,
                CAST(SUM(wait_time_ms) AS bigint) AS total_duration_ms,
                MAX(wait_time_ms) AS max_duration_ms,
                CAST(AVG(wait_time_ms) AS double precision) AS avg_duration_ms
            FROM v_blocked_process_reports
            WHERE server_id = $1 AND event_time >= $2 AND event_time <= $3
            GROUP BY DATE_TRUNC('minute', event_time)
        ),
        dmv AS (
            SELECT
                DATE_TRUNC('minute', event_time) AS bucket,
                COUNT(*) AS event_count,
                CAST(SUM(wait_time_ms) AS bigint) AS total_duration_ms,
                MAX(wait_time_ms) AS max_duration_ms,
                CAST(AVG(wait_time_ms) AS double precision) AS avg_duration_ms
            FROM v_dmv_blocking_snapshots
            WHERE server_id = $1 AND event_time >= $2 AND event_time <= $3
            GROUP BY DATE_TRUNC('minute', event_time)
        )
        SELECT bucket, event_count, total_duration_ms, max_duration_ms, avg_duration_ms FROM bpr
        UNION ALL
        SELECT bucket, event_count, total_duration_ms, max_duration_ms, avg_duration_ms FROM dmv WHERE NOT EXISTS (SELECT 1 FROM bpr)
        ORDER BY bucket
        """;

    /// <summary>
    /// Blocking-duration-severity buckets for one server over the window (Blocking Stats sub-tab): total
    /// events and total / max / avg block duration per minute, XE-preferred with the DMV-snapshot fallback
    /// so it reconciles with the blocking-incident count trend.
    /// </summary>
    public async Task<List<BlockingDurationStatsPoint>> GetBlockingDurationStatsAsync(
        int serverId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        var items = new List<BlockingDurationStatsPoint>();

        await using var command = _dataSource.CreateCommand(BlockingDurationStatsSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(startUtc, DateTimeKind.Unspecified),
        });
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(endUtc, DateTimeKind.Unspecified),
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new BlockingDurationStatsPoint(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? 0 : (int)reader.GetInt64(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                reader.IsDBNull(4) ? 0 : reader.GetDouble(4)));
        }

        return items;
    }
}
