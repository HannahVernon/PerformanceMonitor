/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using PerformanceMonitorLite.Database;
using PerformanceMonitorLite.Services;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Real-DuckDB round-trip pins for the unified "Expensive Queries" reader
/// (<see cref="LocalDataService.GetUnifiedExpensiveQueriesAsync"/>) — the Lite port of Darling's cross-source
/// ranked list, itself the viewer copy of the Dashboard's <c>report.expensive_queries_today</c>. These seed
/// the three archive views' base tables (query_stats / procedure_stats / query_store_stats), read back
/// through the reader, and assert the UNION's cross-source ranking by AVERAGE worker (CPU) time, the SQL-side
/// unit conversions (µs→ms/sec, KB/page→MB), the per-source labels + object-name shapes, the in-row stored
/// plan (and the Query Store store-driven deviation: no plan / no plan_handle), delta summing across
/// snapshots, the #1319 database filter, and window bounding. Mirrors the Darling ExpensiveQueries scenarios.
/// </summary>
public sealed class ExpensiveQueriesReaderTests : IDisposable
{
    private const int ServerId = 9911;

    private readonly string _tempDir;
    private readonly DuckDbInitializer _duckDb;
    private long _nextId = 1;

    public ExpensiveQueriesReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LiteExpQryRt_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _duckDb = new DuckDbInitializer(Path.Combine(_tempDir, "test.duckdb"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch { /* Best-effort cleanup */ }
    }

    private static DateTime Truncate(DateTime t) =>
        new(t.Year, t.Month, t.Day, t.Hour, t.Minute, t.Second, DateTimeKind.Unspecified);

    // ── Cross-source ranking + labels + conversions + in-row plan ──

    [Fact]
    public async Task Unified_RanksAllThreeSourcesByAvgWorkerTime_WithLabelsConversionsAndInRowPlan()
    {
        await _duckDb.InitializeAsync();
        var service = new LocalDataService(_duckDb);

        var at = Truncate(DateTime.UtcNow.AddHours(-1));
        var first = Truncate(DateTime.UtcNow.AddHours(-5));

        /* Query Stats: exec 10, worker 100_000µs -> avg 10 ms; grant 2048 KB -> 2 MB. */
        await SeedQueryStatsAsync(at, "DB1", "0xAAA",
            deltaExec: 10, deltaWorkerUs: 100_000, deltaElapsedUs: 200_000,
            deltaLogicalReads: 1000, deltaLogicalWrites: 50, deltaPhysicalReads: 20,
            maxGrantKb: 2048, creation: first, lastExec: at,
            queryText: "SELECT 1", planXml: "<ShowPlanXML>qs</ShowPlanXML>", planHandle: "0xPH_QS");

        /* Stored Procedure: exec 5, worker 500_000µs -> avg 100 ms (highest); procs carry NO grant. */
        await SeedProcedureStatsAsync(at, "DB1", "dbo", "usp_B", "PROCEDURE",
            deltaExec: 5, deltaWorkerUs: 500_000, deltaElapsedUs: 750_000,
            deltaLogicalReads: 5000, deltaLogicalWrites: 500, deltaPhysicalReads: 100,
            cached: first, lastExec: at, planHandle: "0xPH_PROC", planXml: "<ShowPlanXML>proc</ShowPlanXML>");

        /* Query Store: exec 20, avg cpu 2500µs -> total 50_000µs -> avg 2.5 ms (lowest); module NULL -> 'Ad Hoc';
           mem 256 pages -> 2 MB; NO stored plan, NO plan_handle. */
        await SeedQueryStoreAsync(at, "DB1", queryId: 42, moduleName: null,
            execCount: 20, avgCpuUs: 2500, avgDurationUs: 5000,
            avgLogicalReads: 300, avgLogicalWrites: 10, avgPhysicalReads: 5,
            maxMemoryPages: 256, first: first, lastExec: at, queryText: "SELECT 42");

        var rows = await service.GetUnifiedExpensiveQueriesAsync(ServerId);
        Assert.Equal(3, rows.Count);

        /* Ranked by AvgWorkerTimeMs DESC: Stored Procedure (100) > Query Stats (10) > Query Store (2.5). */
        var proc = rows[0];
        Assert.Equal(LocalDataService.ExpensiveSourceStoredProcedure, proc.Source);
        Assert.Equal("dbo.usp_B", proc.ObjectName);
        Assert.Equal(5, proc.ExecutionCount);
        Assert.Equal(100.0, proc.AvgWorkerTimeMs, 3);
        Assert.Equal(0.5, proc.TotalWorkerTimeSec, 3);
        Assert.Equal(0.75, proc.TotalElapsedTimeSec, 3);
        Assert.Equal(150.0, proc.AvgElapsedTimeMs, 3);
        Assert.Equal(1000, proc.AvgLogicalReads);
        Assert.Null(proc.MaxGrantMb);                       // procedures have no grant columns
        Assert.True(proc.HasQueryPlan);                     // in-row stored plan
        Assert.Equal("<ShowPlanXML>proc</ShowPlanXML>", proc.QueryPlanXml);
        Assert.True(proc.HasLivePlanHandle);
        Assert.False(proc.IsStatementSource);               // object name, not runnable text

        var qs = rows[1];
        Assert.Equal(LocalDataService.ExpensiveSourceQueryStats, qs.Source);
        Assert.Equal("Adhoc", qs.ObjectName);               // no object columns in Lite's query_stats
        Assert.Equal(10.0, qs.AvgWorkerTimeMs, 3);
        Assert.Equal(0.1, qs.TotalWorkerTimeSec, 3);
        Assert.Equal(20.0, qs.AvgElapsedTimeMs, 3);
        Assert.Equal(100, qs.AvgLogicalReads);              // floor(1000/10)
        Assert.Equal(5, qs.AvgLogicalWrites);               // floor(50/10)
        Assert.Equal(2, qs.AvgPhysicalReads);               // floor(20/10)
        Assert.Equal(2.0, qs.MaxGrantMb!.Value, 3);         // 2048 KB / 1024
        Assert.Equal("SELECT 1", qs.QueryText);
        Assert.True(qs.HasQueryPlan);
        Assert.True(qs.HasLivePlanHandle);
        Assert.True(qs.IsStatementSource);

        var store = rows[2];
        Assert.Equal(LocalDataService.ExpensiveSourceQueryStore, store.Source);
        Assert.Equal("Ad Hoc", store.ObjectName);           // module NULL -> COALESCE 'Ad Hoc'
        Assert.Equal(20, store.ExecutionCount);
        Assert.Equal(2.5, store.AvgWorkerTimeMs, 3);
        Assert.Equal(0.05, store.TotalWorkerTimeSec, 3);
        Assert.Equal(5.0, store.AvgElapsedTimeMs, 3);
        Assert.Equal(6000, store.TotalLogicalReads);        // 300 * 20 (weighted)
        Assert.Equal(300, store.AvgLogicalReads);
        Assert.Equal(2.0, store.MaxGrantMb!.Value, 3);      // 256 pages * 8 / 1024
        Assert.Equal("SELECT 42", store.QueryText);
        Assert.False(store.HasQueryPlan);                   // Lite's query_store stores no plan
        Assert.False(store.HasLivePlanHandle);              // and no plan_handle
        Assert.True(store.IsStatementSource);               // runnable statement text
    }

    // ── object_type -> source-label mapping ──

    [Fact]
    public async Task Unified_MapsProcedureObjectTypesToTriggerAndFunctionLabels()
    {
        await _duckDb.InitializeAsync();
        var service = new LocalDataService(_duckDb);

        var at = Truncate(DateTime.UtcNow.AddHours(-1));

        await SeedProcedureStatsAsync(at, "DB1", "dbo", "trg_Audit", "TRIGGER",
            deltaExec: 3, deltaWorkerUs: 30_000, deltaElapsedUs: 60_000,
            deltaLogicalReads: 30, deltaLogicalWrites: 3, deltaPhysicalReads: 0,
            cached: at, lastExec: at, planHandle: "0xT", planXml: null);
        await SeedProcedureStatsAsync(at, "DB1", "dbo", "fn_Calc", "FUNCTION",
            deltaExec: 4, deltaWorkerUs: 20_000, deltaElapsedUs: 40_000,
            deltaLogicalReads: 40, deltaLogicalWrites: 0, deltaPhysicalReads: 0,
            cached: at, lastExec: at, planHandle: "0xF", planXml: null);

        var rows = await service.GetUnifiedExpensiveQueriesAsync(ServerId);

        Assert.Equal(LocalDataService.ExpensiveSourceTrigger,
            Assert.Single(rows, r => r.ObjectName == "dbo.trg_Audit").Source);
        Assert.Equal(LocalDataService.ExpensiveSourceFunction,
            Assert.Single(rows, r => r.ObjectName == "dbo.fn_Calc").Source);
    }

    // ── delta summing across snapshots ──

    [Fact]
    public async Task Unified_SumsQueryStatsDeltasAcrossCollectionTimes()
    {
        await _duckDb.InitializeAsync();
        var service = new LocalDataService(_duckDb);

        var t1 = Truncate(DateTime.UtcNow.AddHours(-3));
        var t2 = Truncate(DateTime.UtcNow.AddHours(-2));

        /* Two snapshots of the SAME (db, query_hash): deltas SUM to exec 30, worker 300_000µs -> avg 10 ms. */
        await SeedQueryStatsAsync(t1, "DB1", "0xSUM",
            deltaExec: 10, deltaWorkerUs: 100_000, deltaElapsedUs: 100_000,
            deltaLogicalReads: 100, deltaLogicalWrites: 0, deltaPhysicalReads: 0,
            maxGrantKb: 1024, creation: t1, lastExec: t1, queryText: "SELECT 1", planXml: null, planHandle: "0xP");
        await SeedQueryStatsAsync(t2, "DB1", "0xSUM",
            deltaExec: 20, deltaWorkerUs: 200_000, deltaElapsedUs: 200_000,
            deltaLogicalReads: 200, deltaLogicalWrites: 0, deltaPhysicalReads: 0,
            maxGrantKb: 4096, creation: t1, lastExec: t2, queryText: "SELECT 1", planXml: null, planHandle: "0xP");

        var row = Assert.Single(await service.GetUnifiedExpensiveQueriesAsync(ServerId));
        Assert.Equal(30, row.ExecutionCount);
        Assert.Equal(0.3, row.TotalWorkerTimeSec, 3);       // 300_000 µs
        Assert.Equal(10.0, row.AvgWorkerTimeMs, 3);         // 300_000/1000/30
        Assert.Equal(300, row.TotalLogicalReads);
        Assert.Equal(4.0, row.MaxGrantMb!.Value, 3);        // MAX(4096) / 1024
    }

    // ── #1319 database filter ──

    [Fact]
    public async Task Unified_DatabaseFilter_1319_FiltersInSql()
    {
        await _duckDb.InitializeAsync();
        var service = new LocalDataService(_duckDb);

        var at = Truncate(DateTime.UtcNow.AddHours(-1));

        await SeedQueryStatsAsync(at, "DbA", "0xA",
            deltaExec: 10, deltaWorkerUs: 100_000, deltaElapsedUs: 100_000,
            deltaLogicalReads: 10, deltaLogicalWrites: 0, deltaPhysicalReads: 0,
            maxGrantKb: 0, creation: at, lastExec: at, queryText: "A", planXml: null, planHandle: "");
        await SeedQueryStatsAsync(at, "DbB", "0xB",
            deltaExec: 10, deltaWorkerUs: 100_000, deltaElapsedUs: 100_000,
            deltaLogicalReads: 10, deltaLogicalWrites: 0, deltaPhysicalReads: 0,
            maxGrantKb: 0, creation: at, lastExec: at, queryText: "B", planXml: null, planHandle: "");

        Assert.Equal(2, (await service.GetUnifiedExpensiveQueriesAsync(ServerId)).Count);
        var filtered = await service.GetUnifiedExpensiveQueriesAsync(ServerId, databaseNames: new[] { "DbA" });
        Assert.Equal("DbA", Assert.Single(filtered).DatabaseName);
    }

    // ── window bounding ──

    [Fact]
    public async Task Unified_ExcludesRowsOutsideTheWindow()
    {
        await _duckDb.InitializeAsync();
        var service = new LocalDataService(_duckDb);

        /* Only capture is 48h ago — outside a 24h window. */
        await SeedQueryStatsAsync(Truncate(DateTime.UtcNow.AddHours(-48)), "DB1", "0xOLD",
            deltaExec: 10, deltaWorkerUs: 100_000, deltaElapsedUs: 100_000,
            deltaLogicalReads: 10, deltaLogicalWrites: 0, deltaPhysicalReads: 0,
            maxGrantKb: 0, creation: Truncate(DateTime.UtcNow.AddHours(-48)),
            lastExec: Truncate(DateTime.UtcNow.AddHours(-48)), queryText: "old", planXml: null, planHandle: "");

        Assert.Empty(await service.GetUnifiedExpensiveQueriesAsync(ServerId, hoursBack: 24));
    }

    // ── final-cap ordering across a larger set ──

    [Fact]
    public async Task Unified_FinalTop_KeepsHighestAvgWorkerAcrossSources()
    {
        await _duckDb.InitializeAsync();
        var service = new LocalDataService(_duckDb);

        var at = Truncate(DateTime.UtcNow.AddHours(-1));

        /* Five query_stats groups with ascending avg CPU (1..5 ms); finalTop=2 keeps the top two (5 ms, 4 ms). */
        for (int i = 1; i <= 5; i++)
        {
            await SeedQueryStatsAsync(at, "DB1", $"0xH{i}",
                deltaExec: 10, deltaWorkerUs: i * 10_000, deltaElapsedUs: i * 10_000,
                deltaLogicalReads: 10, deltaLogicalWrites: 0, deltaPhysicalReads: 0,
                maxGrantKb: 0, creation: at, lastExec: at, queryText: $"q{i}", planXml: null, planHandle: "");
        }

        var top2 = await service.GetUnifiedExpensiveQueriesAsync(ServerId, finalTop: 2);
        Assert.Equal(2, top2.Count);
        Assert.Equal(5.0, top2[0].AvgWorkerTimeMs, 3);
        Assert.Equal(4.0, top2[1].AvgWorkerTimeMs, 3);
    }

    // ── Seeding helpers (raw INSERT via the shared initializer's connection) ──

    private async Task SeedQueryStatsAsync(
        DateTime collectionTimeUtc, string databaseName, string queryHash,
        long deltaExec, long deltaWorkerUs, long deltaElapsedUs,
        long deltaLogicalReads, long deltaLogicalWrites, long deltaPhysicalReads,
        long maxGrantKb, DateTime creation, DateTime lastExec,
        string queryText, string? planXml, string planHandle)
    {
        using var readLock = _duckDb.AcquireReadLock();
        using var connection = _duckDb.CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO query_stats
    (collection_id, collection_time, server_id, server_name, database_name, query_hash,
     delta_execution_count, delta_worker_time, delta_elapsed_time,
     delta_logical_reads, delta_logical_writes, delta_physical_reads,
     max_grant_kb, creation_time, last_execution_time, query_text, query_plan_xml, plan_handle)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18)";
        cmd.Parameters.Add(new DuckDBParameter { Value = _nextId++ });
        cmd.Parameters.Add(new DuckDBParameter { Value = collectionTimeUtc });
        cmd.Parameters.Add(new DuckDBParameter { Value = ServerId });
        cmd.Parameters.Add(new DuckDBParameter { Value = "TestSrv" });
        cmd.Parameters.Add(new DuckDBParameter { Value = databaseName });
        cmd.Parameters.Add(new DuckDBParameter { Value = queryHash });
        cmd.Parameters.Add(new DuckDBParameter { Value = deltaExec });
        cmd.Parameters.Add(new DuckDBParameter { Value = deltaWorkerUs });
        cmd.Parameters.Add(new DuckDBParameter { Value = deltaElapsedUs });
        cmd.Parameters.Add(new DuckDBParameter { Value = deltaLogicalReads });
        cmd.Parameters.Add(new DuckDBParameter { Value = deltaLogicalWrites });
        cmd.Parameters.Add(new DuckDBParameter { Value = deltaPhysicalReads });
        cmd.Parameters.Add(new DuckDBParameter { Value = maxGrantKb });
        cmd.Parameters.Add(new DuckDBParameter { Value = creation });
        cmd.Parameters.Add(new DuckDBParameter { Value = lastExec });
        cmd.Parameters.Add(new DuckDBParameter { Value = queryText });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)planXml ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = planHandle });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedProcedureStatsAsync(
        DateTime collectionTimeUtc, string databaseName, string schemaName, string objectName, string objectType,
        long deltaExec, long deltaWorkerUs, long deltaElapsedUs,
        long deltaLogicalReads, long deltaLogicalWrites, long deltaPhysicalReads,
        DateTime cached, DateTime lastExec, string planHandle, string? planXml)
    {
        using var readLock = _duckDb.AcquireReadLock();
        using var connection = _duckDb.CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO procedure_stats
    (collection_id, collection_time, server_id, server_name, database_name, schema_name, object_name, object_type,
     delta_execution_count, delta_worker_time, delta_elapsed_time,
     delta_logical_reads, delta_logical_writes, delta_physical_reads,
     cached_time, last_execution_time, plan_handle, query_plan_xml)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18)";
        cmd.Parameters.Add(new DuckDBParameter { Value = _nextId++ });
        cmd.Parameters.Add(new DuckDBParameter { Value = collectionTimeUtc });
        cmd.Parameters.Add(new DuckDBParameter { Value = ServerId });
        cmd.Parameters.Add(new DuckDBParameter { Value = "TestSrv" });
        cmd.Parameters.Add(new DuckDBParameter { Value = databaseName });
        cmd.Parameters.Add(new DuckDBParameter { Value = schemaName });
        cmd.Parameters.Add(new DuckDBParameter { Value = objectName });
        cmd.Parameters.Add(new DuckDBParameter { Value = objectType });
        cmd.Parameters.Add(new DuckDBParameter { Value = deltaExec });
        cmd.Parameters.Add(new DuckDBParameter { Value = deltaWorkerUs });
        cmd.Parameters.Add(new DuckDBParameter { Value = deltaElapsedUs });
        cmd.Parameters.Add(new DuckDBParameter { Value = deltaLogicalReads });
        cmd.Parameters.Add(new DuckDBParameter { Value = deltaLogicalWrites });
        cmd.Parameters.Add(new DuckDBParameter { Value = deltaPhysicalReads });
        cmd.Parameters.Add(new DuckDBParameter { Value = cached });
        cmd.Parameters.Add(new DuckDBParameter { Value = lastExec });
        cmd.Parameters.Add(new DuckDBParameter { Value = planHandle });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)planXml ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedQueryStoreAsync(
        DateTime collectionTimeUtc, string databaseName, long queryId, string? moduleName,
        long execCount, long avgCpuUs, long avgDurationUs,
        long avgLogicalReads, long avgLogicalWrites, long avgPhysicalReads,
        long maxMemoryPages, DateTime first, DateTime lastExec, string queryText)
    {
        using var readLock = _duckDb.AcquireReadLock();
        using var connection = _duckDb.CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
INSERT INTO query_store_stats
    (collection_id, collection_time, server_id, server_name, database_name, query_id, module_name,
     execution_count, avg_cpu_time_us, avg_duration_us,
     avg_logical_io_reads, avg_logical_io_writes, avg_physical_io_reads,
     max_query_max_used_memory, first_execution_time, last_execution_time, query_text)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17)";
        cmd.Parameters.Add(new DuckDBParameter { Value = _nextId++ });
        cmd.Parameters.Add(new DuckDBParameter { Value = collectionTimeUtc });
        cmd.Parameters.Add(new DuckDBParameter { Value = ServerId });
        cmd.Parameters.Add(new DuckDBParameter { Value = "TestSrv" });
        cmd.Parameters.Add(new DuckDBParameter { Value = databaseName });
        cmd.Parameters.Add(new DuckDBParameter { Value = queryId });
        cmd.Parameters.Add(new DuckDBParameter { Value = (object?)moduleName ?? DBNull.Value });
        cmd.Parameters.Add(new DuckDBParameter { Value = execCount });
        cmd.Parameters.Add(new DuckDBParameter { Value = avgCpuUs });
        cmd.Parameters.Add(new DuckDBParameter { Value = avgDurationUs });
        cmd.Parameters.Add(new DuckDBParameter { Value = avgLogicalReads });
        cmd.Parameters.Add(new DuckDBParameter { Value = avgLogicalWrites });
        cmd.Parameters.Add(new DuckDBParameter { Value = avgPhysicalReads });
        cmd.Parameters.Add(new DuckDBParameter { Value = maxMemoryPages });
        cmd.Parameters.Add(new DuckDBParameter { Value = first });
        cmd.Parameters.Add(new DuckDBParameter { Value = lastExec });
        cmd.Parameters.Add(new DuckDBParameter { Value = queryText });
        await cmd.ExecuteNonQueryAsync();
    }
}
