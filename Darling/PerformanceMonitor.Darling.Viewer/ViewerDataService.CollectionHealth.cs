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
/// The Collection Health tab's three ported reads (W1i), copied from Lite's
/// <c>LocalDataService.CollectionHealth.cs</c> and run on the <c>v_collection_log</c> passthrough view.
/// This REPLACES the shell's placeholder Collection Health read (the DISTINCT-ON latest-run-per-collector
/// query and its simple <c>CollectorHealthRow</c> record, both removed from <c>ViewerDataService.cs</c>)
/// with Lite's rich 7-day aggregate: per-collector run/success/error counts, average duration, last
/// success / last run / last error timestamps, and the <see cref="CollectorHealthRow.HealthStatus"/>
/// banding. The SQL is byte-portable between DuckDB and Postgres (positional <c>$1/$2/$3</c>, plain
/// aggregates), so only the parameter binding differs: window-start <see cref="DateTime"/>s go in with
/// <c>DateTimeKind.Unspecified</c> (the naive-UTC store convention), and the SUM/COUNT/AVG results are
/// read type-agnostically (Postgres returns <c>bigint</c> for the counts and <c>numeric</c> for AVG,
/// where DuckDB returned HUGEINT/DECIMAL) — the same intent as Lite's <c>ToInt64</c>/<c>ToDouble</c>
/// helpers, minus the DuckDB BigInteger case Postgres never produces.
/// </summary>
public sealed partial class ViewerDataService
{
    /// <summary>
    /// Lite's 7-day per-collector health aggregate (<c>GetCollectionHealthAsync</c>) verbatim: one row
    /// per collector over the trailing window, with the SKIPPED-counts-as-a-healthy-run rule baked into
    /// the last-success MAX and the PERMISSIONS bucket split out for the NO_PERMISSIONS banding. $1
    /// server_id, $2 window start (naive UTC).
    /// </summary>
    public const string CollectionHealthSql = """
        SELECT
            collector_name,
            COUNT(*) AS total_runs,
            SUM(CASE WHEN status = 'SUCCESS' THEN 1 ELSE 0 END) AS success_count,
            SUM(CASE WHEN status = 'ERROR' THEN 1 ELSE 0 END) AS error_count,
            AVG(duration_ms) AS avg_duration_ms,
            -- SKIPPED counts as a healthy run (dedup / version-gated collectors no-op without being stale)
            MAX(CASE WHEN status IN ('SUCCESS', 'SKIPPED') THEN collection_time END) AS last_success_time,
            MAX(collection_time) AS last_run_time,
            MAX(CASE WHEN status IN ('ERROR', 'PERMISSIONS') THEN error_message END) AS last_error,
            MAX(CASE WHEN status IN ('ERROR', 'PERMISSIONS') THEN collection_time END) AS last_error_time,
            SUM(CASE WHEN status = 'PERMISSIONS' THEN 1 ELSE 0 END) AS permission_denied_count
        FROM v_collection_log
        WHERE server_id = $1
        AND   collection_time >= $2
        GROUP BY collector_name
        ORDER BY collector_name
        """;

    /// <summary>
    /// Lite's recent-log read (<c>GetRecentCollectionLogAsync</c>) verbatim: the collection_log rows for
    /// one server since the window start, newest first, capped. Feeds the Collection Log sub-tab grid.
    /// $1 server_id, $2 window start (naive UTC), $3 row cap.
    /// </summary>
    public const string RecentCollectionLogSql = """
        SELECT
            collector_name,
            collection_time,
            duration_ms,
            sql_duration_ms,
            duckdb_duration_ms,
            rows_collected,
            status,
            error_message,
            server_name
        FROM v_collection_log
        WHERE server_id = $1
        AND   collection_time >= $2
        ORDER BY collection_time DESC
        LIMIT $3
        """;

    /// <summary>
    /// Lite's per-collector drill read (<c>GetCollectionLogByCollectorAsync</c>) verbatim: every
    /// collection_log row for one collector on one server since the window start, newest first. Feeds
    /// the CollectionLogWindow the Health Summary grid opens on double-click. $1 server_id, $2
    /// collector_name, $3 window start (naive UTC).
    /// </summary>
    public const string CollectionLogByCollectorSql = """
        SELECT
            collector_name,
            collection_time,
            duration_ms,
            sql_duration_ms,
            duckdb_duration_ms,
            rows_collected,
            status,
            error_message,
            server_name
        FROM v_collection_log
        WHERE server_id = $1
        AND   collector_name = $2
        AND   collection_time >= $3
        ORDER BY collection_time DESC
        """;

