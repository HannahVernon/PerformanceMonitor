using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using PerformanceMonitor.Analysis;
using PerformanceMonitor.PlanAnalysis;
using PerformanceMonitorDashboard.Helpers;
using PerformanceMonitorDashboard.Mcp;
using PerformanceMonitorDashboard.Models;
using PerformanceMonitorDashboard.Services;
using PerformanceMonitor.Common;

namespace PerformanceMonitorDashboard.Analysis;

/// <summary>
/// Enriches findings with drill-down data from SQL Server.
/// Runs after graph traversal, only for findings above severity threshold.
/// Each drill-down query is limited to top N results with truncated text.
///
/// This makes analyze_server self-sufficient -- instead of returning a list
/// of "next tools to call," findings include the actual supporting data.
///
/// Port of Lite's DrillDownCollector -- uses SQL Server collect.* tables instead of DuckDB views.
/// No server_id filtering -- Dashboard monitors one server per database.
/// </summary>
public class SqlServerDrillDownCollector
{
    private readonly string _connectionString;
    private readonly IPlanFetcher? _planFetcher;
    private const int TextLimit = 500;

    public SqlServerDrillDownCollector(string connectionString, IPlanFetcher? planFetcher = null)
    {
        _connectionString = connectionString;
        _planFetcher = planFetcher;
    }

    /// <summary>
    /// Enriches each finding's DrillDown dictionary based on its story path.
    /// </summary>
    public async Task EnrichFindingsAsync(List<AnalysisFinding> findings, AnalysisContext context)
    {
        foreach (var finding in findings)
        {
            try
            {
                finding.DrillDown = new Dictionary<string, object>();
                var pathKeys = finding.StoryPath.Split(" → ", StringSplitOptions.RemoveEmptyEntries).ToHashSet();

                /* D7: the config drill-down is a single cheap config-table read and is
                   required to build config/RCSI/db-config Apply actions, which legitimately
                   score below 0.5 (RCSI-off base severity is 0.3). Collect it regardless of
                   the 0.5 display gate. */
                if (pathKeys.Contains("DB_CONFIG"))
                    await CollectConfigIssues(finding, context);

                /* WS3: the percent-autogrowth drill-down is a single cheap config-table read
                   and is required to render the copy-paste MODIFY FILE fix for a
                   FILE_AUTOGROWTH_PERCENT finding, which scores 0.3 (advisory). Collect it
                   regardless of the 0.5 display gate, like the config drill-down above. */
                if (pathKeys.Contains("FILE_AUTOGROWTH_PERCENT"))
                    await CollectAutogrowthPercentFiles(finding, context);

                /* WS3: the server-config drill-down is a single cheap config-table read and is
                   required to build the SERVER_CONFIG Apply/copy-paste action for a CONFIG_*
                   finding, which scores 0.4 (advisory). Collect it regardless of the 0.5 display
                   gate, like the two config drill-downs above. Only the bad setting that rooted
                   THIS finding is emitted (the engine roots one CONFIG_* fact per finding). */
                if (pathKeys.Contains("CONFIG_MAXDOP") || pathKeys.Contains("CONFIG_CTFP")
                    || pathKeys.Contains("CONFIG_MAX_MEMORY_MB") || pathKeys.Contains("CONFIG_MIN_MAX_MEMORY_NARROW"))
                    await CollectServerConfig(finding, context, pathKeys);

                // Below the 0.5 display gate, only the cheap config drill-down above runs;
                // the expensive collectors (plan fetches, multi-row reads) are skipped.
                if (finding.Severity < 0.5)
                {
                    if (finding.DrillDown.Count == 0)
                        finding.DrillDown = null;
                    continue;
                }

                if (pathKeys.Contains("DEADLOCKS"))
                    await CollectTopDeadlocks(finding, context);

                if (pathKeys.Contains("BLOCKING_EVENTS"))
                    await CollectTopBlockingChains(finding, context);

                if (pathKeys.Contains("BLOCKING_CHAIN"))
                    await CollectReconstructedBlockingChains(finding, context);

                if (pathKeys.Contains("CPU_SPIKE"))
                    await CollectQueriesAtSpike(finding, context);

                if (pathKeys.Contains("CPU_SQL_PERCENT") || pathKeys.Contains("CPU_SPIKE"))
                {
                    await CollectTopCpuQueries(finding, context);
                    await CollectAbnormalCpuPlans(finding, context, pathKeys);
                }

                if (pathKeys.Contains("QUERY_SPILLS"))
                    await CollectTopSpillingQueries(finding, context);

                if (   pathKeys.Contains("IO_READ_LATENCY_MS")
                    || pathKeys.Contains("IO_WRITE_LATENCY_MS")
                    )
                    await CollectFileLatencyBreakdown(finding, context);

                if (   pathKeys.Contains("LCK")
                    || pathKeys.Contains("LCK_M_S")
                    || pathKeys.Contains("LCK_M_IS")
                    )
                    await CollectLockModeBreakdown(finding, context);

                if (pathKeys.Contains("TEMPDB_USAGE"))
                    await CollectTempDbBreakdown(finding, context);

                if (pathKeys.Contains("MEMORY_GRANT_PENDING"))
                    await CollectPendingGrants(finding, context);

                if (pathKeys.Any(k => k.StartsWith("BAD_ACTOR_", StringComparison.OrdinalIgnoreCase)))
                    await CollectBadActorDetail(finding, context);

                if (pathKeys.Contains("PARAMETER_SENSITIVITY"))
                    await CollectParameterSensitiveQueries(finding, context);

                if (pathKeys.Contains("PLAN_REGRESSION"))
                    await CollectRegressedQueries(finding, context);

                // Plan analysis: for findings with top queries, analyze their cached plans
                await CollectPlanAnalysis(finding, context);

                // Remove empty drill-down dictionaries
                if (finding.DrillDown.Count == 0)
                    finding.DrillDown = null;
            }
            catch (Exception ex)
            {
                Logger.Error(
                    $"[SqlServerDrillDownCollector] Drill-down failed for {finding.StoryPath}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                // Don't null out -- keep whatever was collected before the error
            }
        }
    }

    private async Task CollectTopDeadlocks(AnalysisFinding finding, AnalysisContext context)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP 3
    collection_time,
    event_date,
    spid,
    LEFT(CAST(query AS NVARCHAR(MAX)), 500) AS victim_sql
FROM collect.deadlocks
WHERE collection_time >= @startTime AND collection_time <= @endTime
ORDER BY collection_time DESC;";

        cmd.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
        cmd.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

        var items = new List<object>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new
            {
                time = reader.IsDBNull(0) ? "" : reader.GetDateTime(0).ToString("o"),
                deadlock_time = reader.IsDBNull(1) ? "" : reader.GetDateTime(1).ToString("o"),
                victim = reader.IsDBNull(2) ? "" : reader.GetValue(2).ToString(),
                victim_sql = reader.IsDBNull(3) ? "" : reader.GetString(3)
            });
        }

        if (items.Count > 0)
            finding.DrillDown!["top_deadlocks"] = items;
    }

    private async Task CollectTopBlockingChains(AnalysisFinding finding, AnalysisContext context)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP 5
    collection_time,
    database_name,
    spid AS blocked_spid,
    0 AS blocking_spid,
    wait_time_ms,
    lock_mode,
    LEFT(CAST(query_text AS NVARCHAR(MAX)), 500) AS blocked_sql,
    LEFT(blocking_tree, 500) AS blocking_sql
FROM collect.blocking_BlockedProcessReport
WHERE collection_time >= @startTime AND collection_time <= @endTime
ORDER BY wait_time_ms DESC;";

        cmd.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
        cmd.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

        var items = new List<object>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new
            {
                time = reader.IsDBNull(0) ? "" : reader.GetDateTime(0).ToString("o"),
                database = reader.IsDBNull(1) ? "" : reader.GetString(1),
                blocked_spid = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2)),
                blocking_spid = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3)),
                wait_time_ms = reader.IsDBNull(4) ? 0L : Convert.ToInt64(reader.GetValue(4)),
                lock_mode = reader.IsDBNull(5) ? "" : reader.GetString(5),
                blocked_sql = reader.IsDBNull(6) ? "" : reader.GetString(6),
                blocking_sql = reader.IsDBNull(7) ? "" : reader.GetString(7)
            });
        }

        if (items.Count > 0)
            finding.DrillDown!["top_blocking_chains"] = items;
    }

    private async Task CollectQueriesAtSpike(AnalysisFinding finding, AnalysisContext context)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Check if query_snapshots table exists (created dynamically by sp_WhoIsActive)
        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT OBJECT_ID(N'collect.query_snapshots', N'U')";
        var tableExists = await checkCmd.ExecuteScalarAsync();
        if (tableExists == null || tableExists == DBNull.Value) return;

        // Step 1: Find when the spike occurred
        using var peakCmd = connection.CreateCommand();
        peakCmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP 1 collection_time, sqlserver_cpu_utilization
