/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Daily Summary tab's single-row aggregate (W1i), copied from Lite's
/// <c>LocalDataService.DailySummary.cs</c> and run on the <c>v_wait_stats</c> / <c>v_query_stats</c> /
/// <c>v_deadlocks</c> / <c>v_blocked_process_reports</c> (with the <c>v_dmv_blocking_snapshots</c>
/// fallback) / <c>v_cpu_utilization_stats</c> / <c>v_collection_log</c> passthrough views. The whole day
/// rolls up in one scalar-subquery SELECT: total wait seconds, the top wait type, distinct query count,
/// deadlock / blocking-event / high-CPU / collect-error counts, with the health string computed in C#.
/// The SQL is byte-portable between DuckDB and Postgres (positional <c>$1/$2/$3</c> referenced across
/// every subquery, COALESCE/NULLIF, no dialect functions), so only the parameter binding differs: the
/// naive day-boundary <see cref="DateTime"/>s go in with <c>DateTimeKind.Unspecified</c> (the store's
/// naive-UTC convention — a Kind=Utc value would bind as timestamptz and throw against the naive columns).
/// </summary>
public sealed partial class ViewerDataService
{
    /// <summary>
    /// Lite's daily-summary aggregate verbatim. $1 server_id, $2 day start, $3 day end (naive UTC,
    /// half-open <c>[start, end)</c>). The high-CPU rule (total host CPU = SQL + other-process ≥ 80,
    /// with the Linux NULL-other-process fallback) mirrors the alert engine, the Overview headline, and
    /// Dashboard's report.daily_summary — see the inline note.
    /// </summary>
    public const string DailySummarySql = """
        SELECT
            COALESCE(
                (SELECT SUM(delta_wait_time_ms) / 1000.0
                 FROM v_wait_stats
                 WHERE server_id = $1
                 AND   collection_time >= $2 AND collection_time < $3
                 AND   delta_wait_time_ms > 0), 0
            ) AS total_wait_sec,
            (SELECT wait_type
             FROM v_wait_stats
             WHERE server_id = $1
             AND   collection_time >= $2 AND collection_time < $3
             AND   delta_wait_time_ms > 0
             GROUP BY wait_type
             ORDER BY SUM(delta_wait_time_ms) DESC
             LIMIT 1
            ) AS top_wait_type,
            COALESCE(
                (SELECT COUNT(DISTINCT query_hash)
                 FROM v_query_stats
                 WHERE server_id = $1
                 AND   collection_time >= $2 AND collection_time < $3), 0
            ) AS unique_queries,
            COALESCE(
                (SELECT COUNT(*)
                 FROM v_deadlocks
                 WHERE server_id = $1
                 AND   collection_time >= $2 AND collection_time < $3), 0
            ) AS deadlock_count,
            COALESCE(
                NULLIF((SELECT COUNT(*)
                 FROM v_blocked_process_reports
                 WHERE server_id = $1
                 AND   collection_time >= $2 AND collection_time < $3), 0),
                (SELECT COUNT(*)
                 FROM v_dmv_blocking_snapshots
                 WHERE server_id = $1
                 AND   collection_time >= $2 AND collection_time < $3), 0
            ) AS blocking_events,
            COALESCE(
                (SELECT COUNT(*)
                 FROM v_cpu_utilization_stats
                 WHERE server_id = $1
                 /* Total host CPU = SQL + other-process, matching the alert engine (CpuAlertMode.Total),
                    the Overview headline (TotalCpuPercent = sqlserver + (other_process ?? 0)), and
                    Dashboard's report.daily_summary (#1004). other_process_cpu_utilization is NULL on SQL
                    Server on Linux (host CPU not derivable, #1048), so COALESCE(.,0) collapses this to the
                    SQL-only figure there -- the same fallback as Dashboard's ISNULL(total, sqlserver). */
                 AND   (sqlserver_cpu_utilization + COALESCE(other_process_cpu_utilization, 0)) >= 80
                 AND   collection_time >= $2 AND collection_time < $3), 0
            ) AS high_cpu_events,
            COALESCE(
                (SELECT COUNT(*)
                 FROM v_collection_log
                 WHERE server_id = $1
                 AND   status = 'ERROR'
                 AND   collection_time >= $2 AND collection_time < $3), 0
            ) AS collection_errors
        """;

    /// <summary>
    /// Daily summary for one server on a specific date (or today, UTC, when <paramref name="summaryDate"/>
    /// is null). Copied from Lite's <c>GetDailySummaryAsync</c> — same half-open day window, same health
    /// banding computed on the read.
    /// </summary>
    public async Task<DailySummaryRow?> GetDailySummaryAsync(int serverId, DateTime? summaryDate = null, CancellationToken cancellationToken = default)
    {
        var targetDate = summaryDate?.Date ?? DateTime.UtcNow.Date;
        var dayStart = targetDate;
        var dayEnd = targetDate.AddDays(1);

        await using var command = _dataSource.CreateCommand(DailySummarySql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.SpecifyKind(dayStart, DateTimeKind.Unspecified) });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = DateTime.SpecifyKind(dayEnd, DateTimeKind.Unspecified) });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;

        var deadlocks = reader.IsDBNull(3) ? 0L : Convert.ToInt64(reader.GetValue(3));
        var blocking = reader.IsDBNull(4) ? 0L : Convert.ToInt64(reader.GetValue(4));
        var highCpu = reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5));

        var health = "NORMAL";
        if (deadlocks > 0) health = "DEADLOCKS";
        else if (highCpu > 5) health = "CPU_CRITICAL";
        else if (blocking > 10) health = "BLOCKING";

        return new DailySummaryRow
        {
            SummaryDate = targetDate,
            TotalWaitTimeSec = reader.IsDBNull(0) ? 0m : Convert.ToDecimal(reader.GetValue(0)),
            TopWaitType = reader.IsDBNull(1) ? "" : reader.GetString(1),
            UniqueQueries = reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2)),
            DeadlockCount = deadlocks,
            BlockingEvents = blocking,
            HighCpuEvents = highCpu,
            CollectionErrors = reader.IsDBNull(6) ? 0L : Convert.ToInt64(reader.GetValue(6)),
            OverallHealth = health
        };
    }
}

/// <summary>
/// The single Daily Summary grid row. Copied VERBATIM from Lite's <c>DailySummaryRow</c>
/// (LocalDataService.DailySummary.cs): the two display properties are pure formats of stored values.
/// </summary>
public class DailySummaryRow
{
    public DateTime SummaryDate { get; set; }
    public decimal TotalWaitTimeSec { get; set; }
    public string TopWaitType { get; set; } = "";
    public long UniqueQueries { get; set; }
    public long DeadlockCount { get; set; }
    public long BlockingEvents { get; set; }
    public long HighCpuEvents { get; set; }
    public long CollectionErrors { get; set; }
    public string OverallHealth { get; set; } = "";

    public string SummaryDateFormatted => SummaryDate.ToString("yyyy-MM-dd");
    public string TotalWaitFormatted => TotalWaitTimeSec < 1000
        ? $"{TotalWaitTimeSec:N1} s"
        : $"{TotalWaitTimeSec / 60:N1} min";
}
