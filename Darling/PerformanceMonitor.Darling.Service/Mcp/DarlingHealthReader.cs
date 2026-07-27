/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Common;

namespace PerformanceMonitor.Darling.Service.Mcp;

/// <summary>
/// Service-side reads for the health MCP tools (<see cref="DarlingMcpHealthTools"/>) — the one-shot per-server
/// summary (get_server_summary) and the daily health rollup (get_daily_summary). Both reproduce the viewer's
/// proven reads (<c>ViewerDataService.Overview.cs</c> / <c>.DailySummary.cs</c>, themselves Lite's
/// <c>GetServerSummaryAsync</c> / <c>GetDailySummaryAsync</c> ported to Postgres) rather than referencing them —
/// the MCP host is in the Service assembly and cannot reference the WPF Viewer (the viewer's
/// <c>ServerSummaryItem</c> carries WPF brushes; only the raw metric reads are lifted here). All STORED reads
/// (no live monitored-server hit).
///
/// <para>The server-summary read is the CPU + memory + blocking + deadlock + last-collection subset the
/// same-named Lite tool exposes: latest SQL CPU, latest total server memory, blocking count in the last hour
/// (XE blocked-process reports, falling back to the always-on DMV snapshot when the XE count is zero), deadlock
/// count in the last hour, and the newest collection time. The daily-summary read is the viewer's full
/// <c>DailySummaryRangeSql</c>; the composite health band is computed by the SHARED
/// <see cref="DailyHealthBandCalculator"/> so it bands identically to Lite and the Darling viewer.</para>
/// </summary>
internal static class DarlingHealthReader
{
    /* ═══════════════════════════ server summary ═══════════════════════════ */

    /// <summary>One server's one-shot health snapshot — the CPU/memory/blocking/deadlock/last-collection subset
    /// the same-named Lite tool exposes.</summary>
    public sealed record ServerSummaryReadResult(
        double? CpuPercent, double? MemoryMb, int BlockingCount, int DeadlockCount, DateTime? LastCollectionTime)
    {
        /// <summary>True when the server has no collected data at all (no CPU/memory snapshot and no collection
        /// log) — the tool surfaces the #1224 "unavailable" miss instead of an all-zero card.</summary>
        public bool HasNoData =>
            CpuPercent is null && MemoryMb is null && LastCollectionTime is null;
    }

    /// <summary>Latest SQL-process CPU for one server (newest ring-buffer sample). $1 server_id.</summary>
    public const string ServerSummaryCpuSql = @"
SELECT sqlserver_cpu_utilization
FROM v_cpu_utilization_stats
WHERE server_id = $1
ORDER BY sample_time DESC
LIMIT 1";

    /// <summary>Latest total server memory (MB) for one server. $1 server_id.</summary>
    public const string ServerSummaryMemorySql = @"
SELECT CAST(total_server_memory_mb AS double precision)
FROM v_memory_stats
WHERE server_id = $1
ORDER BY collection_time DESC
LIMIT 1";

    /// <summary>Blocking counts in the window from both sources — XE blocked-process reports and the always-on
    /// DMV snapshot; the caller applies Lite's XE-preferred, DMV-fallback rule. $1 server_id, $2 window start.</summary>
    public const string ServerSummaryBlockingSql = @"
SELECT
    (SELECT COUNT(*) FROM v_blocked_process_reports WHERE server_id = $1 AND event_time >= $2),
    (SELECT COUNT(*) FROM v_dmv_blocking_snapshots  WHERE server_id = $1 AND event_time >= $2)";

    /// <summary>Deadlock count in the window. $1 server_id, $2 window start.</summary>
    public const string ServerSummaryDeadlockSql = @"
SELECT COUNT(*) FROM v_deadlocks WHERE server_id = $1 AND deadlock_time >= $2";

    /// <summary>Newest collection time across all collectors for one server. $1 server_id.</summary>
    public const string ServerSummaryLastCollectionSql = @"
SELECT MAX(collection_time) FROM v_collection_log WHERE server_id = $1";