FROM collect.cpu_utilization_stats
WHERE collection_time >= @startTime AND collection_time <= @endTime
ORDER BY sqlserver_cpu_utilization DESC;";

        peakCmd.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
        peakCmd.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

        DateTime? peakTime = null;
        int peakCpu = 0;
        using (var peakReader = await peakCmd.ExecuteReaderAsync())
        {
            if (await peakReader.ReadAsync())
            {
                peakTime = peakReader.GetDateTime(0);
                peakCpu = peakReader.GetInt32(1);
            }
        }

        if (peakTime == null) return;

        // Step 2: Get queries active within 2 minutes of peak
        using var queryCmd = connection.CreateCommand();
        queryCmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP 5
    collection_time,
    [session_id],
    [database_name],
    [status],
    DATEDIFF(MILLISECOND, 0, [CPU]) AS cpu_time_ms,
    DATEDIFF(MILLISECOND, 0, [elapsed_time]) AS total_elapsed_time_ms,
    [reads] AS logical_reads,
    [wait_info] AS wait_type,
    0 AS dop,
    0 AS parallel_worker_count,
    LEFT(CAST([sql_text] AS NVARCHAR(MAX)), 500) AS query_text
FROM collect.query_snapshots
WHERE collection_time >= @spikeStart
AND   collection_time <= @spikeEnd
AND   CAST([sql_text] AS NVARCHAR(MAX)) NOT LIKE 'WAITFOR%'
ORDER BY DATEDIFF(MILLISECOND, 0, [CPU]) DESC;";

        queryCmd.Parameters.Add(new SqlParameter("@spikeStart", peakTime.Value.AddMinutes(-2)));
        queryCmd.Parameters.Add(new SqlParameter("@spikeEnd", peakTime.Value.AddMinutes(2)));

        var items = new List<object>();
        using (var reader = await queryCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                items.Add(new
                {
                    time = reader.IsDBNull(0) ? "" : reader.GetDateTime(0).ToString("o"),
                    session_id = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1)),
                    database = reader.IsDBNull(2) ? "" : reader.GetString(2),
                    status = reader.IsDBNull(3) ? "" : reader.GetString(3),
                    cpu_time_ms = reader.IsDBNull(4) ? 0L : Convert.ToInt64(reader.GetValue(4)),
                    elapsed_time_ms = reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5)),
                    logical_reads = reader.IsDBNull(6) ? 0L : Convert.ToInt64(reader.GetValue(6)),
                    wait_type = reader.IsDBNull(7) ? "" : reader.GetString(7),
                    dop = reader.IsDBNull(8) ? 0 : Convert.ToInt32(reader.GetValue(8)),
                    parallel_workers = reader.IsDBNull(9) ? 0 : Convert.ToInt32(reader.GetValue(9)),
                    query_text = reader.IsDBNull(10) ? "" : reader.GetString(10)
                });
            }
        }

        if (items.Count > 0)
        {
            finding.DrillDown!["spike_peak"] = new
            {
                time = peakTime.Value.ToString("o"),
                cpu_percent = peakCpu
            };
            finding.DrillDown!["queries_at_spike"] = items;
        }
    }

    private async Task CollectTopCpuQueries(AnalysisFinding finding, AnalysisContext context)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP 5
    database_name,
    CONVERT(VARCHAR(18), query_hash, 1) AS query_hash,
    CAST(SUM(total_worker_time_delta) AS BIGINT) AS total_cpu_us,
    CAST(SUM(execution_count_delta) AS BIGINT) AS exec_count,
    MAX(max_dop) AS max_dop,
    CAST(SUM(total_spills) AS BIGINT) AS spills,
    LEFT(CAST(DECOMPRESS(MAX(query_text)) AS NVARCHAR(MAX)), 500) AS query_text
