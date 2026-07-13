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
using DuckDB.NET.Data;
using PerformanceMonitor.Ui;

namespace PerformanceMonitorLite.Services;

/// <summary>
/// One row of the unified "Expensive Queries" ranked list — the Lite port of Darling's
/// <c>ViewerExpensiveQueryRow</c> (Darling/PerformanceMonitor.Darling.Viewer/ViewerDataService.ExpensiveQueries.cs),
/// itself the viewer copy of the Dashboard's <c>report.expensive_queries_today</c>
/// (install/46_create_query_plan_views.sql). A single row is one query/proc/query-store group ranked by
/// average worker (CPU) time, carrying the same metric set: execution count, total/avg worker + elapsed
/// time, logical/physical reads + writes, max grant, the query-text sample, and the stored plan XML.
///
/// <para>Numeric members already carry display units (worker/elapsed in seconds + ms, grant in MB) — the
/// read does the microsecond→ms/sec and KB/page→MB conversions in SQL, exactly like the Dashboard view's
/// outer projection. <see cref="FirstExecutionTime"/>/<see cref="LastExecutionTime"/> render through Lite's
/// <see cref="ServerTimeHelper"/> like the sibling Query grids. <see cref="QueryPlanXml"/> rides IN-ROW
/// (like the Active Queries snapshot grid, and unlike the Top Queries/Procedures/Query Store grids which
/// fetch on demand) because the unified row has no single per-source key to re-fetch by — the final list is
/// capped at a handful of rows, so carrying the stored plan inline is cheap and lets "View Plan" + "Copy
/// Repro Script" run with zero extra reads and zero live SQL.</para>
///
/// <para>Store-driven deviation from Darling: Lite's <c>query_store_stats</c> collects NO plan XML and no
/// plan_handle, so the Query Store arm's <see cref="QueryPlanXml"/> and <see cref="PlanHandle"/> are always
/// empty (Darling's Postgres store keeps a <c>query_plan_text</c>; Lite does not). Its "View Plan" is
/// therefore disabled while "Get Actual Plan" still works by re-executing the (runnable) statement text.</para>
/// </summary>
public sealed class UnifiedExpensiveQueryRow
{
    public string Source { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string ObjectName { get; set; } = "";
    public long ExecutionCount { get; set; }
    public double TotalWorkerTimeSec { get; set; }
    public double AvgWorkerTimeMs { get; set; }
    public double TotalElapsedTimeSec { get; set; }
    public double AvgElapsedTimeMs { get; set; }
    public long TotalLogicalReads { get; set; }
    public long AvgLogicalReads { get; set; }
    public long TotalLogicalWrites { get; set; }
    public long AvgLogicalWrites { get; set; }
    public long TotalPhysicalReads { get; set; }
    public long AvgPhysicalReads { get; set; }

    /// <summary>Max requested memory grant in MB, or null for the procedure sources (the module-level DMVs
    /// expose no grant columns — the Dashboard view carries NULL for them too).</summary>
    public double? MaxGrantMb { get; set; }

    /// <summary>The query-text sample: the statement text for the Query Stats / Query Store sources, the
    /// 3-part object name for the procedure sources (module-level stats carry no statement text — matching
    /// the Dashboard view's query_text_sample).</summary>
    public string QueryText { get; set; } = "";

    /// <summary>The collector's STORED plan XML for the row, carried in-row so "View Plan" / "Copy Repro
    /// Script" need no extra read; null when no plan was captured (best-effort, or aged out of retention).
    /// Always null for the Query Store source (Lite's query_store_stats stores no plan).</summary>
    public string? QueryPlanXml { get; set; }

    /// <summary>The row's plan_handle for the live-plan path, carried by the query_stats + procedure_stats
    /// arms (MAX(plan_handle) per group). The Query Store arm has no plan_handle, so its rows leave this
    /// empty and keep the live-plan action correctly disabled.</summary>
    public string PlanHandle { get; set; } = "";

    public DateTime? FirstExecutionTime { get; set; }
    public DateTime? LastExecutionTime { get; set; }

    public string FirstExecutionTimeLocal => ServerTimeHelper.FormatServerTime(FirstExecutionTime);
    public string LastExecutionTimeLocal => ServerTimeHelper.FormatServerTime(LastExecutionTime);

    /// <summary>Whether a stored plan rode back for this row — gates "View Plan" (a plan-less row shows the
    /// "no plan available" message instead of a shown-and-failed open).</summary>
    public bool HasQueryPlan => !string.IsNullOrEmpty(QueryPlanXml);

    /// <summary>Whether this row carries a plan_handle — the live-plan gate. Query Store rows have none.</summary>
    public bool HasLivePlanHandle => !string.IsNullOrEmpty(PlanHandle);

    /// <summary>True when <see cref="QueryText"/> holds a RUNNABLE statement (the Query Stats / Query Store
    /// sources), so "Get Actual Plan" / "Copy Repro Script" produce a repro. The procedure sources carry
    /// only the object name (no statement text), so they no-op the repro exactly like Lite's procedure rows.</summary>
    public bool IsStatementSource =>
        Source == LocalDataService.ExpensiveSourceQueryStats ||
        Source == LocalDataService.ExpensiveSourceQueryStore;
}

public partial class LocalDataService
{
    // ── Source labels (the `source` column values the UNION emits; pinned by test against the SQL) ──

