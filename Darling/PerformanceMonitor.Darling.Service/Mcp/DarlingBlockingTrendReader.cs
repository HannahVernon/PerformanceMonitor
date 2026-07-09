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

namespace PerformanceMonitor.Darling.Service.Mcp;

/// <summary>
/// Service-side reads for the blocking-incident and deadlock per-minute trend MCP tools
/// (<see cref="DarlingMcpBlockingTools"/> get_blocking_trend / get_deadlock_trend). The SQL is reproduced
/// verbatim from the viewer's Blocking-Trends charts (<c>ViewerDataService.BlockingTrends.cs</c>), which are
/// Lite's <c>GetBlockingTrendAsync</c> / <c>GetDeadlockTrendAsync</c> ported to Postgres. Both are STORED
/// reads (no live monitored-server hit) sharing a <c>(bucket timestamp, COUNT(*))</c> shape, so one reader
/// maps both; COUNT(*) is <c>bigint</c> in Postgres, read via GetInt64 and narrowed to the point's int.
/// Public-const SQL so Darling.Tests pin the dialect (the XE-preferred + DMV-fallback union, the deadlock
/// bucket-on-deadlock_time) without a live Postgres.
/// </summary>
internal static class DarlingBlockingTrendReader
{
    /// <summary>One incident-count-per-minute bucket (mirror of the viewer's <c>BlockingTrendPoint</c>).</summary>
    public sealed record BlockingTrendReadPoint(DateTime Time, int Count);

    /// <summary>
    /// Blocking-incident count per minute — the viewer's <c>BlockingTrendSql</c>. XE blocked-process reports
    /// (<c>v_blocked_process_reports</c>) are the primary source, bucketed on <c>event_time</c>; the always-on
    /// DMV snapshot (<c>v_dmv_blocking_snapshots</c>) is appended only when the XE source has no rows in the
    /// window (<c>WHERE NOT EXISTS</c>), so a server with both sources never double-counts. $1 server_id,
    /// $2 window start, $3 window end (naive UTC).
    /// </summary>
    public const string BlockingTrendSql = """
        WITH bpr AS (
            SELECT DATE_TRUNC('minute', event_time) AS bucket, COUNT(*) AS incident_count
            FROM v_blocked_process_reports
            WHERE server_id = $1 AND event_time >= $2 AND event_time <= $3
            GROUP BY DATE_TRUNC('minute', event_time)
        ),
        dmv AS (
            SELECT DATE_TRUNC('minute', event_time) AS bucket, COUNT(*) AS incident_count
            FROM v_dmv_blocking_snapshots
            WHERE server_id = $1 AND event_time >= $2 AND event_time <= $3
            GROUP BY DATE_TRUNC('minute', event_time)
        )
        SELECT bucket, incident_count FROM bpr
        UNION ALL
        SELECT bucket, incident_count FROM dmv WHERE NOT EXISTS (SELECT 1 FROM bpr)
        ORDER BY bucket
        """;

    /// <summary>
    /// Deadlock count per minute — the viewer's <c>DeadlockTrendSql</c>. Buckets on the deadlock's own
    /// <c>deadlock_time</c> while windowing on the collection prefix. Reads <c>v_deadlocks</c>. $1 server_id,
    /// $2 window start, $3 window end (naive UTC).
    /// </summary>
    public const string DeadlockTrendSql = """
        SELECT
            bucket,
            deadlock_count
        FROM (
            SELECT
                DATE_TRUNC('minute', deadlock_time) AS bucket,
                COUNT(*) AS deadlock_count
            FROM v_deadlocks
            WHERE server_id = $1
            AND   collection_time >= $2
            AND   collection_time <= $3
            GROUP BY DATE_TRUNC('minute', deadlock_time)
        ) sub
        ORDER BY bucket
        """;

    /// <summary>Blocking-incident-per-minute buckets for one server over the window.</summary>
    public static Task<List<BlockingTrendReadPoint>> GetBlockingTrendAsync(
        NpgsqlDataSource postgres, int serverId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
        => ReadCountTrendAsync(postgres, BlockingTrendSql, serverId, startUtc, endUtc, cancellationToken);

    /// <summary>Deadlock-per-minute buckets for one server over the window.</summary>
    public static Task<List<BlockingTrendReadPoint>> GetDeadlockTrendAsync(
        NpgsqlDataSource postgres, int serverId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
        => ReadCountTrendAsync(postgres, DeadlockTrendSql, serverId, startUtc, endUtc, cancellationToken);

    /// <summary>The blocking and deadlock trends share a (bucket timestamp, COUNT(*)) shape, so one reader
    /// maps both. COUNT(*) is bigint in Postgres, read via GetInt64 and narrowed to the point's int.</summary>
    private static async Task<List<BlockingTrendReadPoint>> ReadCountTrendAsync(
        NpgsqlDataSource postgres, string sql, int serverId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken)
    {
        var items = new List<BlockingTrendReadPoint>();
        await using var command = postgres.CreateCommand(sql);
        DarlingMcpReadParameters.AddWindow(command, serverId, startUtc, endUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new BlockingTrendReadPoint(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? 0 : (int)reader.GetInt64(1)));
        }

        return items;
    }
}