FROM collect.query_stats
WHERE collection_time >= @startTime AND collection_time <= @endTime
AND   total_worker_time_delta > 0
GROUP BY database_name, query_hash
ORDER BY CAST(SUM(total_worker_time_delta) AS BIGINT) DESC;";

        cmd.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
        cmd.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

        var items = new List<object>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new
            {
                database = reader.IsDBNull(0) ? "" : reader.GetString(0),
                query_hash = reader.IsDBNull(1) ? "" : reader.GetString(1),
                total_cpu_ms = reader.IsDBNull(2) ? 0.0 : Convert.ToDouble(reader.GetValue(2)) / 1000.0,
                execution_count = reader.IsDBNull(3) ? 0L : Convert.ToInt64(reader.GetValue(3)),
                max_dop = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4)),
                spills = reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5)),
                query_text = reader.IsDBNull(6) ? "" : reader.GetString(6)
            });
        }

        if (items.Count > 0 && !finding.DrillDown!.ContainsKey("top_cpu_queries"))
            finding.DrillDown!["top_cpu_queries"] = items;
    }

    /// <summary>
    /// The plan-cache anomaly detector (§2): one row per offending <c>query_hash</c> whose
    /// CURRENT per-exec CPU has jumped to an abnormal multiple of its OWN trailing baseline,
    /// and which is a material CPU contributor in the window. This is the enrichment the
    /// (PR-B) "Clear cached plan (advanced)" affordance reads. PR-A emits the drill-down but
    /// does NOT register the handler / emit the affordance — dead-code-safe display only.
    ///
    /// <para>
    /// §2a ROW-LEVEL exclusion (the round-2 correctness fix): the delta framework
    /// (install/05_delta_framework.sql:218-307) assigns <c>delta = full cumulative raw
    /// total</c> on TWO arms — first-collection-of-a-plan_handle (the <c>pc.collection_id
    /// IS NULL</c> arm, :233-235) and the first-post-restart row (the
    /// <c>server_start_time &gt;= pc.collection_time</c> arm, :235-236). Both inject false
    /// anomalies on exactly this feature's target population. We exclude BOTH, per row, in
    /// BOTH the current and baseline windows: a row counts as a REAL prior-delta row only
    /// when an earlier collection exists for the same (sql_handle, offsets, plan_handle)
    /// AND that earlier collection_time is &gt; this row's server_start_time (i.e. the
    /// delta interval started at a real prior collection, not at compile/restart). The
    /// per-exec math (M-3) and the materiality CPU sum use ONLY these real-delta rows.
    /// </para>
    /// </summary>
    private async Task CollectAbnormalCpuPlans(AnalysisFinding finding, AnalysisContext context, HashSet<string> pathKeys)
    {
        // The anomaly threshold (sibling of PLAN_REGRESSION's regression_factor) and the
        // materiality floor (a query must contribute at least this much CPU in the window
        // for clearing to be worth offering). Conservative defaults — start ~3x.
        const double AnomalyThreshold = 3.0;
        const double MaterialCpuMsFloor = 1000.0;   // 1s of CPU in the window

        // Co-fired-fact awareness (drives the §5 disclosure steer): whether this CPU
        // finding's story crossed PLAN_REGRESSION / PARAMETER_SENSITIVITY. The analysis
        // already holds the story path — no extra SQL.
        var planRegressionCoFired = pathKeys.Contains("PLAN_REGRESSION");
        var parameterSensitivityCoFired = pathKeys.Contains("PARAMETER_SENSITIVITY");

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        // The baseline window is the @baselineDays preceding the current window (NOT
        // overlapping it). The current window is [@startTime, @endTime].
        cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE @baselineStart datetime2(7) = DATEADD(DAY, -@baselineDays, @startTime);

/*
Real-delta rows only (§2a row-level exclusion): a row is a genuine inter-collection
delta when an EARLIER collection exists for the same (sql_handle, offsets, plan_handle)
whose collection_time is BOTH < this row's collection_time (it is a prior) AND
> this row's server_start_time (so the delta interval did not start at compile/restart).
This drops the first-collection-of-a-plan_handle row and the first-post-restart row in
one predicate, by row, without using sample_interval_seconds (R2-MIN-A).
*/
WITH
    real_delta AS
(
    SELECT
        qs.query_hash,
        qs.database_name,
        qs.collection_time,
        qs.total_worker_time_delta,
        qs.execution_count_delta,
        qs.plan_handle,
        qs.query_text
    FROM collect.query_stats AS qs
    WHERE qs.query_hash IS NOT NULL
    AND   qs.collection_time >= @baselineStart
    AND   qs.collection_time <= @endTime
    AND   EXISTS
          (
              SELECT 1
              FROM collect.query_stats AS prior
              WHERE prior.sql_handle = qs.sql_handle
              AND   prior.statement_start_offset = qs.statement_start_offset
              AND   prior.statement_end_offset = qs.statement_end_offset
              AND   prior.plan_handle = qs.plan_handle
              AND   prior.collection_time < qs.collection_time
              AND   prior.collection_time > qs.server_start_time
          )
),
    windowed AS
(
    SELECT
        rd.query_hash,
        /* current-window per-exec CPU (ms): SUM(worker)/SUM(execs) on real-delta rows */
        current_worker_ms =
            CAST(SUM(CASE WHEN rd.collection_time >= @startTime THEN rd.total_worker_time_delta ELSE 0 END) AS float) / 1000.0,
        current_execs =
            SUM(CASE WHEN rd.collection_time >= @startTime THEN rd.execution_count_delta ELSE 0 END),
        /* baseline-window per-exec CPU (ms): the preceding window, same exclusion */
        baseline_worker_ms =
            CAST(SUM(CASE WHEN rd.collection_time < @startTime THEN rd.total_worker_time_delta ELSE 0 END) AS float) / 1000.0,
        baseline_execs =
            SUM(CASE WHEN rd.collection_time < @startTime THEN rd.execution_count_delta ELSE 0 END),
        /* window CPU contribution (ms), §2a exclusion applied (R2-MIN-B) */
        current_total_cpu_ms =
            CAST(SUM(CASE WHEN rd.collection_time >= @startTime THEN rd.total_worker_time_delta ELSE 0 END) AS float) / 1000.0
    FROM real_delta AS rd
    GROUP BY rd.query_hash
),
    /*
    LOW-1 fix: the window's TOTAL query CPU (ms) over the SAME §2a real-delta rows in the
    current window, so each query's cpu_percent is its real share of query CPU over the
    window (NOT the hardcoded 0 that understated risk in PR-A). Using the same exclusion
    keeps the numerator and denominator consistent (a query can't show a share computed on
    contaminated raw-total CPU it won't actually clear, R2-MIN-B).
    */
    window_total AS
(
    SELECT
        total_cpu_ms =
            CAST(SUM(CASE WHEN rd.collection_time >= @startTime THEN rd.total_worker_time_delta ELSE 0 END) AS float) / 1000.0
    FROM real_delta AS rd
)
SELECT TOP 5
    query_hash = CONVERT(VARCHAR(18), w.query_hash, 1),
    database_name =
    (
        SELECT TOP (1) rd2.database_name
        FROM real_delta AS rd2
        WHERE rd2.query_hash = w.query_hash
        ORDER BY rd2.collection_time DESC
    ),
    current_cpu_per_exec_ms = w.current_worker_ms / NULLIF(w.current_execs, 0),
    baseline_cpu_per_exec_ms = w.baseline_worker_ms / NULLIF(w.baseline_execs, 0),
    anomaly_ratio =
        (w.current_worker_ms / NULLIF(w.current_execs, 0)) /
        NULLIF(w.baseline_worker_ms / NULLIF(w.baseline_execs, 0), 0),
    execution_count = w.current_execs,
    total_cpu_ms = w.current_total_cpu_ms,
    /* LOW-1: real share of the window's total query CPU (rounded int %), 0 when the window
       total is non-positive (degenerate). Display-only — carried into the disclosure. */
    cpu_percent =
        CONVERT(int, ROUND(100.0 * w.current_total_cpu_ms / NULLIF(wt.total_cpu_ms, 0), 0)),
    latest_plan_handle =
    (
        SELECT TOP (1) CONVERT(VARCHAR(130), rd3.plan_handle, 1)
        FROM real_delta AS rd3
        WHERE rd3.query_hash = w.query_hash
        AND   rd3.plan_handle IS NOT NULL
        ORDER BY rd3.collection_time DESC
    ),
    query_text =
    (
        SELECT TOP (1) LEFT(CAST(DECOMPRESS(rd4.query_text) AS NVARCHAR(MAX)), 500)
        FROM real_delta AS rd4
        WHERE rd4.query_hash = w.query_hash
        ORDER BY rd4.collection_time DESC
    )
FROM windowed AS w
CROSS JOIN window_total AS wt
WHERE w.current_execs > 0
AND   w.baseline_execs > 0
AND   w.baseline_worker_ms > 0
AND   w.current_total_cpu_ms >= @materialFloor
/* the anomaly gate: current per-exec >= T x baseline per-exec */
AND   (w.current_worker_ms / NULLIF(w.current_execs, 0)) >=
      @threshold * (w.baseline_worker_ms / NULLIF(w.baseline_execs, 0))
ORDER BY w.current_total_cpu_ms DESC;";

        cmd.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
        cmd.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));
        cmd.Parameters.Add(new SqlParameter("@baselineDays", 7));
        cmd.Parameters.Add(new SqlParameter("@threshold", AnomalyThreshold));
        cmd.Parameters.Add(new SqlParameter("@materialFloor", MaterialCpuMsFloor));

        var items = new List<object>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new
            {
                query_hash = reader.IsDBNull(0) ? "" : reader.GetString(0),
                database = reader.IsDBNull(1) ? "" : reader.GetString(1),
                current_cpu_per_exec_ms = reader.IsDBNull(2) ? 0.0 : Convert.ToDouble(reader.GetValue(2)),
                baseline_cpu_per_exec_ms = reader.IsDBNull(3) ? 0.0 : Convert.ToDouble(reader.GetValue(3)),
                anomaly_ratio = reader.IsDBNull(4) ? 0.0 : Convert.ToDouble(reader.GetValue(4)),
                execution_count = reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5)),
                total_cpu_ms = reader.IsDBNull(6) ? 0.0 : Convert.ToDouble(reader.GetValue(6)),
                // LOW-1 fix: the REAL window CPU share (was hardcoded 0 in PR-A, which made
                // the disclosure render "responsible for 0% of CPU" and understate the risk).
                cpu_percent = reader.IsDBNull(7) ? 0 : Convert.ToInt32(reader.GetValue(7)),
                latest_plan_handle = reader.IsDBNull(8) ? "" : reader.GetString(8),
                query_text = reader.IsDBNull(9) ? "" : reader.GetString(9),
                // §2b co-fired flags (drive the §5 disclosure steer); display-only.
                plan_regression_cofired = planRegressionCoFired,
                parameter_sensitivity_cofired = parameterSensitivityCoFired
            });
        }

        if (items.Count > 0)
            finding.DrillDown!["abnormal_cpu_plans"] = items;
    }

    private async Task CollectTopSpillingQueries(AnalysisFinding finding, AnalysisContext context)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP 5
    database_name,
    CONVERT(VARCHAR(18), query_hash, 1) AS query_hash,
    CAST(SUM(total_spills) AS BIGINT) AS total_spills,
    CAST(SUM(execution_count_delta) AS BIGINT) AS exec_count,
    LEFT(CAST(DECOMPRESS(MAX(query_text)) AS NVARCHAR(MAX)), 500) AS query_text
FROM collect.query_stats
WHERE collection_time >= @startTime AND collection_time <= @endTime
AND   total_spills > 0
GROUP BY database_name, query_hash
ORDER BY CAST(SUM(total_spills) AS BIGINT) DESC;";

        cmd.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
        cmd.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

        var items = new List<object>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new
            {
                database = reader.IsDBNull(0) ? "" : reader.GetString(0),
                query_hash = reader.IsDBNull(1) ? "" : reader.GetString(1),
                total_spills = reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2)),
                execution_count = reader.IsDBNull(3) ? 0L : Convert.ToInt64(reader.GetValue(3)),
                query_text = reader.IsDBNull(4) ? "" : reader.GetString(4)
            });
        }

        if (items.Count > 0)
            finding.DrillDown!["top_spilling_queries"] = items;
    }

    private async Task CollectFileLatencyBreakdown(AnalysisFinding finding, AnalysisContext context)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP 10
    database_name,
    file_type_desc AS file_type,
    AVG(io_stall_read_ms_delta * 1.0 / NULLIF(num_of_reads_delta, 0)) AS avg_read_ms,
    AVG(io_stall_write_ms_delta * 1.0 / NULLIF(num_of_writes_delta, 0)) AS avg_write_ms,
    CAST(SUM(num_of_reads_delta) AS BIGINT) AS total_reads,
    CAST(SUM(num_of_writes_delta) AS BIGINT) AS total_writes