    /// <summary>Ad-hoc statements from query_stats.</summary>
    public const string ExpensiveSourceQueryStats = "Query Stats";

    /// <summary>Stored procedures from procedure_stats (object_type = 'PROCEDURE').</summary>
    public const string ExpensiveSourceStoredProcedure = "Stored Procedure";

    /// <summary>Triggers from procedure_stats (object_type = 'TRIGGER').</summary>
    public const string ExpensiveSourceTrigger = "Trigger";

    /// <summary>
    /// Functions from procedure_stats (object_type = 'FUNCTION'). The shared ProcedureStatsCollector labels
    /// ALL functions 'FUNCTION' (sys.dm_exec_function_stats covers scalar + table-valued alike and does not
    /// distinguish them), so — unlike the Dashboard view's 'Scalar Function' / 'Table Function' split — the
    /// store cannot tell them apart; this single 'Function' label is the faithful adaptation (identical to
    /// Darling, which shares the same collector).
    /// </summary>
    public const string ExpensiveSourceFunction = "Function";

    /// <summary>Queries from query_store_stats.</summary>
    public const string ExpensiveSourceQueryStore = "Query Store";

    /// <summary>TOP(N) per source before the cross-source rank + cap (the Dashboard view's TOP(20)/source).</summary>
    public const int UnifiedExpensiveQueriesTopPerSource = 20;

    /// <summary>Final cap on the merged ranked list (the Dashboard view's outer TOP(20)).</summary>
    public const int UnifiedExpensiveQueriesFinalTop = 20;

