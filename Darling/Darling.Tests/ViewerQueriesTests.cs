/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Viewer;
using PerformanceMonitor.Ui;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the W1f-1 Queries-tab SQL against the Darling store contract (no live Postgres): the three grid
/// reads (Top Queries / Top Procedures / Query Store), their comparison reads, and their slicer bucket
/// reads. The pins guard the load-bearing clauses the string-only tests can catch — the ranking clause,
/// the LATERAL latest-text shape, the WAITFOR trim, the over-fetch + cap, the base-table names, the
/// CAST-back-to-bigint on summed aggregates, and the FULL OUTER JOIN / IS NOT DISTINCT FROM comparison
/// shape. Ordinal correctness + PG execution are covered by the gated live round-trips below.
/// </summary>
public sealed class ViewerQueriesSqlTests
{
    // ── Top Queries ──

    [Fact]
    public void TopQueriesSql_GroupsQueryStatsByDatabaseAndHash_OverTheWindow_BaseTable()
    {
        var sql = ViewerDataService.TopQueriesSql;
        Assert.Contains("FROM query_stats", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("v_query_stats", sql, StringComparison.Ordinal); /* viewer reads base tables */
        Assert.Contains("WHERE server_id = $1", sql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", sql, StringComparison.Ordinal);
        Assert.Contains("collection_time <= $3", sql, StringComparison.Ordinal); /* end bound for the slicer */
        Assert.Contains("GROUP BY database_name, query_hash", sql, StringComparison.Ordinal);
        Assert.Contains("HAVING SUM(delta_execution_count) > 0 OR SUM(delta_elapsed_time) > 0", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TopQueriesSql_RanksByTotalElapsedDelta_OverFetchesFive_CapsAtTop()
    {
        var sql = ViewerDataService.TopQueriesSql;
        Assert.Contains("ORDER BY SUM(delta_elapsed_time) DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $4 + 5", sql, StringComparison.Ordinal); /* over-fetch so the WAITFOR trim can't shrink below top */
        Assert.Contains("ORDER BY r.total_elapsed_us DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $4", sql, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'WAITFOR%'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TopQueriesSql_FetchesTheLatestNonNullQueryText_ViaLateralJoin_WithPlanPresenceFlag()
    {
        var sql = ViewerDataService.TopQueriesSql;
        Assert.Contains("LEFT JOIN LATERAL", sql, StringComparison.Ordinal);
        Assert.Contains("query_text IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY collection_time DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 1", sql, StringComparison.Ordinal);
        /* The LATERAL still fetches only query_text (never the multi-KB plan). The plan is surfaced by a
           cheap group-level presence flag — bool_or(query_plan_xml IS NOT NULL) — that gates the grid's
           Query Plan column; the plan XML itself is read on demand (GetQueryStatsPlanXmlAsync). */
        Assert.Contains("bool_or(query_plan_xml IS NOT NULL) AS has_query_plan", sql, StringComparison.Ordinal);
        Assert.Contains("r.has_query_plan", sql, StringComparison.Ordinal); /* the flag rides from the ranked CTE, not the LATERAL */
    }

    [Fact]
    public void TopQueriesSql_CastsEverySummedAggregateBackToBigint()
    {
        /* Postgres SUM(bigint) returns numeric; without the CAST the typed GetInt64 readers throw. */
        var sql = ViewerDataService.TopQueriesSql;
        Assert.Contains("CAST(SUM(delta_execution_count) AS bigint)", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(SUM(delta_worker_time) AS bigint)", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(SUM(delta_elapsed_time) AS bigint)", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(SUM(delta_logical_reads) AS bigint)", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(SUM(delta_rows) AS bigint)", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(SUM(delta_logical_writes) AS bigint)", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(SUM(delta_physical_reads) AS bigint)", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(SUM(delta_spills) AS bigint)", sql, StringComparison.Ordinal);
        /* Peak CPU/sec keeps Lite's per-sample expression (double precision). */
        Assert.Contains("NULLIF(sample_interval_seconds, 0)", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TopQueriesSql_DropsLitesStalenessFilterAndUtcOffset()
    {
        /* The viewer has no per-server UTC offset; the delta HAVING already excludes never-ran plans. */
        var sql = ViewerDataService.TopQueriesSql;
        Assert.DoesNotContain("INTERVAL", sql, StringComparison.Ordinal);
        /* #1319: $5 is now the global database filter (database_name = ANY($5)), NOT Lite's utc-offset
           staleness param — the viewer still drops Lite's INTERVAL staleness filter. */
        Assert.Contains("database_name = ANY($5)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("$6", sql, StringComparison.Ordinal);
    }

    // ── Top Procedures ──

    [Fact]
    public void TopProceduresSql_GroupsProcedureStatsByObject_RanksByTotalElapsed_BaseTable()
    {
        var sql = ViewerDataService.TopProceduresSql;
        Assert.Contains("FROM procedure_stats", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("v_procedure_stats", sql, StringComparison.Ordinal); /* no such view exists in Darling */
        Assert.Contains("WHERE server_id = $1", sql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", sql, StringComparison.Ordinal);
        Assert.Contains("collection_time <= $3", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY database_name, schema_name, object_name, object_type", sql, StringComparison.Ordinal);
        Assert.Contains("HAVING SUM(delta_execution_count) > 0 OR SUM(delta_elapsed_time) > 0", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY SUM(delta_elapsed_time) DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $4", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(SUM(delta_execution_count) AS bigint)", sql, StringComparison.Ordinal);
        /* avg_spills is the one derived double in the proc read. */
        Assert.Contains("CAST(SUM(delta_spills) AS double precision) / NULLIF(SUM(delta_execution_count), 0)", sql, StringComparison.Ordinal);
        /* Item 7: whether the collector stored a plan for the object — gates the grid's Download button on the
           same query_plan_xml IS NOT NULL filter GetProcedureStatsPlanXmlAsync fetches on. */
        Assert.Contains("bool_or(query_plan_xml IS NOT NULL) AS has_query_plan", sql, StringComparison.Ordinal);
    }

    // ── Query Store ──

    [Fact]
    public void QueryStoreTopSql_GroupsByQueryAndPlan_RanksByTotalDuration_BaseTable()
    {
        var sql = ViewerDataService.QueryStoreTopSql;
        Assert.Contains("FROM query_store_stats", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY database_name, query_id, plan_id, query_hash", sql, StringComparison.Ordinal);
        /* Rank by total duration = executions * avg duration, over-fetch 5, cap at top (Lite's shape). */
        Assert.Contains("ORDER BY SUM(execution_count) * AVG(CAST(avg_duration_us AS double precision)) DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $4 + 5", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY r.total_executions * r.avg_duration_ms DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $4", sql, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'WAITFOR%'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryStoreTopSql_ConvertsMetrics_ForcedPlanBoolean_MemoryPagesToMb_NoPlanText()
    {
        var sql = ViewerDataService.QueryStoreTopSql;
        Assert.Contains("CAST(SUM(execution_count) AS bigint)", sql, StringComparison.Ordinal);
        Assert.Contains("AVG(CAST(avg_duration_us AS double precision)) / 1000.0", sql, StringComparison.Ordinal);
        /* Postgres has no MAX(boolean); the forced-plan flag folds through bool_or. */
        Assert.Contains("bool_or(is_forced_plan)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("MAX(CASE WHEN is_forced_plan", sql, StringComparison.Ordinal);
        Assert.Contains("AVG(CAST(avg_query_max_used_memory AS double precision)) * 8.0 / 1024.0", sql, StringComparison.Ordinal);
        /* View-Plan deferred: the collected query_plan_text column is not read this wave. */
        Assert.DoesNotContain("query_plan_text", sql, StringComparison.Ordinal);
    }

    // ── Comparisons ──

    [Theory]
    [InlineData(nameof(ViewerDataService.QueryStatsComparisonSql), "query_stats", "GROUP BY th.database_name, th.query_hash")]
    [InlineData(nameof(ViewerDataService.QueryStoreComparisonSql), "query_store_stats", "GROUP BY th.database_name, th.query_hash")]
    [InlineData(nameof(ViewerDataService.ProcedureStatsComparisonSql), "procedure_stats", "GROUP BY tp.database_name, tp.schema_name, tp.object_name")]
    public void ComparisonSql_UnionsTop100_FullOuterJoins_NullSafe(string sqlName, string table, string finalGroupBy)
    {
        var sql = SqlByName(sqlName);
        Assert.Contains($"FROM {table}", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY SUM(", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 100", sql, StringComparison.Ordinal);
        Assert.Contains("UNION ALL", sql, StringComparison.Ordinal);
        Assert.Contains("FULL OUTER JOIN baseline_period b", sql, StringComparison.Ordinal);
        /* The CTE INNER JOINs keep Lite's null-safe IS NOT DISTINCT FROM (legal in a PG inner join)... */
        Assert.Contains("IS NOT DISTINCT FROM", sql, StringComparison.Ordinal);
        /* ...but the FULL JOIN must be COALESCE-equality — PG can't FULL-JOIN on IS NOT DISTINCT FROM. */
        Assert.Contains("COALESCE(c.database_name, '') = COALESCE(b.database_name, '')", sql, StringComparison.Ordinal);
        Assert.Contains(finalGroupBy, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryStoreComparisonSql_UsesExecutionCountWeightedAverages()
    {
        /* Query Store rows are per-interval averages; the comparison weights each by execution_count. */
        var sql = ViewerDataService.QueryStoreComparisonSql;
        Assert.Contains("SUM(qs.execution_count * qs.avg_duration_us::double precision) / NULLIF(SUM(qs.execution_count), 0)", sql, StringComparison.Ordinal);
    }

    // ── Slicers ──

    [Theory]
    [InlineData(nameof(ViewerDataService.QueryStatsSlicerSql), "query_stats", "COUNT(DISTINCT query_hash)")]
    [InlineData(nameof(ViewerDataService.ProcStatsSlicerSql), "procedure_stats", "COUNT(DISTINCT object_name)")]
    [InlineData(nameof(ViewerDataService.QueryStoreSlicerSql), "query_store_stats", "COUNT(DISTINCT query_id)")]
    public void SlicerSql_BucketsByHour_SevenColumnShape(string sqlName, string table, string distinctCount)
    {
        var sql = SqlByName(sqlName);
        Assert.Contains("date_trunc('hour', collection_time)", sql, StringComparison.Ordinal);
        Assert.Contains($"FROM {table}", sql, StringComparison.Ordinal);
        Assert.Contains(distinctCount, sql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", sql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", sql, StringComparison.Ordinal);
        Assert.Contains("collection_time <= $3", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY bucket", sql, StringComparison.Ordinal);
    }

    // ── PG dialect (all Queries reads) ──

    [Theory]
    [InlineData(nameof(ViewerDataService.TopQueriesSql))]
    [InlineData(nameof(ViewerDataService.TopProceduresSql))]
    [InlineData(nameof(ViewerDataService.QueryStoreTopSql))]
    [InlineData(nameof(ViewerDataService.QueryStatsComparisonSql))]
    [InlineData(nameof(ViewerDataService.ProcedureStatsComparisonSql))]
    [InlineData(nameof(ViewerDataService.QueryStoreComparisonSql))]
    [InlineData(nameof(ViewerDataService.QueryStatsSlicerSql))]
    [InlineData(nameof(ViewerDataService.ProcStatsSlicerSql))]
    [InlineData(nameof(ViewerDataService.QueryStoreSlicerSql))]
    public void QueriesReads_ArePostgresDialect_PositionalParams_NoTsqlIsms(string sqlName)
    {
        var sql = SqlByName(sqlName);
        Assert.DoesNotContain("now(", sql.ToLowerInvariant());
        Assert.DoesNotContain("getdate", sql.ToLowerInvariant());
        Assert.DoesNotContain("N'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
        Assert.Contains("$1", sql, StringComparison.Ordinal);
    }

    private static string SqlByName(string name) => name switch
    {
        nameof(ViewerDataService.TopQueriesSql) => ViewerDataService.TopQueriesSql,
        nameof(ViewerDataService.TopProceduresSql) => ViewerDataService.TopProceduresSql,
        nameof(ViewerDataService.QueryStoreTopSql) => ViewerDataService.QueryStoreTopSql,
        nameof(ViewerDataService.QueryStatsComparisonSql) => ViewerDataService.QueryStatsComparisonSql,
        nameof(ViewerDataService.ProcedureStatsComparisonSql) => ViewerDataService.ProcedureStatsComparisonSql,
        nameof(ViewerDataService.QueryStoreComparisonSql) => ViewerDataService.QueryStoreComparisonSql,
        nameof(ViewerDataService.QueryStatsSlicerSql) => ViewerDataService.QueryStatsSlicerSql,
        nameof(ViewerDataService.ProcStatsSlicerSql) => ViewerDataService.ProcStatsSlicerSql,
        _ => ViewerDataService.QueryStoreSlicerSql,
    };
}

/// <summary>The Queries row models' pure display helpers: raw server-clock formatting, FullName, totals.</summary>
public sealed class ViewerQueriesDisplayTests
{
    [Fact]
    public void FormatServerClock_ShowsRawServerWallClock_EmptyForNull()
    {
        /* last_execution_time / creation_time are the SQL server's local wall clock — shown raw, NOT run
           through the naive-UTC-to-local conversion the collection_time columns get. */
        var t = new DateTime(2026, 7, 1, 13, 45, 7);
        Assert.Equal("2026-07-01 13:45:07", ViewerDataService.FormatServerClock(t));
        Assert.Equal("", ViewerDataService.FormatServerClock(null));
    }

    [Fact]
    public void QueryStatsRow_ConvertsMicrosecondsToMs_AndAveragesByExecutions()
    {
        var row = new ViewerQueryStatsRow { TotalExecutions = 4, TotalCpuUs = 8000, TotalElapsedUs = 20000, TotalLogicalReads = 400 };
        Assert.Equal(8.0, row.TotalCpuMs);
        Assert.Equal(20.0, row.TotalElapsedMs);
        Assert.Equal(2.0, row.AvgCpuMs);
        Assert.Equal(5.0, row.AvgElapsedMs);
        Assert.Equal(100.0, row.AvgReads);
    }

    [Fact]
    public void ProcedureStatsRow_FullName_ComposesSchemaAndObject()
    {
        Assert.Equal("dbo.usp_Get", new ViewerProcedureStatsRow { SchemaName = "dbo", ObjectName = "usp_Get" }.FullName);
        Assert.Equal("usp_Get", new ViewerProcedureStatsRow { SchemaName = "", ObjectName = "usp_Get" }.FullName);
    }

    [Fact]
    public void QueryStoreRow_TotalsAreExecutionsTimesAverage()
    {
        var row = new ViewerQueryStoreRow { TotalExecutions = 10, AvgDurationMs = 3.5, AvgCpuTimeMs = 1.25 };
        Assert.Equal(35.0, row.TotalDurationMs);
        Assert.Equal(12.5, row.TotalCpuMs);
    }

    [Fact]
    public void ComparisonItems_AreTheSharedUiTypes()
    {
        /* The three comparison reads return the shared .Ui derivatives the collapsed grids bind. */
        Assert.IsAssignableFrom<ComparisonItemBase>(new QueryStatsComparisonItem());
        Assert.IsAssignableFrom<ComparisonItemBase>(new ProcedureStatsComparisonItem());
        Assert.Equal("NEW", new QueryStatsComparisonItem { ExecutionCount = 5, BaselineExecutionCount = 0 }.StatusBadge);
        Assert.Equal("GONE", new ProcedureStatsComparisonItem { ExecutionCount = 0, BaselineExecutionCount = 5 }.StatusBadge);
    }
}

/// <summary>
/// Gated (DARLING_TEST_PG) live round-trips for the three Queries reads + a comparison + a slicer. Each
/// plants query/procedure/query-store rows for a negative sentinel server across two collections, then
/// asserts the read executes on real Postgres and returns the expected grouping / ordering / unit
/// conversion / WAITFOR trim end-to-end (the string pins can't catch a dialect error or an ordinal
/// slip). Shares the serialized "live-postgres" collection and cleans up in finally.
/// </summary>
[Collection("live-postgres")]
public sealed class ViewerQueriesLivePostgresTests
{
    private const int QueryStatsServerId = -970801;
    private const int ProcedureStatsServerId = -970802;
    private const int QueryStoreServerId = -970803;
    private const int ComparisonServerId = -970804;
    private const int SlicerServerId = -970805;
    private const int ProcedureStatsPlanServerId = -970806;

    private static string? ConnectionString => Environment.GetEnvironmentVariable("DARLING_TEST_PG");

    [Fact]
    public async Task TopQueries_GroupsSums_ExcludesWaitfor_OrdersByTotalDuration_AgainstDevPostgres()
    {
        var cs = ConnectionString;
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "Set DARLING_TEST_PG to a Postgres connection string to run the live Top Queries test.");

        using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteRowsAsync(connection, "query_stats", QueryStatsServerId);

        await using var viewer = new ViewerDataService(cs!);
        var end = TruncateToSeconds(DateTime.UtcNow);
        var start = end.AddHours(-24);
        var t1 = start.AddHours(1);
        var t2 = start.AddHours(2);

        try
        {
            /* "slow" query: two collections summed. total_elapsed = 300_000 us across the two rows. */
            await InsertQueryStatsAsync(connection, QueryStatsServerId, t1, "StackOverflow", "0xSLOW",
                deltaExec: 2, deltaWorker: 40_000, deltaElapsed: 120_000, deltaReads: 200, queryText: "SELECT slow");
            await InsertQueryStatsAsync(connection, QueryStatsServerId, t2, "StackOverflow", "0xSLOW",
                deltaExec: 3, deltaWorker: 60_000, deltaElapsed: 180_000, deltaReads: 300, queryText: "SELECT slow");
            /* "fast" query: smaller total elapsed → ranks second. */
            await InsertQueryStatsAsync(connection, QueryStatsServerId, t1, "StackOverflow", "0xFAST",
                deltaExec: 5, deltaWorker: 5_000, deltaElapsed: 10_000, deltaReads: 50, queryText: "SELECT fast");
            /* WAITFOR shell: must be trimmed. */
            await InsertQueryStatsAsync(connection, QueryStatsServerId, t1, "StackOverflow", "0xWAIT",
                deltaExec: 1, deltaWorker: 1, deltaElapsed: 999_999, deltaReads: 1, queryText: "WAITFOR DELAY '00:00:05'");

            var rows = await viewer.GetTopQueriesByCpuAsync(QueryStatsServerId, start, end);

            Assert.Equal(2, rows.Count); /* WAITFOR excluded despite its huge elapsed */
            Assert.DoesNotContain(rows, r => r.QueryHash == "0xWAIT");

            /* Ordered by total elapsed descending: slow first. */
            Assert.Equal("0xSLOW", rows[0].QueryHash);
            Assert.Equal(5, rows[0].TotalExecutions);           /* 2 + 3 summed across collections */
            Assert.Equal(300.0, rows[0].TotalElapsedMs);        /* (120000 + 180000) us -> ms */
            Assert.Equal(100.0, rows[0].TotalCpuMs);            /* (40000 + 60000) us -> ms */
            Assert.Equal("SELECT slow", rows[0].QueryText);
            Assert.Equal("0xFAST", rows[1].QueryHash);
        }
        finally
        {
            await DeleteRowsAsync(connection, "query_stats", QueryStatsServerId);
        }
    }

    [Fact]
    public async Task TopProcedures_GroupsByObject_ComposesFullName_OrdersByTotalDuration_AgainstDevPostgres()
    {
        var cs = ConnectionString;
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "Set DARLING_TEST_PG to a Postgres connection string to run the live Top Procedures test.");

        using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteRowsAsync(connection, "procedure_stats", ProcedureStatsServerId);

        await using var viewer = new ViewerDataService(cs!);
        var end = TruncateToSeconds(DateTime.UtcNow);
        var start = end.AddHours(-24);

        try
        {
            await InsertProcedureStatsAsync(connection, ProcedureStatsServerId, start.AddHours(1), "StackOverflow", "dbo", "usp_Slow", "PROC",
                deltaExec: 4, deltaWorker: 40_000, deltaElapsed: 200_000, deltaReads: 400);
            await InsertProcedureStatsAsync(connection, ProcedureStatsServerId, start.AddHours(1), "StackOverflow", "dbo", "usp_Fast", "PROC",
                deltaExec: 10, deltaWorker: 5_000, deltaElapsed: 20_000, deltaReads: 100);

            var rows = await viewer.GetTopProceduresByCpuAsync(ProcedureStatsServerId, start, end);

            Assert.Equal(2, rows.Count);
            Assert.Equal("dbo.usp_Slow", rows[0].FullName);
            Assert.Equal(4, rows[0].TotalExecutions);
            Assert.Equal(200.0, rows[0].TotalElapsedMs);
            Assert.Equal(50.0, rows[0].AvgElapsedMs);           /* 200000 us / 4 execs -> ms */
            Assert.Equal("dbo.usp_Fast", rows[1].FullName);
        }
        finally
        {
            await DeleteRowsAsync(connection, "procedure_stats", ProcedureStatsServerId);
        }
    }

    [Fact]
    public async Task ProcedureStatsPlan_FetchesNewestStoredXml_KeyedByObject_NullWhenUncaptured_AgainstDevPostgres()
    {
        var cs = ConnectionString;
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "Set DARLING_TEST_PG to a Postgres connection string to run the live procedure-plan test.");

        using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteRowsAsync(connection, "procedure_stats", ProcedureStatsPlanServerId);

        await using var viewer = new ViewerDataService(cs!);
        var t = TruncateToSeconds(DateTime.UtcNow).AddHours(-2);

        try
        {
            /* Same (db, schema, object) captured twice — the newer plan wins (ORDER BY collection_time DESC). */
            await InsertProcedureStatsAsync(connection, ProcedureStatsPlanServerId, t, "StackOverflow", "dbo", "usp_WithPlan", "PROC",
                deltaExec: 1, deltaWorker: 10_000, deltaElapsed: 20_000, deltaReads: 100, planXml: "<ShowPlanXML>older</ShowPlanXML>");
            await InsertProcedureStatsAsync(connection, ProcedureStatsPlanServerId, t.AddMinutes(30), "StackOverflow", "dbo", "usp_WithPlan", "PROC",
                deltaExec: 1, deltaWorker: 10_000, deltaElapsed: 20_000, deltaReads: 100, planXml: "<ShowPlanXML>newer</ShowPlanXML>");
            /* A procedure with activity but no captured plan (query_plan_xml NULL). */
            await InsertProcedureStatsAsync(connection, ProcedureStatsPlanServerId, t, "StackOverflow", "dbo", "usp_NoPlan", "PROC",
                deltaExec: 1, deltaWorker: 10_000, deltaElapsed: 20_000, deltaReads: 100);

            Assert.Equal("<ShowPlanXML>newer</ShowPlanXML>",
                await viewer.GetProcedureStatsPlanXmlAsync(ProcedureStatsPlanServerId, "StackOverflow", "dbo", "usp_WithPlan"));
            /* No plan captured, and an absent object, both fetch null. */
            Assert.Null(await viewer.GetProcedureStatsPlanXmlAsync(ProcedureStatsPlanServerId, "StackOverflow", "dbo", "usp_NoPlan"));
            Assert.Null(await viewer.GetProcedureStatsPlanXmlAsync(ProcedureStatsPlanServerId, "StackOverflow", "dbo", "usp_Absent"));
        }
        finally
        {
            await DeleteRowsAsync(connection, "procedure_stats", ProcedureStatsPlanServerId);
        }
    }

    [Fact]
    public async Task QueryStore_AveragesIntervals_ForcedPlanBoolean_MemoryToMb_AgainstDevPostgres()
    {
        var cs = ConnectionString;
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "Set DARLING_TEST_PG to a Postgres connection string to run the live Query Store test.");

        using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteRowsAsync(connection, "query_store_stats", QueryStoreServerId);

        await using var viewer = new ViewerDataService(cs!);
        var end = TruncateToSeconds(DateTime.UtcNow);
        var start = end.AddHours(-24);

        try
        {
            /* One query, one plan, two intervals averaged: avg_duration_us 2000 & 4000 -> AVG 3000us = 3ms. */
            await InsertQueryStoreAsync(connection, QueryStoreServerId, start.AddHours(1), "StackOverflow", queryId: 42, planId: 7,
                execCount: 3, avgDurationUs: 2000, avgCpuUs: 1000, forced: true, maxMemPages: 1024, queryText: "SELECT qs");
            await InsertQueryStoreAsync(connection, QueryStoreServerId, start.AddHours(2), "StackOverflow", queryId: 42, planId: 7,
                execCount: 3, avgDurationUs: 4000, avgCpuUs: 2000, forced: true, maxMemPages: 1024, queryText: "SELECT qs");

            var rows = await viewer.GetQueryStoreTopQueriesAsync(QueryStoreServerId, start, end);

            var r = Assert.Single(rows);
            Assert.Equal(42, r.QueryId);
            Assert.Equal(7, r.PlanId);
            Assert.Equal(6, r.TotalExecutions);            /* 3 + 3 summed */
            Assert.Equal(3.0, r.AvgDurationMs, 3);         /* AVG(2000, 4000) us -> ms */
            Assert.True(r.IsForcedPlan);
            Assert.Equal(8.0, r.AvgMemoryMb, 3);           /* 1024 pages * 8 KB / 1024 -> 8 MB */
            Assert.Equal("SELECT qs", r.QueryText);
        }
        finally
        {
            await DeleteRowsAsync(connection, "query_store_stats", QueryStoreServerId);
        }
    }

    [Fact]
    public async Task QueryStatsComparison_FlagsNewAndGone_AcrossWindows_AgainstDevPostgres()
    {
        var cs = ConnectionString;
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "Set DARLING_TEST_PG to a Postgres connection string to run the live comparison test.");

        using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteRowsAsync(connection, "query_stats", ComparisonServerId);

        await using var viewer = new ViewerDataService(cs!);
        var currentEnd = TruncateToSeconds(DateTime.UtcNow);
        var currentStart = currentEnd.AddHours(-1);
        var baselineEnd = currentStart;
        var baselineStart = baselineEnd.AddHours(-1);

        try
        {
            /* Present in both -> normal; present only current -> NEW; only baseline -> GONE. */
            await InsertQueryStatsAsync(connection, ComparisonServerId, currentStart.AddMinutes(10), "DB", "0xBOTH",
                deltaExec: 5, deltaWorker: 5000, deltaElapsed: 10000, deltaReads: 50, queryText: "both");
            await InsertQueryStatsAsync(connection, ComparisonServerId, baselineStart.AddMinutes(10), "DB", "0xBOTH",
                deltaExec: 5, deltaWorker: 5000, deltaElapsed: 8000, deltaReads: 50, queryText: "both");
            await InsertQueryStatsAsync(connection, ComparisonServerId, currentStart.AddMinutes(10), "DB", "0xNEW",
                deltaExec: 3, deltaWorker: 3000, deltaElapsed: 6000, deltaReads: 30, queryText: "new");
            await InsertQueryStatsAsync(connection, ComparisonServerId, baselineStart.AddMinutes(10), "DB", "0xGONE",
                deltaExec: 7, deltaWorker: 7000, deltaElapsed: 14000, deltaReads: 70, queryText: "gone");

            var items = await viewer.GetQueryStatsComparisonAsync(ComparisonServerId, currentStart, currentEnd, baselineStart, baselineEnd);

            Assert.Equal("NEW", items.Single(i => i.QueryHash == "0xNEW").StatusBadge);
            Assert.Equal("GONE", items.Single(i => i.QueryHash == "0xGONE").StatusBadge);
            Assert.Equal("", items.Single(i => i.QueryHash == "0xBOTH").StatusBadge);
        }
        finally
        {
            await DeleteRowsAsync(connection, "query_stats", ComparisonServerId);
        }
    }

    [Fact]
    public async Task QueryStatsSlicer_BucketsByHour_AgainstDevPostgres()
    {
        var cs = ConnectionString;
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "Set DARLING_TEST_PG to a Postgres connection string to run the live slicer test.");

        using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteRowsAsync(connection, "query_stats", SlicerServerId);

        await using var viewer = new ViewerDataService(cs!);
        var end = TruncateToSeconds(DateTime.UtcNow);
        var start = end.AddHours(-24);
        var hour1 = TruncateToHour(start.AddHours(3));
        var hour2 = TruncateToHour(start.AddHours(5));

        try
        {
            await InsertQueryStatsAsync(connection, SlicerServerId, hour1.AddMinutes(5), "DB", "0xA",
                deltaExec: 1, deltaWorker: 60_000, deltaElapsed: 120_000, deltaReads: 10, queryText: "a");
            await InsertQueryStatsAsync(connection, SlicerServerId, hour1.AddMinutes(35), "DB", "0xB",
                deltaExec: 1, deltaWorker: 60_000, deltaElapsed: 120_000, deltaReads: 10, queryText: "b");
            await InsertQueryStatsAsync(connection, SlicerServerId, hour2.AddMinutes(5), "DB", "0xA",
                deltaExec: 1, deltaWorker: 30_000, deltaElapsed: 60_000, deltaReads: 10, queryText: "a");

            var buckets = await viewer.GetQueryStatsSlicerDataAsync(SlicerServerId, start, end);

            Assert.Equal(2, buckets.Count); /* two distinct hours */
            var first = buckets[0];
            Assert.Equal(hour1, first.BucketTime);
            Assert.Equal(2, first.SessionCount);        /* two distinct query hashes in hour1 */
            Assert.Equal(120.0, first.TotalCpu, 3);     /* (60000 + 60000) us / 1000 -> ms */
        }
        finally
        {
            await DeleteRowsAsync(connection, "query_stats", SlicerServerId);
        }
    }

    // ── Insert helpers (only the columns each read touches; the rest default to NULL) ──

    private static async Task InsertQueryStatsAsync(
        NpgsqlConnection connection, int serverId, DateTime collectionTimeUtc, string databaseName, string queryHash,
        long deltaExec, long deltaWorker, long deltaElapsed, long deltaReads, string queryText)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO query_stats
    (collection_id, collection_time, server_id, server_name,
     database_name, query_hash, query_plan_hash, sql_handle, plan_handle,
     last_execution_time, creation_time, sample_interval_seconds,
     delta_execution_count, delta_worker_time, delta_elapsed_time, delta_logical_reads,
     delta_rows, delta_logical_writes, delta_physical_reads, delta_spills,
     total_clr_time, plan_generation_num, query_text)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21, $22, $23)", connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue("viewer-queries-e2e");
        command.Parameters.AddWithValue(databaseName);
        command.Parameters.AddWithValue(queryHash);
        command.Parameters.AddWithValue("0xPLANHASH");
        command.Parameters.AddWithValue("0xSQLHANDLE");
        command.Parameters.AddWithValue("0xPLANHANDLE");
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc.AddHours(-1), DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(60);
        command.Parameters.AddWithValue(deltaExec);
        command.Parameters.AddWithValue(deltaWorker);
        command.Parameters.AddWithValue(deltaElapsed);
        command.Parameters.AddWithValue(deltaReads);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(queryText);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertProcedureStatsAsync(
        NpgsqlConnection connection, int serverId, DateTime collectionTimeUtc,
        string databaseName, string schemaName, string objectName, string objectType,
        long deltaExec, long deltaWorker, long deltaElapsed, long deltaReads, string? planXml = null)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO procedure_stats
    (collection_id, collection_time, server_id, server_name,
     database_name, schema_name, object_name, object_type,
     cached_time, last_execution_time, sql_handle, plan_handle,
     delta_execution_count, delta_worker_time, delta_elapsed_time, delta_logical_reads,
     delta_logical_writes, delta_physical_reads, delta_spills, query_plan_xml)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20)", connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue("viewer-procs-e2e");
        command.Parameters.AddWithValue(databaseName);
        command.Parameters.AddWithValue(schemaName);
        command.Parameters.AddWithValue(objectName);
        command.Parameters.AddWithValue(objectType);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc.AddHours(-1), DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue("0xSQLHANDLE");
        command.Parameters.AddWithValue("0xPLANHANDLE");
        command.Parameters.AddWithValue(deltaExec);
        command.Parameters.AddWithValue(deltaWorker);
        command.Parameters.AddWithValue(deltaElapsed);
        command.Parameters.AddWithValue(deltaReads);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue((object?)planXml ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertQueryStoreAsync(
        NpgsqlConnection connection, int serverId, DateTime collectionTimeUtc, string databaseName,
        long queryId, long planId, long execCount, long avgDurationUs, long avgCpuUs, bool forced, long maxMemPages, string queryText)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO query_store_stats
    (collection_id, collection_time, server_id, server_name,
     database_name, query_id, plan_id, query_hash, query_plan_hash, query_text, module_name,
     execution_type_desc, first_execution_time, last_execution_time,
     execution_count, avg_duration_us, avg_cpu_time_us, avg_logical_io_reads, avg_logical_io_writes,
     avg_physical_io_reads, avg_rowcount, avg_clr_time_us, avg_log_bytes_used, avg_tempdb_space_used,
     avg_num_physical_io_reads, avg_query_max_used_memory, min_dop, max_dop,
     plan_type, plan_forcing_type, is_forced_plan, force_failure_count, compatibility_level)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20,
        $21, $22, $23, $24, $25, $26, $27, $28, $29, $30, $31, $32, $33)", connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue("viewer-qs-e2e");
        command.Parameters.AddWithValue(databaseName);
        command.Parameters.AddWithValue(queryId);
        command.Parameters.AddWithValue(planId);
        command.Parameters.AddWithValue("0xQSHASH");
        command.Parameters.AddWithValue("0xQSPLANHASH");
        command.Parameters.AddWithValue(queryText);
        command.Parameters.AddWithValue("dbo.usp_Qs");
        command.Parameters.AddWithValue("Regular");
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc.AddHours(-1), DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(execCount);
        command.Parameters.AddWithValue(avgDurationUs);
        command.Parameters.AddWithValue(avgCpuUs);
        command.Parameters.AddWithValue(100L);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(10L);
        command.Parameters.AddWithValue(50L);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(5L);
        command.Parameters.AddWithValue(maxMemPages);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue("Compiled");
        command.Parameters.AddWithValue("None");
        command.Parameters.AddWithValue(forced);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(160);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        DateTime.SpecifyKind(new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond)), DateTimeKind.Unspecified);

    private static DateTime TruncateToHour(DateTime value) =>
        DateTime.SpecifyKind(new DateTime(value.Year, value.Month, value.Day, value.Hour, 0, 0), DateTimeKind.Unspecified);

    private static async Task DeleteRowsAsync(NpgsqlConnection connection, string table, int serverId)
    {
        using var cleanup = new NpgsqlCommand($"DELETE FROM {table} WHERE server_id = {serverId};", connection);
        await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