    /// <summary>
    /// One server's one-shot health summary — the viewer's <c>GetServerSummaryAsync</c> reduced to the subset
    /// the same-named Lite tool serves. Blocking / deadlock counts use a one-hour window (Lite's window); CPU
    /// and memory take the newest snapshot.
    /// </summary>
    public static async Task<ServerSummaryReadResult> GetServerSummaryAsync(
        NpgsqlDataSource postgres, int serverId, CancellationToken cancellationToken = default)
    {
        var windowStart = DateTime.UtcNow.AddHours(-1);

        double? cpuPercent = null;
        double? memoryMb = null;
        var blockingCount = 0;
        var deadlockCount = 0;
        DateTime? lastCollection = null;

        await using (var command = postgres.CreateCommand(ServerSummaryCpuSql))
        {
            DarlingMcpReadParameters.AddInt(command, serverId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                cpuPercent = reader.IsDBNull(0) ? null : Convert.ToDouble(reader.GetValue(0));
            }
        }

        await using (var command = postgres.CreateCommand(ServerSummaryMemorySql))
        {
            DarlingMcpReadParameters.AddInt(command, serverId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                memoryMb = reader.IsDBNull(0) ? null : Convert.ToDouble(reader.GetValue(0));
            }
        }

        await using (var command = postgres.CreateCommand(ServerSummaryBlockingSql))
        {
            DarlingMcpReadParameters.AddInt(command, serverId);
            DarlingMcpReadParameters.AddTimestamp(command, windowStart);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var xeCount = reader.IsDBNull(0) ? 0 : (int)reader.GetInt64(0);
                var dmvCount = reader.IsDBNull(1) ? 0 : (int)reader.GetInt64(1);
                /* Lite's fallback: use XE when it has any row this window, else the DMV snapshot. */
                blockingCount = xeCount > 0 ? xeCount : dmvCount;
            }
        }