FROM collect.file_io_stats
WHERE collection_time >= @startTime AND collection_time <= @endTime
AND   (num_of_reads_delta > 0 OR num_of_writes_delta > 0)
GROUP BY database_name, file_type_desc
ORDER BY AVG(io_stall_read_ms_delta * 1.0 / NULLIF(num_of_reads_delta, 0)) DESC;";

        cmd.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
        cmd.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

        var items = new List<object>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new
            {
                database = reader.IsDBNull(0) ? "" : reader.GetString(0),
                file_type = reader.IsDBNull(1) ? "" : reader.GetString(1),
                avg_read_latency_ms = reader.IsDBNull(2) ? 0.0 : Math.Round(Convert.ToDouble(reader.GetValue(2)), 2),
                avg_write_latency_ms = reader.IsDBNull(3) ? 0.0 : Math.Round(Convert.ToDouble(reader.GetValue(3)), 2),
                total_reads = reader.IsDBNull(4) ? 0L : Convert.ToInt64(reader.GetValue(4)),
                total_writes = reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5))
            });
        }

        if (items.Count > 0)
            finding.DrillDown!["file_latency_breakdown"] = items;
    }

    private async Task CollectLockModeBreakdown(AnalysisFinding finding, AnalysisContext context)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP 10
    wait_type,
    CAST(SUM(wait_time_ms_delta) AS BIGINT) AS total_wait_ms,
    CAST(SUM(waiting_tasks_count_delta) AS BIGINT) AS total_count
FROM collect.wait_stats
WHERE collection_time >= @startTime AND collection_time <= @endTime
AND   wait_type LIKE 'LCK%'
AND   wait_time_ms_delta > 0
GROUP BY wait_type
ORDER BY CAST(SUM(wait_time_ms_delta) AS BIGINT) DESC;";

        cmd.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
        cmd.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

        var items = new List<object>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new
            {
                lock_type = reader.IsDBNull(0) ? "" : reader.GetString(0),
                total_wait_ms = reader.IsDBNull(1) ? 0.0 : Convert.ToDouble(reader.GetValue(1)),
                waiting_tasks = reader.IsDBNull(2) ? 0.0 : Convert.ToDouble(reader.GetValue(2))
            });
        }

        if (items.Count > 0)
            finding.DrillDown!["lock_mode_breakdown"] = items;
    }

    private async Task CollectConfigIssues(AnalysisFinding finding, AnalysisContext context)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // The Dashboard uses config.database_configuration_history which stores
        // settings as rows (setting_type, setting_name, setting_value) not columns.
        // Pivot the latest snapshot into the format we need.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

;WITH latest AS (
    SELECT database_name, setting_name,
           CAST(setting_value AS NVARCHAR(256)) AS setting_value,
           ROW_NUMBER() OVER (PARTITION BY database_name, setting_name ORDER BY collection_time DESC) AS rn
    FROM config.database_configuration_history
    WHERE setting_name IN (
        'recovery_model_desc', 'is_auto_shrink_on', 'is_auto_close_on',
        'is_read_committed_snapshot_on', 'page_verify_option_desc', 'is_query_store_on'
    )
),
pivoted AS (
    SELECT
        database_name,
        MAX(CASE WHEN setting_name = 'recovery_model_desc' THEN setting_value END) AS recovery_model,
        MAX(CASE WHEN setting_name = 'is_auto_shrink_on' THEN setting_value END) AS is_auto_shrink_on,
        MAX(CASE WHEN setting_name = 'is_auto_close_on' THEN setting_value END) AS is_auto_close_on,
        MAX(CASE WHEN setting_name = 'is_read_committed_snapshot_on' THEN setting_value END) AS is_rcsi_on,
        MAX(CASE WHEN setting_name = 'page_verify_option_desc' THEN setting_value END) AS page_verify_option,
        MAX(CASE WHEN setting_name = 'is_query_store_on' THEN setting_value END) AS is_query_store_on
    FROM latest
    WHERE rn = 1
    GROUP BY database_name
)
SELECT database_name, recovery_model,
       is_auto_shrink_on, is_auto_close_on,
       is_rcsi_on, page_verify_option, is_query_store_on
FROM pivoted
WHERE is_auto_shrink_on = '1' OR is_auto_close_on = '1'
   OR is_rcsi_on = '0' OR page_verify_option != 'CHECKSUM'
ORDER BY database_name;";

        // Read the pivoted rows first into a typed buffer so we can determine the
        // RCSI-OFF set BEFORE building the emitted items — the §3.3 enrichment is
        // computed only for RCSI-off databases and injected into those rows.
        var rows = new List<ConfigIssueRow>();
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add(new ConfigIssueRow
                {
                    Database = reader.IsDBNull(0) ? "" : reader.GetString(0),
                    RecoveryModel = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    AutoShrink = (reader.IsDBNull(2) ? "" : reader.GetString(2)) == "1",
                    AutoClose = (reader.IsDBNull(3) ? "" : reader.GetString(3)) == "1",
                    Rcsi = (reader.IsDBNull(4) ? "" : reader.GetString(4)) == "1",
                    PageVerify = reader.IsDBNull(5) ? "" : reader.GetString(5),
                    QueryStore = (reader.IsDBNull(6) ? "" : reader.GetString(6)) == "1"
                });
            }
        }

        // §3.3 enrichment: per-DB blocking/deadlock counts + reader/writer split, ONLY
        // for RCSI-off databases, over the analysis window, from already-collected
        // monitoring tables (no fresh probe of the target server).
        var rcsiOff = rows.Where(r => !r.Rcsi && !string.IsNullOrEmpty(r.Database))
                          .Select(r => r.Database)
                          .ToList();
        var enrichment = rcsiOff.Count > 0
            ? await CollectRcsiInactionFigures(connection, rcsiOff, context)
            : new Dictionary<string, RcsiInactionFigures>(StringComparer.Ordinal);

        var items = new List<object>();
        foreach (var r in rows)
        {
            var issues = new List<string>();
            if (r.AutoShrink) issues.Add("auto_shrink ON");
            if (r.AutoClose) issues.Add("auto_close ON");
            if (!r.Rcsi) issues.Add("RCSI OFF");
            if (!string.IsNullOrEmpty(r.PageVerify) && r.PageVerify != "CHECKSUM") issues.Add($"page_verify={r.PageVerify}");

            // RCSI-off rows carry the three structured inaction-risk fields (M-2:
            // identical JSON names + types to Lite, which emits them null/0). RCSI-on
            // rows omit them entirely (the affordance never applies there).
            if (!r.Rcsi)
            {
                enrichment.TryGetValue(r.Database, out var fig);
                items.Add(new
                {
                    database = r.Database,
                    recovery_model = r.RecoveryModel,
                    rcsi = r.Rcsi,
                    query_store = r.QueryStore,
                    issues,
                    auto_shrink = r.AutoShrink,
                    auto_close = r.AutoClose,
                    page_verify = r.PageVerify,
                    // §3.3: int / int / nullable-int. Counts from blocking_deadlock_stats
                    // (O-P3-F — NOT a blocking_BlockedProcessReport row count); the split
                    // is a separate pass over blocking_BlockedProcessReport.
                    rcsi_blocking_events = fig?.BlockingEvents ?? 0,
                    rcsi_deadlocks = fig?.Deadlocks ?? 0,
                    rcsi_reader_writer_pct = fig?.ReaderWriterPct
                });
            }
            else
            {
                items.Add(new
                {
                    database = r.Database,
                    recovery_model = r.RecoveryModel,
                    rcsi = r.Rcsi,
                    query_store = r.QueryStore,
                    issues,
                    // §4.1: structured, wording-independent fields the shared extractor
                    // (FactRemediation.ExtractDbConfigTargets) reads. Identical JSON names
                    // and types to the Lite collector (bool / bool / string).
                    auto_shrink = r.AutoShrink,
                    auto_close = r.AutoClose,
                    page_verify = r.PageVerify
                });
            }
        }

        if (items.Count > 0)
            finding.DrillDown!["config_issues"] = items;
    }

    /// <summary>
    /// Lists the large (&gt;= 10 GB) data/log files on PERCENTAGE autogrowth (WS3), latest
    /// snapshot per file, excluding system databases — and attaches a copy-paste
    /// <c>ALTER DATABASE ... MODIFY FILE</c> fix per file (FILEGROWTH set to a size-tiered
    /// fixed MB). The structured fields (<c>database</c>, <c>logical_file_name</c>,
    /// <c>total_size_mb</c>, <c>growth_pct</c>) are what the shared extractor
    /// (FactRemediation.ExtractFileGrowthTargets) reads; the rendered <c>alter_statement</c>
    /// uses the SHARED renderer so it is byte-identical to the reader's copy-paste rebuild.
    /// Advisory only — no Apply.
    /// </summary>
    private async Task CollectAutogrowthPercentFiles(AnalysisFinding finding, AnalysisContext context)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