    /// <summary>
    /// Per-collector health summary for one server over the trailing 7 days. Copied from Lite's
    /// <c>GetCollectionHealthAsync</c> — same window, same columns, HealthStatus computed on the row.
    /// </summary>
    public async Task<List<CollectorHealthRow>> GetCollectionHealthAsync(int serverId, CancellationToken cancellationToken = default)
    {
        var items = new List<CollectorHealthRow>();

        await using var command = _dataSource.CreateCommand(CollectionHealthSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(DateTime.UtcNow.AddDays(-7), DateTimeKind.Unspecified),
        });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new CollectorHealthRow
            {
                CollectorName = reader.GetString(0),
                TotalRuns = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1)),
                SuccessCount = reader.IsDBNull(2) ? 0 : Convert.ToInt64(reader.GetValue(2)),
                ErrorCount = reader.IsDBNull(3) ? 0 : Convert.ToInt64(reader.GetValue(3)),
                AvgDurationMs = reader.IsDBNull(4) ? 0 : Convert.ToDouble(reader.GetValue(4)),
                LastSuccessTime = reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                LastRunTime = reader.IsDBNull(6) ? null : reader.GetDateTime(6),
                LastError = reader.IsDBNull(7) ? null : reader.GetString(7),
                LastErrorTime = reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                PermissionDeniedCount = reader.IsDBNull(9) ? 0 : Convert.ToInt64(reader.GetValue(9)),
            });
        }

        return items;
    }

    /// <summary>
    /// Recent collection_log entries for one server, most recent first. Copied from Lite's
    /// <c>GetRecentCollectionLogAsync</c> (default 4-hour window, 500-row cap).
    /// </summary>
    public async Task<List<CollectionLogRow>> GetRecentCollectionLogAsync(int serverId, int hoursBack = 4, int maxRows = 500, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(RecentCollectionLogSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(-hoursBack), DateTimeKind.Unspecified),
        });
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = maxRows });

        return await ReadCollectionLogAsync(command, cancellationToken);
    }

    /// <summary>
    /// Collection_log entries for a specific collector on one server, most recent first. Copied from
    /// Lite's <c>GetCollectionLogByCollectorAsync</c> (default 168-hour / 7-day window). Feeds the
    /// CollectionLogWindow drill.
    /// </summary>
    public async Task<List<CollectionLogRow>> GetCollectionLogByCollectorAsync(int serverId, string collectorName, int hoursBack = 168, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(CollectionLogByCollectorSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = collectorName });
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(DateTime.UtcNow.AddHours(-hoursBack), DateTimeKind.Unspecified),
        });

        return await ReadCollectionLogAsync(command, cancellationToken);
    }

    /// <summary>Shared reader for the two collection-log projections (identical column list).</summary>
    private static async Task<List<CollectionLogRow>> ReadCollectionLogAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var items = new List<CollectionLogRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new CollectionLogRow
            {
                CollectorName = reader.GetString(0),
                CollectionTime = reader.GetDateTime(1),
                DurationMs = reader.IsDBNull(2) ? null : Convert.ToInt32(reader.GetValue(2)),
                SqlDurationMs = reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3)),
                DuckDbDurationMs = reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4)),
                RowsCollected = reader.IsDBNull(5) ? null : Convert.ToInt32(reader.GetValue(5)),
                Status = reader.GetString(6),
                ErrorMessage = reader.IsDBNull(7) ? null : reader.GetString(7),
                ServerName = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }

        return items;
    }
}

/// <summary>
/// One row of the Collection Log grid / drill window — a single collector run's outcome. Copied
/// VERBATIM from Lite's <c>CollectionLogRow</c> (LocalDataService.CollectionHealth.cs): every display
/// property is a pure format of stored values, and <see cref="CollectionTimeFormatted"/>'s
/// <c>ToLocalTime()</c> on the store's naive-UTC collection_time is correct because Npgsql reads a
/// <c>timestamp</c> column as an <c>Unspecified</c>-kind <see cref="DateTime"/> and
/// <see cref="DateTime.ToLocalTime"/> treats Unspecified as UTC (the same convention Lite's DuckDB
/// reader produced). <see cref="DuckDbDurationMs"/> keeps its store column name (<c>duckdb_duration_ms</c>)
/// but in the Darling store that column records the POSTGRES write phase — the Collection Log grid
/// labels it "Store (ms)".
/// </summary>
public class CollectionLogRow
{
    public string CollectorName { get; set; } = "";
    public string? ServerName { get; set; }
    public DateTime CollectionTime { get; set; }
    public int? DurationMs { get; set; }
    public int? SqlDurationMs { get; set; }