    /// <summary>
    /// The unified "Expensive Queries" read for one server over the toolbar window — a cross-source UNION ALL
    /// of the three query engines, each grouped and TOP(N)-ranked by AVERAGE worker (CPU) time, then merged,
    /// re-ranked by average worker time and capped. Mirrors the Dashboard's <c>report.expensive_queries_today</c>
    /// (install/46_create_query_plan_views.sql) and Darling's <c>GetUnifiedExpensiveQueriesAsync</c>, adapted
    /// SQL-Server/Postgres → DuckDB against Lite's archive views.
    ///
    /// <para>Per-source idioms are reused from the existing Lite reads: the query_stats / query_store arms SUM
    /// the collected DELTAS / weight per-interval averages by execution_count (never the raw cumulative DMV
    /// totals, which would over-count across snapshots — exactly why <c>GetTopQueriesByCpuAsync</c> sums delta
    /// columns), CAST every summed value back to BIGINT for the typed reader, and a LATERAL fetches each
    /// group's LATEST query text + stored plan. Every arm is both-sides window-bound on <c>collection_time</c>
    /// (naive UTC) and honours the #1319 database filter.</para>
    ///
    /// <para>Store-driven deviations from the Dashboard view (all reported, none silently dropped):
    /// (1) Lite's query_stats table has NO object_type/schema_name/object_name columns, so the Query Stats
    /// source's object_name is the constant 'Adhoc' (the Dashboard view's STATEMENT case) and the statement
    /// text carries the identity. (2) The procedure collector labels all functions 'FUNCTION', so the
    /// 'Scalar Function'/'Table Function' split collapses to <see cref="ExpensiveSourceFunction"/>.
    /// (3) The Query Store grant is converted 8-KB-pages → MB (<c>* 8 / 1024</c>), staying consistent with
    /// Lite's Query Store tab; the Dashboard view treats the same page count as KB (a latent unit bug, off by
    /// 8×) — matching Lite's own read is the unit-correct choice. (4) Lite's query_store_stats stores no plan
    /// XML and no plan_handle, so the Query Store arm carries neither.</para>
    ///
    /// <para>$1 server_id, $2 window start, $3 window end (naive UTC), $4 TOP-per-source, $5 final TOP,
    /// $6.. optional database-name filter.</para>
    /// </summary>
    public async Task<List<UnifiedExpensiveQueryRow>> GetUnifiedExpensiveQueriesAsync(
        int serverId, int hoursBack = 24,
        int topPerSource = UnifiedExpensiveQueriesTopPerSource, int finalTop = UnifiedExpensiveQueriesFinalTop,
        DateTime? fromDate = null, DateTime? toDate = null,
        IReadOnlyList<string>? databaseNames = null)
    {
        using var _q = TimeQuery("GetExpensiveQueriesAsync", "cross-source expensive queries by avg CPU");
        using var connection = await OpenConnectionAsync();
        using var command = connection.CreateCommand();

        var (startTime, endTime) = GetTimeRange(hoursBack, fromDate, toDate);
        /* The SAME clause (same positional params $6..) is spliced into all three arms and the values bound
           once — the established Lite pattern (see GetQueryStatsComparisonAsync). */
        var dbClause = BuildDbInClause(databaseNames, "database_name", 6, out var dbValues);

        command.CommandText = $@"
WITH all_sources AS (
    /* Ad-hoc queries from query_stats — grouped by (database, query_hash), ranked by avg worker time.
       No object_type/schema_name/object_name columns in Lite's query_stats, so object_name is the constant
       'Adhoc' (the Dashboard view's STATEMENT case); the statement text carries the identity. */
    SELECT
        '{ExpensiveSourceQueryStats}' AS source,
        r.database_name,
        'Adhoc' AS object_name,
        r.execution_count,
        r.total_worker_us,
        r.total_elapsed_us,
        r.total_logical_reads,
        r.total_logical_writes,
        r.total_physical_reads,
        r.max_grant_mb,
        COALESCE(t.query_text, '') AS query_text_sample,
        t.query_plan_xml,
        r.plan_handle,
        r.first_execution_time,
        r.last_execution_time
    FROM (
        SELECT
            database_name,
            query_hash,
            CAST(SUM(delta_execution_count) AS BIGINT) AS execution_count,
            CAST(SUM(delta_worker_time) AS BIGINT) AS total_worker_us,
            CAST(SUM(delta_elapsed_time) AS BIGINT) AS total_elapsed_us,
            CAST(SUM(delta_logical_reads) AS BIGINT) AS total_logical_reads,
            CAST(SUM(delta_logical_writes) AS BIGINT) AS total_logical_writes,
            CAST(SUM(delta_physical_reads) AS BIGINT) AS total_physical_reads,
            MAX(max_grant_kb) / 1024.0 AS max_grant_mb,
            MAX(plan_handle) AS plan_handle,
            MIN(creation_time) AS first_execution_time,
            MAX(last_execution_time) AS last_execution_time
        FROM v_query_stats
        WHERE server_id = $1
        AND   collection_time >= $2
        AND   collection_time <= $3{dbClause}
        GROUP BY database_name, query_hash
        HAVING SUM(delta_execution_count) > 0 OR SUM(delta_elapsed_time) > 0
        ORDER BY CAST(SUM(delta_worker_time) AS DOUBLE PRECISION) / NULLIF(SUM(delta_execution_count), 0) DESC
        LIMIT $4
    ) AS r
    LEFT JOIN LATERAL (
        SELECT query_text, query_plan_xml
        FROM v_query_stats
        WHERE server_id = $1
        AND   database_name = r.database_name
        AND   query_hash = r.query_hash
        AND   query_text IS NOT NULL
        ORDER BY collection_time DESC
        LIMIT 1
    ) AS t ON TRUE

    UNION ALL

    /* Stored procedures / triggers / functions from procedure_stats — grouped by object, ranked by avg
       worker time. The procedure DMVs expose no memory-grant columns, so max_grant_mb is NULL (the Dashboard
       view carries NULL too); the query-text sample is the 3-part object name. */
    SELECT
        CASE r.object_type
            WHEN 'PROCEDURE' THEN '{ExpensiveSourceStoredProcedure}'
            WHEN 'TRIGGER'   THEN '{ExpensiveSourceTrigger}'
            WHEN 'FUNCTION'  THEN '{ExpensiveSourceFunction}'
            ELSE 'Procedure'
        END AS source,
        r.database_name,
        r.schema_name || '.' || r.object_name AS object_name,
        r.execution_count,
        r.total_worker_us,
        r.total_elapsed_us,
        r.total_logical_reads,
        r.total_logical_writes,
        r.total_physical_reads,
        CAST(NULL AS DOUBLE PRECISION) AS max_grant_mb,
        r.database_name || '.' || r.schema_name || '.' || r.object_name AS query_text_sample,
        p.query_plan_xml,
        r.plan_handle,
        r.first_execution_time,
        r.last_execution_time
    FROM (
        SELECT
            database_name,
            schema_name,
            object_name,
            object_type,
            CAST(SUM(delta_execution_count) AS BIGINT) AS execution_count,
            CAST(SUM(delta_worker_time) AS BIGINT) AS total_worker_us,
            CAST(SUM(delta_elapsed_time) AS BIGINT) AS total_elapsed_us,
            CAST(SUM(delta_logical_reads) AS BIGINT) AS total_logical_reads,
            CAST(SUM(delta_logical_writes) AS BIGINT) AS total_logical_writes,
            CAST(SUM(delta_physical_reads) AS BIGINT) AS total_physical_reads,
            MAX(plan_handle) AS plan_handle,
            MIN(cached_time) AS first_execution_time,
            MAX(last_execution_time) AS last_execution_time
        FROM v_procedure_stats
        WHERE server_id = $1
        AND   collection_time >= $2
        AND   collection_time <= $3{dbClause}
        GROUP BY database_name, schema_name, object_name, object_type
        HAVING SUM(delta_execution_count) > 0 OR SUM(delta_elapsed_time) > 0
        ORDER BY CAST(SUM(delta_worker_time) AS DOUBLE PRECISION) / NULLIF(SUM(delta_execution_count), 0) DESC
        LIMIT $4
    ) AS r
    LEFT JOIN LATERAL (
        SELECT query_plan_xml
        FROM v_procedure_stats
        WHERE server_id = $1
        AND   database_name = r.database_name
        AND   schema_name = r.schema_name
        AND   object_name = r.object_name
        AND   query_plan_xml IS NOT NULL
        ORDER BY collection_time DESC
        LIMIT 1
    ) AS p ON TRUE

    UNION ALL

    /* Query Store — grouped by (database, query_id, module), ranked by avg worker time. Query Store rows are
       per-interval averages, so totals weight each average by execution_count (like the Query Store slicer).
       The grant is 8-KB pages → MB (* 8 / 1024), consistent with the Query Store tab. Lite's query_store_stats
       stores no plan XML and no plan_handle, so both are NULL. */
    SELECT
        '{ExpensiveSourceQueryStore}' AS source,
        r.database_name,
        COALESCE(r.module_name, 'Ad Hoc') AS object_name,
        r.execution_count,
        r.total_worker_us,
        r.total_elapsed_us,
        r.total_logical_reads,
        r.total_logical_writes,
        r.total_physical_reads,
        r.max_grant_mb,
        COALESCE(t.query_text, '') AS query_text_sample,
        CAST(NULL AS VARCHAR) AS query_plan_xml,
        CAST(NULL AS VARCHAR) AS plan_handle,
        r.first_execution_time,
        r.last_execution_time
    FROM (
        SELECT
            database_name,
            query_id,
            module_name,
            CAST(SUM(execution_count) AS BIGINT) AS execution_count,
            CAST(SUM(avg_cpu_time_us * execution_count) AS BIGINT) AS total_worker_us,
            CAST(SUM(avg_duration_us * execution_count) AS BIGINT) AS total_elapsed_us,
            CAST(SUM(avg_logical_io_reads * execution_count) AS BIGINT) AS total_logical_reads,
            CAST(SUM(avg_logical_io_writes * execution_count) AS BIGINT) AS total_logical_writes,
            CAST(SUM(avg_physical_io_reads * execution_count) AS BIGINT) AS total_physical_reads,
            MAX(max_query_max_used_memory) * 8.0 / 1024.0 AS max_grant_mb,
            MIN(first_execution_time) AS first_execution_time,
            MAX(last_execution_time) AS last_execution_time
        FROM v_query_store_stats
        WHERE server_id = $1
        AND   collection_time >= $2
        AND   collection_time <= $3{dbClause}
        GROUP BY database_name, query_id, module_name
        HAVING SUM(execution_count) > 0
        ORDER BY CAST(SUM(avg_cpu_time_us * execution_count) AS DOUBLE PRECISION) / NULLIF(SUM(execution_count), 0) DESC
        LIMIT $4
    ) AS r
    LEFT JOIN LATERAL (
        SELECT query_text
        FROM v_query_store_stats
        WHERE server_id = $1
        AND   database_name = r.database_name
        AND   query_id = r.query_id
        AND   query_text IS NOT NULL
        ORDER BY collection_time DESC
        LIMIT 1
    ) AS t ON TRUE
)
SELECT
    source,
    database_name,
    object_name,
    execution_count,
    total_worker_us / 1000000.0 AS total_worker_sec,
    total_worker_us / 1000.0 / NULLIF(execution_count, 0) AS avg_worker_ms,
    total_elapsed_us / 1000000.0 AS total_elapsed_sec,
    total_elapsed_us / 1000.0 / NULLIF(execution_count, 0) AS avg_elapsed_ms,
    total_logical_reads,
    CAST(FLOOR(total_logical_reads / NULLIF(execution_count, 0)) AS BIGINT) AS avg_logical_reads,
    total_logical_writes,
    CAST(FLOOR(total_logical_writes / NULLIF(execution_count, 0)) AS BIGINT) AS avg_logical_writes,
    total_physical_reads,
    CAST(FLOOR(total_physical_reads / NULLIF(execution_count, 0)) AS BIGINT) AS avg_physical_reads,
    max_grant_mb,
    query_text_sample,
    query_plan_xml,
    first_execution_time,
    last_execution_time,
    plan_handle
FROM all_sources
ORDER BY avg_worker_ms DESC NULLS LAST
LIMIT $5";

        command.Parameters.Add(new DuckDBParameter { Value = serverId });
        command.Parameters.Add(new DuckDBParameter { Value = startTime });
        command.Parameters.Add(new DuckDBParameter { Value = endTime });
        command.Parameters.Add(new DuckDBParameter { Value = topPerSource });
        command.Parameters.Add(new DuckDBParameter { Value = finalTop });
        foreach (var db in dbValues)
            command.Parameters.Add(new DuckDBParameter { Value = db });

        var rows = new List<UnifiedExpensiveQueryRow>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new UnifiedExpensiveQueryRow
            {
                Source = reader.IsDBNull(0) ? "" : reader.GetString(0),
                DatabaseName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                ObjectName = reader.IsDBNull(2) ? "" : reader.GetString(2),
                ExecutionCount = reader.IsDBNull(3) ? 0 : ToInt64(reader.GetValue(3)),
                TotalWorkerTimeSec = reader.IsDBNull(4) ? 0 : ToDouble(reader.GetValue(4)),
                AvgWorkerTimeMs = reader.IsDBNull(5) ? 0 : ToDouble(reader.GetValue(5)),
                TotalElapsedTimeSec = reader.IsDBNull(6) ? 0 : ToDouble(reader.GetValue(6)),
                AvgElapsedTimeMs = reader.IsDBNull(7) ? 0 : ToDouble(reader.GetValue(7)),
                TotalLogicalReads = reader.IsDBNull(8) ? 0 : ToInt64(reader.GetValue(8)),
                AvgLogicalReads = reader.IsDBNull(9) ? 0 : ToInt64(reader.GetValue(9)),
                TotalLogicalWrites = reader.IsDBNull(10) ? 0 : ToInt64(reader.GetValue(10)),
                AvgLogicalWrites = reader.IsDBNull(11) ? 0 : ToInt64(reader.GetValue(11)),
                TotalPhysicalReads = reader.IsDBNull(12) ? 0 : ToInt64(reader.GetValue(12)),
                AvgPhysicalReads = reader.IsDBNull(13) ? 0 : ToInt64(reader.GetValue(13)),
                MaxGrantMb = reader.IsDBNull(14) ? null : ToDouble(reader.GetValue(14)),
                QueryText = reader.IsDBNull(15) ? "" : reader.GetString(15),
                QueryPlanXml = reader.IsDBNull(16) ? null : reader.GetString(16),
                FirstExecutionTime = reader.IsDBNull(17) ? null : reader.GetDateTime(17),
                LastExecutionTime = reader.IsDBNull(18) ? null : reader.GetDateTime(18),
                PlanHandle = reader.IsDBNull(19) ? "" : reader.GetString(19),
            });
        }

        return rows;
    }
}