        await using (var command = postgres.CreateCommand(ServerSummaryDeadlockSql))
        {
            DarlingMcpReadParameters.AddInt(command, serverId);
            DarlingMcpReadParameters.AddTimestamp(command, windowStart);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                deadlockCount = reader.IsDBNull(0) ? 0 : (int)reader.GetInt64(0);
            }
        }

        await using (var command = postgres.CreateCommand(ServerSummaryLastCollectionSql))
        {
            DarlingMcpReadParameters.AddInt(command, serverId);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is not null && result != DBNull.Value)
            {
                lastCollection = Convert.ToDateTime(result);
            }
        }

        return new ServerSummaryReadResult(cpuPercent, memoryMb, blockingCount, deadlockCount, lastCollection);
    }

    /* ═══════════════════════════ daily summary ═══════════════════════════ */

    /// <summary>One day's rolled-up signals plus the shared composite health band. Structurally a subset of the
    /// viewer's <c>DailySummaryRow</c>; the band comes from the SHARED <see cref="DailyHealthBandCalculator"/>.</summary>
    public sealed record DailySummaryReadRow(
        DateTime SummaryDate, decimal TotalWaitTimeSec, string TopWaitType, long UniqueQueries, long DeadlockCount,
        long BlockingEvents, long HighCpuEvents, long CollectionErrors, long MemoryPressureEvents,
        long MemoryCriticalEvents, long AlertCount, long MaxBlockDurationMs, bool HasData)
    {
        public DailyHealthSignals ToSignals() => new()
        {
            HasData = HasData,
            Deadlocks = DeadlockCount,
            CollectionErrors = CollectionErrors,
            HighCpuEvents = HighCpuEvents,
            BlockingEvents = BlockingEvents,
            MemoryPressureEvents = MemoryPressureEvents,
            MemoryCriticalEvents = MemoryCriticalEvents,
            AlertCount = AlertCount,
        };

        public DailyHealthBand HealthBand => DailyHealthBandCalculator.Classify(ToSignals());

        /// <summary>Human label for the band ("Healthy" / "Warning" / "Critical" / "No Data").</summary>
        public string OverallHealth => DailyHealthBandCalculator.Label(HealthBand);
    }

    /// <summary>
    /// Grouped per-day daily-summary aggregate — the viewer's <c>DailySummaryRangeSql</c> verbatim. $1
    /// server_id, $2 range start, $3 range end (naive UTC, half-open <c>[start, end)</c>). Reads the
    /// <c>v_wait_stats</c> / <c>v_query_stats</c> / <c>v_deadlocks</c> / <c>v_blocked_process_reports</c> (with
    /// the <c>v_dmv_blocking_snapshots</c> fallback) / <c>v_cpu_utilization_stats</c> / <c>v_collection_log</c> /
    /// <c>v_memory_pressure_events</c> passthrough views plus <c>config_alert_log</c>; one row per collected day.
    /// </summary>
    public const string DailySummaryRangeSql = """
        WITH wait_per_type AS (
            SELECT date_trunc('day', collection_time) AS d, wait_type, SUM(delta_wait_time_ms) AS ms
            FROM v_wait_stats
            WHERE server_id = $1 AND collection_time >= $2 AND collection_time < $3 AND delta_wait_time_ms > 0
            GROUP BY 1, 2
        ),
        wait_totals AS (
            SELECT d, SUM(ms) / 1000.0 AS total_wait_sec
            FROM wait_per_type
            GROUP BY d
        ),
        wait_top AS (
            SELECT DISTINCT ON (d) d, wait_type AS top_wait_type
            FROM wait_per_type
            ORDER BY d, ms DESC
        ),
        waits AS (
            SELECT t.d, t.total_wait_sec, tp.top_wait_type
            FROM wait_totals t
            LEFT JOIN wait_top tp ON tp.d = t.d
        ),
        queries AS (
            SELECT date_trunc('day', collection_time) AS d, COUNT(DISTINCT query_hash) AS c
            FROM v_query_stats
            WHERE server_id = $1 AND collection_time >= $2 AND collection_time < $3
            GROUP BY 1
        ),
        deadlocks AS (
            SELECT date_trunc('day', collection_time) AS d, COUNT(*) AS c
            FROM v_deadlocks
            WHERE server_id = $1 AND collection_time >= $2 AND collection_time < $3
            GROUP BY 1
        ),
        bpr AS (
            SELECT date_trunc('day', collection_time) AS d, COUNT(*) AS c, MAX(wait_time_ms) AS max_wait_ms
            FROM v_blocked_process_reports
            WHERE server_id = $1 AND collection_time >= $2 AND collection_time < $3
            GROUP BY 1
        ),
        dmv AS (
            SELECT date_trunc('day', collection_time) AS d, COUNT(*) AS c, MAX(wait_time_ms) AS max_wait_ms
            FROM v_dmv_blocking_snapshots
            WHERE server_id = $1 AND collection_time >= $2 AND collection_time < $3
            GROUP BY 1
        ),
        cpu AS (
            SELECT date_trunc('day', collection_time) AS d,
                   COUNT(*) FILTER (WHERE (sqlserver_cpu_utilization + COALESCE(other_process_cpu_utilization, 0)) >= 80) AS c
            FROM v_cpu_utilization_stats
            WHERE server_id = $1 AND collection_time >= $2 AND collection_time < $3
            GROUP BY 1
        ),
        coll AS (
            SELECT date_trunc('day', collection_time) AS d,
                   COUNT(*) AS runs,
                   COUNT(*) FILTER (WHERE status = 'ERROR') AS errs
            FROM v_collection_log
            WHERE server_id = $1 AND collection_time >= $2 AND collection_time < $3
            GROUP BY 1
        ),
        mem AS (
            SELECT date_trunc('day', collection_time) AS d,
                   COUNT(*) FILTER (WHERE memory_indicators_process >= 2 OR memory_indicators_system >= 2) AS pressure,
                   COUNT(*) FILTER (WHERE memory_indicators_process >= 3) AS critical
            FROM v_memory_pressure_events
            WHERE server_id = $1 AND collection_time >= $2 AND collection_time < $3
            GROUP BY 1
        ),
        alerts AS (
            SELECT date_trunc('day', alert_time) AS d, COUNT(*) AS c
            FROM config_alert_log
            WHERE server_id = $1 AND alert_time >= $2 AND alert_time < $3
              AND dismissed = FALSE
              AND metric_name NOT LIKE '%Cleared%'
              AND metric_name NOT LIKE '%Resolved%'
              AND metric_name NOT LIKE '%Restored%'
            GROUP BY 1
        ),
        day_spine AS (
            SELECT d FROM waits
            UNION SELECT d FROM queries
            UNION SELECT d FROM deadlocks
            UNION SELECT d FROM bpr
            UNION SELECT d FROM dmv
            UNION SELECT d FROM cpu
            UNION SELECT d FROM coll
            UNION SELECT d FROM mem
            UNION SELECT d FROM alerts
        )
        SELECT
            s.d AS day,
            COALESCE(w.total_wait_sec, 0) AS total_wait_sec,
            w.top_wait_type,
            COALESCE(q.c, 0) AS unique_queries,
            COALESCE(dl.c, 0) AS deadlock_count,
            COALESCE(NULLIF(b.c, 0), dm.c, 0) AS blocking_events,
            COALESCE(cp.c, 0) AS high_cpu_events,
            COALESCE(cl.errs, 0) AS collection_errors,
            COALESCE(m.pressure, 0) AS memory_pressure_events,
            COALESCE(m.critical, 0) AS memory_critical_events,
            COALESCE(al.c, 0) AS alert_count,
            COALESCE(CASE WHEN COALESCE(b.c, 0) > 0 THEN b.max_wait_ms ELSE dm.max_wait_ms END, 0) AS peak_block_wait_ms
        FROM day_spine s
        LEFT JOIN waits w ON w.d = s.d
        LEFT JOIN queries q ON q.d = s.d
        LEFT JOIN deadlocks dl ON dl.d = s.d
        LEFT JOIN bpr b ON b.d = s.d
        LEFT JOIN dmv dm ON dm.d = s.d
        LEFT JOIN cpu cp ON cp.d = s.d
        LEFT JOIN coll cl ON cl.d = s.d
        LEFT JOIN mem m ON m.d = s.d
        LEFT JOIN alerts al ON al.d = s.d
        ORDER BY s.d
        """;

    /// <summary>One <see cref="DailySummaryReadRow"/> per collected day in the half-open [fromDate, toDate)
    /// window (the viewer's <c>GetDailySummaryRangeAsync</c>).</summary>
    public static async Task<List<DailySummaryReadRow>> GetDailySummaryRangeAsync(
        NpgsqlDataSource postgres, int serverId, DateTime fromDate, DateTime toDate, CancellationToken cancellationToken = default)
    {
        var results = new List<DailySummaryReadRow>();
        await using var command = postgres.CreateCommand(DailySummaryRangeSql);
        DarlingMcpReadParameters.AddInt(command, serverId);
        DarlingMcpReadParameters.AddTimestamp(command, fromDate.Date);
        DarlingMcpReadParameters.AddTimestamp(command, toDate.Date);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadDailySummaryRow(reader));
        }

        return results;
    }

    /// <summary>Daily summary for one server on a specific date (or today, UTC, when <paramref name="summaryDate"/>
    /// is null) — the viewer's <c>GetDailySummaryAsync</c>. Returns a No-Data row when the day had no collection.</summary>
    public static async Task<DailySummaryReadRow> GetDailySummaryAsync(
        NpgsqlDataSource postgres, int serverId, DateTime? summaryDate = null, CancellationToken cancellationToken = default)
    {
        var targetDate = summaryDate?.Date ?? DateTime.UtcNow.Date;
        var rows = await GetDailySummaryRangeAsync(postgres, serverId, targetDate, targetDate.AddDays(1), cancellationToken);
        return rows.Count > 0
            ? rows[0]
            : new DailySummaryReadRow(targetDate, 0m, "", 0, 0, 0, 0, 0, 0, 0, 0, 0, HasData: false);
    }

    private static DailySummaryReadRow ReadDailySummaryRow(DbDataReader reader) => new(
        reader.IsDBNull(0) ? DateTime.MinValue : Convert.ToDateTime(reader.GetValue(0)),
        reader.IsDBNull(1) ? 0m : Convert.ToDecimal(reader.GetValue(1)),
        reader.IsDBNull(2) ? "" : reader.GetString(2),
        reader.IsDBNull(3) ? 0L : Convert.ToInt64(reader.GetValue(3)),
        reader.IsDBNull(4) ? 0L : Convert.ToInt64(reader.GetValue(4)),
        reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5)),
        reader.IsDBNull(6) ? 0L : Convert.ToInt64(reader.GetValue(6)),
        reader.IsDBNull(7) ? 0L : Convert.ToInt64(reader.GetValue(7)),
        reader.IsDBNull(8) ? 0L : Convert.ToInt64(reader.GetValue(8)),
        reader.IsDBNull(9) ? 0L : Convert.ToInt64(reader.GetValue(9)),
        reader.IsDBNull(10) ? 0L : Convert.ToInt64(reader.GetValue(10)),
        reader.IsDBNull(11) ? 0L : Convert.ToInt64(reader.GetValue(11)),
        HasData: true);
}