    /// <summary>Stored under the legacy <c>duckdb_duration_ms</c> column name; in the Darling store it
    /// carries the Postgres storage-phase milliseconds. Surfaced as the grid's "Store (ms)" column.</summary>
    public int? DuckDbDurationMs { get; set; }
    public int? RowsCollected { get; set; }
    public string Status { get; set; } = "";
    public string? ErrorMessage { get; set; }

    public string CollectionTimeFormatted => CollectionTime.ToLocalTime().ToString("g");

    public string DurationFormatted => DurationMs.HasValue
        ? (DurationMs.Value < 1000 ? $"{DurationMs.Value} ms" : $"{DurationMs.Value / 1000.0:F1} s")
        : "";

    public string SqlDurationFormatted => SqlDurationMs.HasValue ? $"{SqlDurationMs.Value} ms" : "";

    public string DuckDbDurationFormatted => DuckDbDurationMs.HasValue ? $"{DuckDbDurationMs.Value} ms" : "";
}

/// <summary>
/// One Collection Health "Health Summary" grid row — a collector's 7-day roll-up with its health band.
/// Copied VERBATIM from Lite's rich <c>CollectorHealthRow</c> (LocalDataService.CollectionHealth.cs);
/// it REPLACES the shell's placeholder <c>CollectorHealthRow</c> record (a single latest-run snapshot).
/// Every property is a pure computation over the aggregate — <see cref="HealthStatus"/> bands
/// NEVER_RUN / NO_PERMISSIONS / FAILING / STALE / WARNING / HEALTHY with the on-load-collector staleness
/// exemption, exactly as Lite. The <see cref="DateTime.UtcNow"/> arithmetic in
/// <see cref="HoursSinceLastSuccess"/> is correct against the store's naive-UTC timestamps because both
/// sides are UTC instants (tick subtraction ignores Kind), matching Lite.
/// </summary>
public class CollectorHealthRow
{
    /// <summary>
    /// On-load collectors run once per tab open, not on the scheduled loop.
    /// Staleness thresholds don't apply to them.
    /// </summary>
    private static readonly HashSet<string> OnLoadCollectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "server_config",
        "database_config",
        "database_scoped_config",
        "trace_flags",
        "server_properties"
    };

    public string CollectorName { get; set; } = "";
    public long TotalRuns { get; set; }
    public long SuccessCount { get; set; }
    public long ErrorCount { get; set; }
    public double AvgDurationMs { get; set; }
    public DateTime? LastSuccessTime { get; set; }
    public DateTime? LastRunTime { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorTime { get; set; }
    public long PermissionDeniedCount { get; set; }

    public double FailureRatePercent => TotalRuns > 0 ? (double)ErrorCount / TotalRuns * 100 : 0;
    public double HoursSinceLastSuccess => LastSuccessTime.HasValue
        ? (DateTime.UtcNow - LastSuccessTime.Value).TotalHours
        : 999;

    public string HealthStatus
    {
        get
        {
            if (TotalRuns == 0) return "NEVER_RUN";
            if (PermissionDeniedCount > 0 && ErrorCount == 0 && SuccessCount == 0) return "NO_PERMISSIONS";
            if (OnLoadCollectors.Contains(CollectorName))
            {
                if (FailureRatePercent > 20) return "WARNING";
                return "HEALTHY";
            }
            if (HoursSinceLastSuccess > 24) return "FAILING";
            if (HoursSinceLastSuccess > 4) return "STALE";
            if (FailureRatePercent > 20) return "WARNING";
            return "HEALTHY";
        }
    }

    public string AvgDurationFormatted => AvgDurationMs < 1000
        ? $"{AvgDurationMs:F0} ms"
        : $"{AvgDurationMs / 1000:F1} s";

    public string LastSuccessFormatted => LastSuccessTime.HasValue
        ? LastSuccessTime.Value.ToLocalTime().ToString("g")
        : "Never";

    public string LastRunFormatted => LastRunTime.HasValue
        ? LastRunTime.Value.ToLocalTime().ToString("g")
        : "Never";

    public string LastErrorFormatted => LastErrorTime.HasValue
        ? LastErrorTime.Value.ToLocalTime().ToString("g")
        : "";
}