;WITH latest AS (
    SELECT
        database_name,
        file_id,
        file_type_desc,
        file_name,
        total_size_mb,
        is_percent_growth,
        growth_pct,
        ROW_NUMBER() OVER (PARTITION BY database_name, file_id ORDER BY collection_time DESC) AS rn
    FROM collect.database_size_stats
    WHERE database_name NOT IN ('master', 'msdb', 'model', 'tempdb')
)
SELECT TOP (50)
    database_name,
    file_type_desc,
    file_name,
    total_size_mb,
    growth_pct
FROM latest
WHERE rn = 1
AND   is_percent_growth = 1
AND   total_size_mb >= @minSizeMb
ORDER BY total_size_mb DESC;";

        cmd.Parameters.Add(new SqlParameter("@minSizeMb", 10240.0)); /* 10 GB */

        var items = new List<object>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var database = reader.IsDBNull(0) ? "" : reader.GetString(0);
            var fileType = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var logical = reader.IsDBNull(2) ? "" : reader.GetString(2);
            var sizeMb = reader.IsDBNull(3) ? 0.0 : Convert.ToDouble(reader.GetValue(3));
            var growthPct = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4));
            if (string.IsNullOrEmpty(database) || string.IsNullOrEmpty(logical)) continue;

            var growthMb = FactRemediation.RecommendedGrowthMbFor(fileType);
            items.Add(new
            {
                database,
                logical_file_name = logical,
                file_type = fileType,
                total_size_mb = sizeMb,
                growth_pct = growthPct,
                issue = $"{growthPct}% autogrowth on {sizeMb / 1024.0:N1} GB {fileType} file",
                alter_statement = FactRemediation.BuildModifyFileStatement(database, logical, growthMb)
            });
        }

        if (items.Count > 0)
            finding.DrillDown!["autogrowth_percent_files"] = items;
    }

    /// <summary>
    /// Emits the <c>server_config</c> drill-down for a CONFIG_* finding (WS3): the single bad
    /// server-level setting that rooted this finding, with its latest <c>value_in_use</c> plus the
    /// server's <c>engine_edition</c> and <c>cores_per_socket</c> (needed to compute the
    /// edition-aware, core-capped MAXDOP recommendation in
    /// <see cref="FactRemediation.ExtractServerConfigTargets"/>). The structured fields
    /// (<c>setting</c>, <c>current_value</c>, <c>edition</c>, <c>cores_per_socket</c>) are what the
    /// shared extractor reads; nothing here is executed. The four CONFIG_* keys root SEPARATE
    /// findings, so each finding emits exactly the one row for its own setting.
    /// </summary>
    private async Task CollectServerConfig(AnalysisFinding finding, AnalysisContext context, HashSet<string> pathKeys)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        // Latest value per requested config (ROW_NUMBER, not TOP N — same dedup correctness as the
        // fact collector), plus the latest server_properties row for edition + cores-per-socket.
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

;WITH latest_config AS (
    SELECT
        configuration_name,
        CAST(value_in_use AS BIGINT) AS value_in_use,
        ROW_NUMBER() OVER (PARTITION BY configuration_name ORDER BY collection_time DESC) AS rn
    FROM config.server_configuration_history
    WHERE configuration_name IN (
        'cost threshold for parallelism',
        'max degree of parallelism',
        'max server memory (MB)',
        'min server memory (MB)'
    )
)
SELECT
    configuration_name,
    value_in_use
FROM latest_config
WHERE rn = 1;

SELECT TOP (1)
    engine_edition,
    cores_per_socket
FROM collect.server_properties
ORDER BY collection_time DESC;";

        long? ctfp = null, maxdop = null, maxMem = null, minMem = null;
        int edition = 0, coresPerSocket = 0;

        using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var name = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var value = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1));
                switch (name)
                {
                    case "cost threshold for parallelism": ctfp = value; break;
                    case "max degree of parallelism": maxdop = value; break;
                    case "max server memory (MB)": maxMem = value; break;
                    case "min server memory (MB)": minMem = value; break;
                }
            }

            if (await reader.NextResultAsync() && await reader.ReadAsync())
            {
                edition = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                coresPerSocket = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
            }
        }

        var items = new List<object>();

        // Emit ONLY the setting that rooted this finding (the engine roots one CONFIG_* per finding).
        // edition / cores_per_socket ride every row (the extractor uses them only for MAXDOP).
        if (pathKeys.Contains("CONFIG_MAXDOP") && maxdop is { } md)
            items.Add(new { setting = "maxdop", current_value = md, edition, cores_per_socket = coresPerSocket });

        if (pathKeys.Contains("CONFIG_CTFP") && ctfp is { } ct)
            items.Add(new { setting = "ctfp", current_value = ct, edition, cores_per_socket = coresPerSocket });

        if (pathKeys.Contains("CONFIG_MAX_MEMORY_MB") && maxMem is { } mx)
            items.Add(new { setting = "max_memory", current_value = mx, edition, cores_per_socket = coresPerSocket });

        // For the narrow-memory finding the bad value the operator acts on is MIN server memory
        // (lower it); carry it as the min_memory target.
        if (pathKeys.Contains("CONFIG_MIN_MAX_MEMORY_NARROW") && minMem is { } mn)
            items.Add(new { setting = "min_memory", current_value = mn, edition, cores_per_socket = coresPerSocket });

        if (items.Count > 0)
            finding.DrillDown!["server_config"] = items;
    }

    private sealed class ConfigIssueRow
    {
        public string Database = "";
        public string RecoveryModel = "";
        public bool AutoShrink;
        public bool AutoClose;
        public bool Rcsi;
        public string PageVerify = "";
        public bool QueryStore;
    }

    private sealed class RcsiInactionFigures
    {
        public int BlockingEvents;
        public int Deadlocks;
        public int? ReaderWriterPct;
    }

    /// <summary>
    /// Computes, per RCSI-off database, the §3.3 inaction-risk figures over the
    /// analysis window: blocking/deadlock counts from the PRE-AGGREGATED
    /// collect.blocking_deadlock_stats (O-P3-F — never a blocking_BlockedProcessReport
    /// row count), and the reader-vs-writer share from a SEPARATE pass over
    /// collect.blocking_BlockedProcessReport classified against the FULL lock_mode
    /// vocabulary (M-1). All reads are parameterized on the time window; database names
    /// are bound as a parameterized TVP-free IN list (literal-free).
    /// </summary>
    private static async Task<Dictionary<string, RcsiInactionFigures>> CollectRcsiInactionFigures(
        SqlConnection connection, List<string> databases, AnalysisContext context)
    {
        var result = new Dictionary<string, RcsiInactionFigures>(StringComparer.Ordinal);
        foreach (var d in databases)
            result[d] = new RcsiInactionFigures();

        // Pass 1 — counts from the pre-aggregated stats table (per-DB, per-window).
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    bds.database_name,
    blocking_events = SUM(bds.blocking_event_count),
    deadlocks = SUM(bds.deadlock_count)
FROM collect.blocking_deadlock_stats AS bds
WHERE bds.collection_time >= @startTime
AND   bds.collection_time <= @endTime
GROUP BY bds.database_name;";
            cmd.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
            cmd.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var db = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (!result.TryGetValue(db, out var fig)) continue;
                fig.BlockingEvents = reader.IsDBNull(1) ? 0 : (int)Math.Min(int.MaxValue, Convert.ToInt64(reader.GetValue(1)));
                fig.Deadlocks = reader.IsDBNull(2) ? 0 : (int)Math.Min(int.MaxValue, Convert.ToInt64(reader.GetValue(2)));
            }
        }

        // Pass 2 — reader-vs-writer split over the blocked-process reports, classified
        // against the FULL lock_mode vocabulary (M-1). Reader-side = S/IS/RangeS-*;
        // writer-side = X/IX/U/UIX/RangeX-*/RangeI-*; Sch-S/Sch-M/BU EXCLUDED from the
        // denominator (RCSI-irrelevant). pct = 100 * reader / NULLIF(reader+writer, 0).
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

