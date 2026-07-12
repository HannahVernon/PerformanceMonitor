/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the unified "Expensive Queries" cross-source UNION SQL against the Darling store contract (no live
/// Postgres): the three base tables it UNIONs, the per-source source-label constants, the both-sides window
/// bound on every branch, the average-worker ranking + TOP-per-source + final cap, the CAST-back-to-bigint
/// on summed deltas, and the store-driven deviations from the Dashboard's <c>report.expensive_queries_today</c>
/// (query_stats has no object columns → 'Adhoc'; procedure grant is NULL; Query Store grant is 8-KB-pages→MB
/// and its plan column is query_plan_text). Ordinal correctness + PG execution are covered by the gated live
/// round-trip below.
/// </summary>
public sealed class ViewerUnifiedExpensiveQueriesSqlTests
{
    [Fact]
    public void UnifiedSql_UnionsAllThreeBaseTables_NotTheViews()
    {
        var sql = ViewerDataService.UnifiedExpensiveQueriesSql;
        Assert.Contains("FROM query_stats", sql, StringComparison.Ordinal);
        Assert.Contains("FROM procedure_stats", sql, StringComparison.Ordinal);
        Assert.Contains("FROM query_store_stats", sql, StringComparison.Ordinal);
        /* viewer reads base tables, never the v_* passthrough views */
        Assert.DoesNotContain("v_query_stats", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("v_procedure_stats", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("v_query_store_stats", sql, StringComparison.Ordinal);
        /* three source subqueries → exactly two UNION ALLs */
        Assert.Equal(2, CountOccurrences(sql, "UNION ALL"));
    }

    [Fact]
    public void UnifiedSql_EmitsTheSourceLabelConstants_PerBranch()
    {
        var sql = ViewerDataService.UnifiedExpensiveQueriesSql;
        Assert.Contains($"'{ViewerDataService.ExpensiveSourceQueryStats}' AS source", sql, StringComparison.Ordinal);
        Assert.Contains($"'{ViewerDataService.ExpensiveSourceQueryStore}' AS source", sql, StringComparison.Ordinal);
        /* the procedure branch maps object_type → the three procedure source labels via a CASE */
        Assert.Contains($"THEN '{ViewerDataService.ExpensiveSourceStoredProcedure}'", sql, StringComparison.Ordinal);
        Assert.Contains($"THEN '{ViewerDataService.ExpensiveSourceTrigger}'", sql, StringComparison.Ordinal);
        Assert.Contains($"THEN '{ViewerDataService.ExpensiveSourceFunction}'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedSql_IsBothSidesWindowBound_OnEveryBranch()
    {
        var sql = ViewerDataService.UnifiedExpensiveQueriesSql;
        /* Each of the three source subqueries binds the window on collection_time on BOTH sides. */
        Assert.Equal(3, CountOccurrences(sql, "collection_time >= $2"));
        Assert.Equal(3, CountOccurrences(sql, "collection_time <= $3"));
        /* server_id is re-scoped 6× — the 3 ranked subqueries PLUS the 3 LATERAL latest-text/plan joins. */
        Assert.Equal(6, CountOccurrences(sql, "WHERE server_id = $1"));
    }

    [Fact]
    public void UnifiedSql_RanksEachSourceByAverageWorkerTime_TopPerSource_ThenFinalRankAndCap()
    {
        var sql = ViewerDataService.UnifiedExpensiveQueriesSql;
        /* query_stats + procedure_stats rank by avg worker = SUM(delta_worker_time)/SUM(delta_execution_count). */
        Assert.Contains("ORDER BY SUM(delta_worker_time)::double precision / NULLIF(SUM(delta_execution_count), 0) DESC", sql, StringComparison.Ordinal);
        /* query_store ranks by avg worker = SUM(avg_cpu_time_us * execution_count)/SUM(execution_count). */
        Assert.Contains("ORDER BY SUM(avg_cpu_time_us * execution_count)::double precision / NULLIF(SUM(execution_count), 0) DESC", sql, StringComparison.Ordinal);
        /* TOP-per-source ($4) three times, one per branch. */
        Assert.Equal(3, CountOccurrences(sql, "LIMIT $4"));
        /* the merged list is re-ranked by avg worker (ms) and capped ($5). */
        Assert.Contains("ORDER BY avg_worker_ms DESC NULLS LAST", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $5", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedSql_CastsSummedDeltasBackToBigint_AndConvertsUnitsInTheOuterProjection()
    {
        var sql = ViewerDataService.UnifiedExpensiveQueriesSql;
        /* Postgres SUM(bigint) is numeric; the typed readers need the CAST back to bigint (like TopQueriesSql). */
        Assert.Contains("CAST(SUM(delta_execution_count) AS bigint)", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(SUM(delta_worker_time) AS bigint)", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(SUM(avg_cpu_time_us * execution_count) AS bigint)", sql, StringComparison.Ordinal);
        /* worker/elapsed µs → sec + per-exec ms in the outer projection (the Dashboard view's units). */
        Assert.Contains("total_worker_us / 1000000.0 AS total_worker_sec", sql, StringComparison.Ordinal);
        Assert.Contains("total_worker_us / 1000.0 / NULLIF(execution_count, 0) AS avg_worker_ms", sql, StringComparison.Ordinal);
        Assert.Contains("total_elapsed_us / 1000000.0 AS total_elapsed_sec", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedSql_QueryStatsObjectNameIsAdhoc_BecauseTheStoreLacksObjectColumns()
    {
        /* Darling's query_stats has NO object_type/schema_name/object_name columns (the Dashboard's does), so
           the Query Stats source's object_name is the constant 'Adhoc' — the Dashboard view's STATEMENT case. */
        var sql = ViewerDataService.UnifiedExpensiveQueriesSql;
        Assert.Contains("'Adhoc' AS object_name", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY database_name, query_hash", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedSql_ProcedureGrantIsNull_QueryStoreGrantIs8KbPagesToMb()
    {
        var sql = ViewerDataService.UnifiedExpensiveQueriesSql;
        /* Procedure DMVs carry no grant columns → NULL (the Dashboard view carries NULL too). */
        Assert.Contains("NULL::double precision AS max_grant_mb", sql, StringComparison.Ordinal);
        /* query_stats grant is genuine KB → MB. */
        Assert.Contains("(MAX(max_grant_kb) / 1024.0)::double precision AS max_grant_mb", sql, StringComparison.Ordinal);
        /* Query Store max_query_max_used_memory is 8-KB PAGES → MB (* 8 / 1024) — unit-correct + consistent with
           Darling's Query Store tab; the Dashboard view treats the page count as KB (a latent 8x unit bug). */
        Assert.Contains("(MAX(max_query_max_used_memory) * 8.0 / 1024.0)::double precision AS max_grant_mb", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedSql_QueryStorePlanColumnIsQueryPlanText_AliasedToQueryPlanXml()
    {
        /* query_stats/procedure_stats store the plan in query_plan_xml; query_store_stats stores it in
           query_plan_text — the QS branch aliases it so every branch's plan column lines up. */
        var sql = ViewerDataService.UnifiedExpensiveQueriesSql;
        Assert.Contains("t.query_plan_text AS query_plan_xml", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedSql_CarriesPlanHandle_OnStatementAndProcedureArms_NullForQueryStore()
    {
        /* "Fetch Live Plan" is keyed on plan_handle: the query_stats + procedure_stats arms carry
           MAX(plan_handle) (mirroring their standalone reads), while the Query Store arm has none
           (NULL::text) — so QS-sourced rows keep "Fetch Live Plan" correctly disabled. The UNION stays
           column-aligned across all three branches. */
        var sql = ViewerDataService.UnifiedExpensiveQueriesSql;
        Assert.Equal(2, CountOccurrences(sql, "MAX(plan_handle) AS plan_handle"));
        Assert.Contains("NULL::text AS plan_handle", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UnifiedSql_IsPostgresDialect_SixPositionalParams_NoTsqlIsms()
    {
        var sql = ViewerDataService.UnifiedExpensiveQueriesSql;
        Assert.DoesNotContain("getdate", sql.ToLowerInvariant());
        /* No T-SQL national-string literals or @named params (the "N'" guard the sibling reads use is skipped
           here — it false-positives on the 'FUNCTION' object_type literal, whose word ends in N; the @ + getdate
           + positional-param checks are the meaningful T-SQL-ism guards). */
        Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
        Assert.Contains("$1", sql, StringComparison.Ordinal);
        Assert.Contains("$5", sql, StringComparison.Ordinal);
        /* #1319: the global database filter is the 6th positional param — a nullable text[] applied as
           database_name = ANY($6) in all three UNION-ALL source branches. */
        Assert.Contains("database_name = ANY($6)", sql, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}

/// <summary>The unified Expensive Queries row's pure display + source-classification helpers.</summary>
public sealed class ViewerUnifiedExpensiveQueriesRowTests
{
    [Fact]
    public void SourceLabelConstants_HaveTheExpectedValues()
    {
        Assert.Equal("Query Stats", ViewerDataService.ExpensiveSourceQueryStats);
        Assert.Equal("Stored Procedure", ViewerDataService.ExpensiveSourceStoredProcedure);
        Assert.Equal("Trigger", ViewerDataService.ExpensiveSourceTrigger);
        Assert.Equal("Function", ViewerDataService.ExpensiveSourceFunction);
        Assert.Equal("Query Store", ViewerDataService.ExpensiveSourceQueryStore);
    }

    [Theory]
    [InlineData("Query Stats", true)]
    [InlineData("Query Store", true)]
    [InlineData("Stored Procedure", false)]
    [InlineData("Trigger", false)]
    [InlineData("Function", false)]
    public void IsStatementSource_TrueOnlyForTheStatementSources(string source, bool expected)
    {
        /* Only the statement sources carry runnable query text; the procedure sources carry the object name, so
           they gate OUT of "Copy Repro Script" (matching Lite, where procedure rows have no repro). */
        Assert.Equal(expected, new ViewerExpensiveQueryRow { Source = source }.IsStatementSource);
    }

    [Fact]
    public void HasQueryPlan_ReflectsTheInRowPlanPresence()
    {
        Assert.True(new ViewerExpensiveQueryRow { QueryPlanXml = "<ShowPlanXML/>" }.HasQueryPlan);
        Assert.False(new ViewerExpensiveQueryRow { QueryPlanXml = null }.HasQueryPlan);
        Assert.False(new ViewerExpensiveQueryRow { QueryPlanXml = "" }.HasQueryPlan);
    }

    [Fact]
    public void HasLivePlanHandle_ReflectsThePlanHandlePresence()
    {
        /* Gates "Fetch Live Plan": the query_stats + procedure_stats sources carry a plan_handle; a Query
           Store row (or any plan_handle-less row) leaves it empty and stays disabled. */
        Assert.True(new ViewerExpensiveQueryRow { PlanHandle = "0x06000100AB" }.HasLivePlanHandle);
        Assert.False(new ViewerExpensiveQueryRow { PlanHandle = "" }.HasLivePlanHandle);
    }

    [Fact]
    public void ExecutionTimes_FormatAsRawServerClock_EmptyForNull()
    {
        var row = new ViewerExpensiveQueryRow
        {
            FirstExecutionTime = new DateTime(2026, 7, 1, 8, 30, 0),
            LastExecutionTime = null,
        };
        Assert.Equal("2026-07-01 08:30:00", row.FirstExecutionTimeLocal);
        Assert.Equal("", row.LastExecutionTimeLocal);
    }
}

/// <summary>
/// The unified Expensive Queries row's "Copy Repro Script" mapping (through the shared
/// <see cref="ViewerServerTab.BuildReproScriptForRow"/>): the statement sources build a repro from their
/// in-row stored plan; the procedure sources (no runnable text) no-op. Pure mapping — no connection.
/// </summary>
public sealed class ViewerUnifiedExpensiveQueriesReproTests
{
    private const string Product = "SQL Server Performance Monitor Test";

    [Fact]
    public void QueryStatsSourceRow_BuildsReproFromInRowText_AndInRowStoredPlan()
    {
        var row = new ViewerExpensiveQueryRow
        {
            Source = ViewerDataService.ExpensiveSourceQueryStats,
            QueryText = "SELECT COUNT(*) FROM dbo.Orders",
            DatabaseName = "sales",
            QueryPlanXml = "<ShowPlanXML>x</ShowPlanXML>",
        };

        /* enrichedPlanXml is ignored for this source — the plan rides in-row (like the Active Queries case). */
        var script = ViewerServerTab.BuildReproScriptForRow(row, enrichedPlanXml: null, Product);

        Assert.NotNull(script);
        Assert.Contains("SELECT COUNT(*) FROM dbo.Orders", script!, StringComparison.Ordinal);
        Assert.Contains("USE [sales];", script, StringComparison.Ordinal);
        Assert.Contains("Source: Expensive Queries", script, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryStoreSourceRow_BuildsRepro()
    {
        var row = new ViewerExpensiveQueryRow
        {
            Source = ViewerDataService.ExpensiveSourceQueryStore,
            QueryText = "UPDATE dbo.Inventory SET Qty = Qty - 1",
            DatabaseName = "wh",
        };

        var script = ViewerServerTab.BuildReproScriptForRow(row, enrichedPlanXml: null, Product);

        Assert.NotNull(script);
        Assert.Contains("UPDATE dbo.Inventory SET Qty = Qty - 1", script!, StringComparison.Ordinal);
        Assert.Contains("Source: Expensive Queries", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Stored Procedure")]
    [InlineData("Trigger")]
    [InlineData("Function")]
    public void ProcedureSourceRow_NoOps_BecauseItHasNoRunnableStatementText(string source)
    {
        /* The procedure sources carry the object name as their "query text", not a runnable statement, so the
           repro no-ops (returns null) exactly like Lite's procedure rows — no malformed EXEC is produced. */
        var row = new ViewerExpensiveQueryRow
        {
            Source = source,
            QueryText = "sales.dbo.usp_DoWork",
            DatabaseName = "sales",
            QueryPlanXml = "<ShowPlanXML/>",
        };

        Assert.Null(ViewerServerTab.BuildReproScriptForRow(row, enrichedPlanXml: null, Product));
    }

    [Fact]
    public void StatementSourceRow_WithEmptyText_ReturnsNull()
    {
        var row = new ViewerExpensiveQueryRow { Source = ViewerDataService.ExpensiveSourceQueryStats, QueryText = "" };
        Assert.Null(ViewerServerTab.BuildReproScriptForRow(row, enrichedPlanXml: null, Product));
    }
}

/// <summary>
/// Gated (DARLING_TEST_PG) live round-trip for the unified Expensive Queries read: plants query-stats,
/// procedure-stats and query-store rows for a negative sentinel server across two collections each, then
/// asserts the read merges all three sources into one list, ranks by average worker time, applies the correct
/// per-source unit conversions (µs→sec/ms, 8-KB pages→MB), and carries the store-driven object_name / grant
/// deviations end-to-end (the string pins can't catch a dialect error or an ordinal slip). Shares the
/// serialized "live-postgres" collection and cleans up in finally.
/// </summary>
[Collection("live-postgres")]
public sealed class ViewerUnifiedExpensiveQueriesLivePostgresTests
{
    private const int ServerId = -970901;

    private static string? ConnectionString => Environment.GetEnvironmentVariable("DARLING_TEST_PG");

    [Fact]
    public async Task Unified_MergesThreeSources_RanksByAvgWorker_ConvertsUnits_AgainstDevPostgres()
    {
        var cs = ConnectionString;
        Assert.SkipWhen(string.IsNullOrEmpty(cs), "Set DARLING_TEST_PG to a Postgres connection string to run the live unified Expensive Queries test.");

        using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteRowsAsync(connection, "query_stats");
        await DeleteRowsAsync(connection, "procedure_stats");
        await DeleteRowsAsync(connection, "query_store_stats");

        await using var viewer = new ViewerDataService(cs!);
        var end = TruncateToSeconds(DateTime.UtcNow);
        var start = end.AddHours(-24);
        var t1 = start.AddHours(1);
        var t2 = start.AddHours(2);

        try
        {
            /* Query Stats: highest avg worker. Two collections summed → exec 5, worker 500_000 us → avg 100 ms.
               Same plan_handle in both collections so MAX(plan_handle) is deterministic. */
            await InsertQueryStatsAsync(connection, t1, "StackOverflow", "0xHOT",
                deltaExec: 2, deltaWorker: 200_000, deltaElapsed: 400_000, deltaReads: 200,
                maxGrantKb: 2048, queryText: "SELECT hot", planXml: "<ShowPlanXML>qs</ShowPlanXML>", planHandle: "0x0500QSHANDLE");
            await InsertQueryStatsAsync(connection, t2, "StackOverflow", "0xHOT",
                deltaExec: 3, deltaWorker: 300_000, deltaElapsed: 600_000, deltaReads: 300,
                maxGrantKb: 2048, queryText: "SELECT hot", planXml: "<ShowPlanXML>qs</ShowPlanXML>", planHandle: "0x0500QSHANDLE");

            /* Procedure: medium avg worker. exec 4, worker 200_000 us → avg 50 ms. No grant column. */
            await InsertProcedureStatsAsync(connection, t1, "StackOverflow", "dbo", "usp_Mid", "PROCEDURE",
                deltaExec: 4, deltaWorker: 200_000, deltaElapsed: 400_000, deltaReads: 400,
                planXml: "<ShowPlanXML>ps</ShowPlanXML>", planHandle: "0x0500PSHANDLE");

            /* Query Store: lowest avg worker. exec 10, worker = avg 10_000 us * 10 → avg 10 ms. Grant 1024 pages. */
            await InsertQueryStoreAsync(connection, t1, "StackOverflow", queryId: 99, module: "dbo.usp_Qs",
                execCount: 10, avgCpuUs: 10_000, avgDurationUs: 20_000, maxMemPages: 1024,
                queryText: "SELECT qs", planText: "<ShowPlanXML>qsd</ShowPlanXML>");

            var rows = await viewer.GetUnifiedExpensiveQueriesAsync(ServerId, start, end);

            Assert.Equal(3, rows.Count);

            /* Ranked by average worker time descending: Query Stats (100 ms) > Procedure (50 ms) > Query Store (10 ms). */
            Assert.Equal(ViewerDataService.ExpensiveSourceQueryStats, rows[0].Source);
            Assert.Equal("Adhoc", rows[0].ObjectName);              /* store has no object columns for query_stats */
            Assert.Equal(5, rows[0].ExecutionCount);                /* 2 + 3 summed across collections */
            Assert.Equal(100.0, rows[0].AvgWorkerTimeMs, 3);        /* 500_000 us / 5 execs → ms */
            Assert.Equal(0.5, rows[0].TotalWorkerTimeSec, 3);       /* 500_000 us → sec */
            Assert.Equal(2.0, rows[0].MaxGrantMb!.Value, 3);        /* 2048 KB → 2 MB */
            Assert.Equal("SELECT hot", rows[0].QueryText);
            Assert.Equal("<ShowPlanXML>qs</ShowPlanXML>", rows[0].QueryPlanXml);
            Assert.Equal("0x0500QSHANDLE", rows[0].PlanHandle);     /* MAX(plan_handle) across both collections */
            Assert.True(rows[0].HasLivePlanHandle);

            Assert.Equal(ViewerDataService.ExpensiveSourceStoredProcedure, rows[1].Source);
            Assert.Equal("dbo.usp_Mid", rows[1].ObjectName);
            Assert.Equal(50.0, rows[1].AvgWorkerTimeMs, 3);
            Assert.Null(rows[1].MaxGrantMb);                        /* procedure DMVs carry no grant */
            Assert.Equal("0x0500PSHANDLE", rows[1].PlanHandle);     /* the procedure arm carries plan_handle too */

            Assert.Equal(ViewerDataService.ExpensiveSourceQueryStore, rows[2].Source);
            Assert.Equal("dbo.usp_Qs", rows[2].ObjectName);
            Assert.Equal(10, rows[2].ExecutionCount);
            Assert.Equal(10.0, rows[2].AvgWorkerTimeMs, 3);
            Assert.Equal(8.0, rows[2].MaxGrantMb!.Value, 3);        /* 1024 pages * 8 KB / 1024 → 8 MB */
            Assert.Equal("", rows[2].PlanHandle);                   /* Query Store has no plan_handle → disabled */
            Assert.False(rows[2].HasLivePlanHandle);
        }
        finally
        {
            await DeleteRowsAsync(connection, "query_stats");
            await DeleteRowsAsync(connection, "procedure_stats");
            await DeleteRowsAsync(connection, "query_store_stats");
        }
    }

    // ── Insert helpers (only the columns the unified read touches; the rest default to NULL) ──

    private static async Task InsertQueryStatsAsync(
        NpgsqlConnection connection, DateTime collectionTimeUtc, string databaseName, string queryHash,
        long deltaExec, long deltaWorker, long deltaElapsed, long deltaReads, long maxGrantKb, string queryText, string planXml,
        string planHandle)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO query_stats
    (collection_id, collection_time, server_id, server_name,
     database_name, query_hash, creation_time, last_execution_time,
     delta_execution_count, delta_worker_time, delta_elapsed_time,
     delta_logical_reads, delta_logical_writes, delta_physical_reads,
     max_grant_kb, query_text, query_plan_xml, plan_handle)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18)", connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(ServerId);
        command.Parameters.AddWithValue("viewer-expensive-e2e");
        command.Parameters.AddWithValue(databaseName);
        command.Parameters.AddWithValue(queryHash);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc.AddHours(-1), DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(deltaExec);
        command.Parameters.AddWithValue(deltaWorker);
        command.Parameters.AddWithValue(deltaElapsed);
        command.Parameters.AddWithValue(deltaReads);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(maxGrantKb);
        command.Parameters.AddWithValue(queryText);
        command.Parameters.AddWithValue(planXml);
        command.Parameters.AddWithValue(planHandle);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertProcedureStatsAsync(
        NpgsqlConnection connection, DateTime collectionTimeUtc,
        string databaseName, string schemaName, string objectName, string objectType,
        long deltaExec, long deltaWorker, long deltaElapsed, long deltaReads, string planXml, string planHandle)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO procedure_stats
    (collection_id, collection_time, server_id, server_name,
     database_name, schema_name, object_name, object_type,
     cached_time, last_execution_time,
     delta_execution_count, delta_worker_time, delta_elapsed_time,
     delta_logical_reads, delta_logical_writes, delta_physical_reads, query_plan_xml, plan_handle)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18)", connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(ServerId);
        command.Parameters.AddWithValue("viewer-expensive-e2e");
        command.Parameters.AddWithValue(databaseName);
        command.Parameters.AddWithValue(schemaName);
        command.Parameters.AddWithValue(objectName);
        command.Parameters.AddWithValue(objectType);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc.AddHours(-1), DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(deltaExec);
        command.Parameters.AddWithValue(deltaWorker);
        command.Parameters.AddWithValue(deltaElapsed);
        command.Parameters.AddWithValue(deltaReads);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(planXml);
        command.Parameters.AddWithValue(planHandle);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertQueryStoreAsync(
        NpgsqlConnection connection, DateTime collectionTimeUtc, string databaseName,
        long queryId, string module, long execCount, long avgCpuUs, long avgDurationUs, long maxMemPages,
        string queryText, string planText)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO query_store_stats
    (collection_id, collection_time, server_id, server_name,
     database_name, query_id, plan_id, module_name, query_text, query_plan_text,
     first_execution_time, last_execution_time,
     execution_count, avg_duration_us, avg_cpu_time_us,
     avg_logical_io_reads, avg_logical_io_writes, avg_physical_io_reads, max_query_max_used_memory)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19)", connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(ServerId);
        command.Parameters.AddWithValue("viewer-expensive-e2e");
        command.Parameters.AddWithValue(databaseName);
        command.Parameters.AddWithValue(queryId);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(module);
        command.Parameters.AddWithValue(queryText);
        command.Parameters.AddWithValue(planText);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc.AddHours(-1), DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(execCount);
        command.Parameters.AddWithValue(avgDurationUs);
        command.Parameters.AddWithValue(avgCpuUs);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(0L);
        command.Parameters.AddWithValue(maxMemPages);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        DateTime.SpecifyKind(new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond)), DateTimeKind.Unspecified);

    private static async Task DeleteRowsAsync(NpgsqlConnection connection, string table)
    {
        using var cleanup = new NpgsqlCommand($"DELETE FROM {table} WHERE server_id = {ServerId};", connection);
        await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