WITH classified AS
(
    SELECT
        bpr.database_name,
        is_reader =
            CASE
                WHEN bpr.lock_mode IN (N'S', N'IS') THEN 1
                WHEN bpr.lock_mode LIKE N'RangeS-%' THEN 1
                ELSE 0
            END,
        is_writer =
            CASE
                WHEN bpr.lock_mode IN (N'X', N'IX', N'U', N'UIX') THEN 1
                WHEN bpr.lock_mode LIKE N'RangeX-%' THEN 1
                WHEN bpr.lock_mode LIKE N'RangeI-%' THEN 1
                ELSE 0
            END
    FROM collect.blocking_BlockedProcessReport AS bpr
    WHERE bpr.event_time >= @startTime
    AND   bpr.event_time <= @endTime
    AND   bpr.database_name IS NOT NULL
    /* EXCLUDE Sch-S / Sch-M / BU from the denominator (RCSI-irrelevant). */
    AND   bpr.lock_mode NOT IN (N'Sch-S', N'Sch-M', N'BU')
)
SELECT
    c.database_name,
    reader_count = SUM(CONVERT(bigint, c.is_reader)),
    writer_count = SUM(CONVERT(bigint, c.is_writer))
FROM classified AS c
WHERE c.is_reader = 1 OR c.is_writer = 1
GROUP BY c.database_name;";
            cmd.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
            cmd.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var db = reader.IsDBNull(0) ? "" : reader.GetString(0);
                if (!result.TryGetValue(db, out var fig)) continue;
                var readerCount = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1));
                var writerCount = reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2));
                var denom = readerCount + writerCount;
                fig.ReaderWriterPct = denom > 0 ? (int)(100 * readerCount / denom) : (int?)null;
            }
        }

        return result;
    }

    private async Task CollectTempDbBreakdown(AnalysisFinding finding, AnalysisContext context)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP 5
    collection_time,
    user_object_reserved_mb,
    internal_object_reserved_mb,
    version_store_reserved_mb,
    unallocated_mb
FROM collect.tempdb_stats
WHERE collection_time >= @startTime AND collection_time <= @endTime
ORDER BY (user_object_reserved_mb + internal_object_reserved_mb + version_store_reserved_mb) DESC;";

        cmd.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
        cmd.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

        var items = new List<object>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new
            {
                time = reader.GetDateTime(0).ToString("o"),
                user_objects_mb = reader.IsDBNull(1) ? 0.0 : Convert.ToDouble(reader.GetValue(1)),
                internal_objects_mb = reader.IsDBNull(2) ? 0.0 : Convert.ToDouble(reader.GetValue(2)),
                version_store_mb = reader.IsDBNull(3) ? 0.0 : Convert.ToDouble(reader.GetValue(3)),
                unallocated_mb = reader.IsDBNull(4) ? 0.0 : Convert.ToDouble(reader.GetValue(4))
            });
        }

        if (items.Count > 0)
            finding.DrillDown!["tempdb_breakdown"] = items;
    }

    private async Task CollectPendingGrants(AnalysisFinding finding, AnalysisContext context)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP 5
    collection_time,
    target_memory_mb, total_memory_mb, available_memory_mb,
    granted_memory_mb, used_memory_mb,
    grantee_count, waiter_count,
    timeout_error_count_delta, forced_grant_count_delta
FROM collect.memory_grant_stats
WHERE collection_time >= @startTime AND collection_time <= @endTime
AND   waiter_count > 0
ORDER BY waiter_count DESC;";

        cmd.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
        cmd.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

        var items = new List<object>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new
            {
                time = reader.IsDBNull(0) ? "" : reader.GetDateTime(0).ToString("o"),
                target_memory_mb = reader.IsDBNull(1) ? 0.0 : Convert.ToDouble(reader.GetValue(1)),
                total_memory_mb = reader.IsDBNull(2) ? 0.0 : Convert.ToDouble(reader.GetValue(2)),
                available_memory_mb = reader.IsDBNull(3) ? 0.0 : Convert.ToDouble(reader.GetValue(3)),
                granted_memory_mb = reader.IsDBNull(4) ? 0.0 : Convert.ToDouble(reader.GetValue(4)),
                used_memory_mb = reader.IsDBNull(5) ? 0.0 : Convert.ToDouble(reader.GetValue(5)),
                grantee_count = reader.IsDBNull(6) ? 0 : reader.GetInt32(6),
                waiter_count = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                timeout_errors = reader.IsDBNull(8) ? 0L : Convert.ToInt64(reader.GetValue(8)),
                forced_grants = reader.IsDBNull(9) ? 0L : Convert.ToInt64(reader.GetValue(9))
            });
        }

        if (items.Count > 0)
            finding.DrillDown!["pending_grants"] = items;
    }

    /// <summary>
    /// For findings that have query hashes (bad actors), fetch the execution plan
    /// live from SQL Server via IPlanFetcher, then run PlanAnalyzer to surface
    /// warnings and missing indexes. No plan storage needed -- fetch on demand
    /// only for queries that make it into high-impact findings.
    /// </summary>
    private async Task CollectPlanAnalysis(AnalysisFinding finding, AnalysisContext context)
    {
        if (finding.DrillDown == null || _planFetcher == null) return;

        // Only analyze plans for bad actor findings (1 plan each).
        // Skip top_cpu_queries (5 plans would be too heavy).
        if (!finding.RootFactKey.StartsWith("BAD_ACTOR_", StringComparison.OrdinalIgnoreCase)) return;

        var queryHash = finding.RootFactKey.Replace("BAD_ACTOR_", "");
        if (string.IsNullOrEmpty(queryHash)) return;

        // Look up plan_handle from collect.query_stats for this query_hash
        string? planHandle = null;
        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP 1 CONVERT(VARCHAR(130), plan_handle, 1) AS plan_handle
FROM collect.query_stats
WHERE query_hash = CONVERT(BINARY(8), @queryHash, 1)
AND   plan_handle IS NOT NULL
ORDER BY collection_time DESC;";

            cmd.Parameters.Add(new SqlParameter("@queryHash", queryHash));

            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync() && !reader.IsDBNull(0))
                planHandle = reader.GetString(0);
        }
        catch { return; }

        if (string.IsNullOrEmpty(planHandle)) return;

        // Fetch plan XML live from SQL Server
        var planXml = await _planFetcher.FetchPlanXmlAsync(context.ServerId, planHandle);
        if (string.IsNullOrEmpty(planXml)) return;

        try
        {
            var plan = ShowPlanParser.Parse(planXml);
            PlanAnalyzer.Analyze(plan);

            var allWarnings = plan.Batches
                .SelectMany(b => b.Statements)
                .Where(s => s.RootNode != null)
                .SelectMany(s =>
                {
                    var nodeWarnings = new List<PlanNode>();
                    CollectPlanNodes(s.RootNode!, nodeWarnings);
                    return s.PlanWarnings
                        .Concat(nodeWarnings.SelectMany(n => n.Warnings));
                })
                .ToList();

            var missingIndexes = plan.AllMissingIndexes;

            if (allWarnings.Count == 0 && missingIndexes.Count == 0) return;

            finding.DrillDown["plan_analysis"] = new
            {
                query_hash = queryHash,
                warning_count = allWarnings.Count,
                critical_count = allWarnings.Count(w => w.Severity == PlanWarningSeverity.Critical),
                warnings = allWarnings
                    .OrderByDescending(w => w.Severity)
                    .Take(10)
                    .Select(w => new
                    {
                        severity = w.Severity.ToString(),
                        type = w.WarningType,
                        message = McpHelpers.Truncate(w.Message, 300)
                    }),
                missing_indexes = missingIndexes.Take(5).Select(idx => new
                {
                    table = $"{idx.Schema}.{idx.Table}",
                    impact = idx.Impact,
                    create_statement = idx.CreateStatement
                })
            };
        }
        catch
        {
            // Plan parsing can fail on malformed XML -- skip silently
        }
    }

    private static void CollectPlanNodes(PlanNode node, List<PlanNode> nodes)
    {
        nodes.Add(node);
        foreach (var child in node.Children)
            CollectPlanNodes(child, nodes);
    }

    private async Task CollectBadActorDetail(AnalysisFinding finding, AnalysisContext context)
    {
        // Extract query_hash from the fact key (BAD_ACTOR_0x...)
        var queryHash = finding.RootFactKey.Replace("BAD_ACTOR_", "");
        if (string.IsNullOrEmpty(queryHash)) return;

        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    database_name,
    CONVERT(VARCHAR(18), query_hash, 1) AS query_hash,
    LEFT(CAST(DECOMPRESS(MAX(query_text)) AS NVARCHAR(MAX)), 500) AS query_text,
    CAST(SUM(execution_count_delta) AS BIGINT) AS exec_count,
    CASE WHEN SUM(execution_count_delta) > 0
         THEN CAST(SUM(total_worker_time_delta) AS FLOAT) / SUM(execution_count_delta) / 1000.0
         ELSE 0 END AS avg_cpu_ms,
    CASE WHEN SUM(execution_count_delta) > 0
         THEN CAST(SUM(total_elapsed_time_delta) AS FLOAT) / SUM(execution_count_delta) / 1000.0
         ELSE 0 END AS avg_elapsed_ms,
    CASE WHEN SUM(execution_count_delta) > 0
         THEN CAST(SUM(total_logical_reads_delta) AS FLOAT) / SUM(execution_count_delta)
         ELSE 0 END AS avg_reads,
    CAST(SUM(total_worker_time_delta) AS BIGINT) AS total_cpu_us,
    CAST(SUM(total_logical_reads_delta) AS BIGINT) AS total_reads,
    CAST(SUM(total_spills) AS BIGINT) AS total_spills,
    MAX(max_dop) AS max_dop
FROM collect.query_stats
WHERE collection_time >= @startTime
AND   collection_time <= @endTime
AND   query_hash = CONVERT(BINARY(8), @queryHash, 1)
GROUP BY database_name, query_hash;";

        cmd.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
        cmd.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));
        cmd.Parameters.Add(new SqlParameter("@queryHash", queryHash));

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            finding.DrillDown!["bad_actor_query"] = new
            {
                database = reader.IsDBNull(0) ? "" : reader.GetString(0),
                query_hash = reader.IsDBNull(1) ? "" : reader.GetString(1),
                query_text = reader.IsDBNull(2) ? "" : reader.GetString(2),
                execution_count = reader.IsDBNull(3) ? 0L : Convert.ToInt64(reader.GetValue(3)),
                avg_cpu_ms = reader.IsDBNull(4) ? 0.0 : Math.Round(Convert.ToDouble(reader.GetValue(4)), 2),
                avg_elapsed_ms = reader.IsDBNull(5) ? 0.0 : Math.Round(Convert.ToDouble(reader.GetValue(5)), 2),
                avg_reads = reader.IsDBNull(6) ? 0.0 : Math.Round(Convert.ToDouble(reader.GetValue(6)), 0),
                total_cpu_ms = reader.IsDBNull(7) ? 0.0 : Convert.ToDouble(reader.GetValue(7)) / 1000.0,
                total_reads = reader.IsDBNull(8) ? 0L : Convert.ToInt64(reader.GetValue(8)),
                total_spills = reader.IsDBNull(9) ? 0L : Convert.ToInt64(reader.GetValue(9)),
                max_dop = reader.IsDBNull(10) ? 0 : Convert.ToInt32(reader.GetValue(10))
            };
        }
    }

    /// <summary>
    /// Top parameter-sensitive plans behind a PARAMETER_SENSITIVITY finding.
    /// Re-runs the detector for the top 5 offenders.
    /// </summary>
    private async Task CollectParameterSensitiveQueries(AnalysisFinding finding, AnalysisContext context)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

WITH latest AS
(
    SELECT
        database_name,
        query_hash,
        query_plan_hash,
        execution_count,
        creation_time,
        min_worker_time,
        max_worker_time,
        min_grant_kb,
        max_grant_kb,
        min_spills,
        max_spills,
        query_text,
        ROW_NUMBER() OVER
        (
            PARTITION BY database_name, query_hash, query_plan_hash
            ORDER BY collection_time DESC
        ) AS rn
    FROM collect.query_stats
    WHERE collection_time >= @startTime
    AND   collection_time <= @endTime
    AND   execution_count_delta > 0
)
SELECT
    database_name,
    CONVERT(varchar(18), query_hash, 1) AS query_hash,
    CONVERT(varchar(18), query_plan_hash, 1) AS query_plan_hash,
    execution_count,
    min_worker_time,
    max_worker_time,
    CAST(max_worker_time AS float) / NULLIF(min_worker_time, 0) AS worker_ratio,
    CAST(max_grant_kb AS float) / NULLIF(min_grant_kb, 0) AS grant_ratio,
    CASE WHEN max_spills > 0 AND min_spills = 0 THEN 1 ELSE 0 END AS spill_divergence,
    LEFT(CAST(DECOMPRESS(query_text) AS NVARCHAR(MAX)), 500) AS query_text
FROM latest
WHERE rn = 1
AND   min_worker_time >= 10000
AND   max_worker_time >= 250000
AND   execution_count >= 20
AND   creation_time <= @startTime
AND   CAST(max_worker_time AS float) / NULLIF(min_worker_time, 0) >= 10
ORDER BY worker_ratio DESC
OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY";

        cmd.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
        cmd.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

        var items = new List<object>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new
            {
                database = reader.IsDBNull(0) ? "" : reader.GetString(0),
                query_hash = reader.IsDBNull(1) ? "" : reader.GetString(1),
                query_plan_hash = reader.IsDBNull(2) ? "" : reader.GetString(2),
                execution_count = reader.IsDBNull(3) ? 0L : Convert.ToInt64(reader.GetValue(3)),
                min_worker_time_us = reader.IsDBNull(4) ? 0L : Convert.ToInt64(reader.GetValue(4)),
                max_worker_time_us = reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5)),
                worker_ratio = reader.IsDBNull(6) ? 0.0 : Convert.ToDouble(reader.GetValue(6)),
                grant_ratio = reader.IsDBNull(7) ? 0.0 : Convert.ToDouble(reader.GetValue(7)),
                spills_on_some_inputs = !reader.IsDBNull(8) && Convert.ToInt32(reader.GetValue(8)) == 1,
                query_text = reader.IsDBNull(9) ? "" : reader.GetString(9)
            });
        }

        if (items.Count > 0)
            finding.DrillDown!["parameter_sensitive_queries"] = items;
    }

    /// <summary>
    /// Top regressed queries behind a PLAN_REGRESSION finding.
    /// Uses the same 14-day server_last_execution_time comparison window as the detector
    /// (NOT the standard analysis window) so the days-old "best plan" baseline is present.
    /// </summary>
    private async Task CollectRegressedQueries(AnalysisFinding finding, AnalysisContext context)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

WITH deduped AS
(
    SELECT
        database_name,
        query_id,
        plan_id,
        query_plan_hash,
        count_executions,
        avg_cpu_time,
        avg_duration,
        server_last_execution_time,
        ROW_NUMBER() OVER
        (
            PARTITION BY database_name, query_id, plan_id, server_first_execution_time
            ORDER BY collection_time DESC
        ) AS rn
    FROM collect.query_store_data
    WHERE execution_type_desc = N'Regular'
    AND   server_last_execution_time >= @windowStart
),
plan_agg AS
(
    -- query_plan_hash is invariant within a plan_id, so include it in the GROUP BY
    -- (MS Learn's MAX page does not list binary/varbinary in the accepted types).
    SELECT
        database_name,
        query_id,
        plan_id,
        query_plan_hash,
        SUM(count_executions) AS execs,
        CASE WHEN SUM(count_executions) > 0
             THEN SUM(avg_cpu_time * count_executions) / NULLIF(SUM(count_executions), 0)
             ELSE 0 END AS cpu_per_exec,
        CASE WHEN SUM(count_executions) > 0
             THEN SUM(avg_duration * count_executions) / NULLIF(SUM(count_executions), 0)
             ELSE 0 END AS dur_per_exec,
        MAX(server_last_execution_time) AS last_exec
    FROM deduped
    WHERE rn = 1
    GROUP BY database_name, query_id, plan_id, query_plan_hash
),
plan_dedup AS
(
    -- MAX(plan_id) carries the most recently observed plan_id in the hash partition
    -- forward — newer plans are less likely evicted by Query Store retention.
    -- Functionally any plan_id sharing the hash forces the same execution shape.
    SELECT
        database_name,
        query_id,
        query_plan_hash,
        MAX(plan_id) AS plan_id,
        SUM(execs) AS execs,
        CASE WHEN SUM(execs) > 0
             THEN SUM(cpu_per_exec * execs) / NULLIF(SUM(execs), 0)
             ELSE 0 END AS cpu_per_exec,
        CASE WHEN SUM(execs) > 0
             THEN SUM(dur_per_exec * execs) / NULLIF(SUM(execs), 0)
             ELSE 0 END AS dur_per_exec,
        MAX(last_exec) AS last_exec
    FROM plan_agg
    GROUP BY database_name, query_id, query_plan_hash
    HAVING SUM(execs) >= 25
),
ranked AS
(
    SELECT
        *,
        ROW_NUMBER() OVER (PARTITION BY database_name, query_id ORDER BY last_exec DESC) AS recency,
        ROW_NUMBER() OVER (PARTITION BY database_name, query_id ORDER BY cpu_per_exec ASC) AS cheapness
    FROM plan_dedup
),
compared AS
(
    SELECT
        l.database_name,
        l.query_id,
        l.query_plan_hash AS latest_plan_hash,
        l.cpu_per_exec AS latest_cpu,
        l.dur_per_exec AS latest_dur,
        b.query_plan_hash AS best_plan_hash,
        b.plan_id AS best_plan_id,
        b.cpu_per_exec AS best_cpu,
        b.dur_per_exec AS best_dur,
        (SELECT MAX(v)
         FROM (VALUES
             (CAST(l.cpu_per_exec AS float) / NULLIF(b.cpu_per_exec, 0)),
             (CAST(l.dur_per_exec AS float) / NULLIF(b.dur_per_exec, 0))
         ) AS x(v)) AS regression_factor
    FROM ranked AS l
    JOIN ranked AS b
      ON  b.database_name = l.database_name
      AND b.query_id = l.query_id
      AND b.cheapness = 1
    WHERE l.recency = 1
    AND   l.query_plan_hash <> b.query_plan_hash
)
SELECT
    c.database_name,
    c.query_id,
    CONVERT(varchar(18), c.latest_plan_hash, 1) AS latest_plan_hash,
    c.latest_cpu,
    c.latest_dur,
    CONVERT(varchar(18), c.best_plan_hash, 1) AS best_plan_hash,
    c.best_plan_id,
    c.best_cpu,
    c.best_dur,
    c.regression_factor,
    -- query_sql_text is varbinary(max); fetch it via APPLY (MAX() on varbinary(max) is invalid).
    LEFT(CAST(DECOMPRESS(qt.query_sql_text) AS NVARCHAR(MAX)), 500) AS query_text
FROM compared AS c
OUTER APPLY
(
    SELECT TOP (1) qs.query_sql_text
    FROM collect.query_store_data AS qs
    WHERE qs.database_name = c.database_name
    AND   qs.query_id = c.query_id
    AND   qs.query_plan_hash = c.latest_plan_hash
    AND   qs.server_last_execution_time >= @windowStart
    ORDER BY qs.server_last_execution_time DESC
) AS qt
WHERE c.regression_factor >= 2
ORDER BY c.regression_factor DESC
OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY";

        cmd.Parameters.Add(new SqlParameter("@windowStart", context.TimeRangeStart.AddDays(-14)));

        var items = new List<object>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new
            {
                database = reader.IsDBNull(0) ? "" : reader.GetString(0),
                query_id = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1)),
                latest_plan_hash = reader.IsDBNull(2) ? "" : reader.GetString(2),
                latest_cpu_per_exec_us = reader.IsDBNull(3) ? 0.0 : Convert.ToDouble(reader.GetValue(3)),
                latest_duration_per_exec_us = reader.IsDBNull(4) ? 0.0 : Convert.ToDouble(reader.GetValue(4)),
                best_plan_hash = reader.IsDBNull(5) ? "" : reader.GetString(5),
                best_plan_id = reader.IsDBNull(6) ? 0L : Convert.ToInt64(reader.GetValue(6)),
                best_cpu_per_exec_us = reader.IsDBNull(7) ? 0.0 : Convert.ToDouble(reader.GetValue(7)),
                best_duration_per_exec_us = reader.IsDBNull(8) ? 0.0 : Convert.ToDouble(reader.GetValue(8)),
                regression_factor = reader.IsDBNull(9) ? 0.0 : Convert.ToDouble(reader.GetValue(9)),
                query_text = reader.IsDBNull(10) ? "" : reader.GetString(10)
            });
        }

        if (items.Count > 0)
            finding.DrillDown!["regressed_queries"] = items;
    }

    /// <summary>
    /// Reconstructs blocking chains (same logic as the collector) and surfaces the top 3
    /// by magnitude — apex, depth, victim count, and the level-by-level structure that
    /// the flat top_blocking_chains list cannot show.
    /// </summary>
    private async Task CollectReconstructedBlockingChains(AnalysisFinding finding, AnalysisContext context)
    {
        using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        // blocking_spid IS NOT NULL filters out rows whose source XML had an empty
        // <blocking-process><process/></blocking-process> (system task / torn-down session) —
        // those can't contribute to a reconstructed chain.
        cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP (5000)
    event_time,
    database_name,
    spid,
    last_transaction_started,
    blocking_spid,
    blocking_last_tran_started,
    wait_time_ms,
    lock_mode,
    blocking_status,
    blocked_sql_text,
    blocking_sql_text
FROM collect.blocking_BlockedProcessReport
WHERE collection_time >= @collectionWindow
AND   event_time >= @startTime
AND   event_time <= @endTime
AND   activity = 'blocked'
AND   blocking_spid IS NOT NULL
ORDER BY collection_time DESC";

        cmd.Parameters.Add(new SqlParameter("@collectionWindow", context.TimeRangeStart.AddHours(-1)));
        cmd.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
        cmd.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

        var rows = new List<BlockingPairRow>();
        using (var reader = await cmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add(new BlockingPairRow
                {
                    EventTime = reader.IsDBNull(0) ? default : reader.GetDateTime(0),
                    DatabaseName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    BlockedSpid = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2)),
                    BlockedTranStarted = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3),
                    BlockingSpid = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4)),
                    BlockingTranStarted = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5),
                    WaitTimeMs = reader.IsDBNull(6) ? 0L : Convert.ToInt64(reader.GetValue(6)),
                    LockMode = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    BlockingStatus = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                    BlockedSqlText = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                    BlockingSqlText = reader.IsDBNull(10) ? string.Empty : reader.GetString(10)
                });
            }
        }

        if (rows.Count == 0) return;

        var reconstruction = BlockingChainReconstructor.Reconstruct(
            rows, maxDepth: 50, maxPairs: 5000, stepBudget: 100_000);

        var items = new List<object>();
        foreach (var chain in reconstruction.Chains.Take(3))
        {
            items.Add(new
            {
                apex_spid = chain.ApexSpid,
                apex_sleeping = chain.ApexSleeping,
                depth = chain.Depth,
                // Distinct sessions blocked under this apex over the window — cumulative, not peak-concurrent.
                victim_count = chain.VictimCount,
                max_wait_ms = chain.MaxWaitMs,
                levels = chain.Levels.Select(l => new
                {
                    level = l.Level,
                    blocking_spid = l.BlockingSpid,
                    blocked_spid = l.BlockedSpid,
                    lock_mode = l.LockMode,
                    wait_time_ms = l.WaitTimeMs,
                    blocking_sql = l.BlockingSqlText,
                    blocked_sql = l.BlockedSqlText
                }).ToList()
            });
        }

        if (items.Count > 0)
            finding.DrillDown!["reconstructed_blocking_chains"] = items;
    }
}
