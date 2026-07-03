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
using NpgsqlTypes;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the Overview trend-chart SQL against the Darling store contract (no live Postgres) and
/// unit-tests the wait-category roll-up. The two reads window on collection_time (naive-UTC), the
/// CPU aggregates are CAST to double precision for the typed reader, and the wait read is
/// deliberately un-filtered so <see cref="ViewerDataService.RollUpWaitCategories"/> can classify
/// every type through the shared <see cref="PerformanceMonitor.Common.ChartPalette.WaitCategory"/>.
/// </summary>
public sealed class ViewerTrendsSqlTests
{
    [Fact]
    public void CpuTrendSql_AveragesPerCollection_OverTheWindow()
    {
        Assert.Contains("FROM cpu_utilization_stats", ViewerDataService.CpuTrendSql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", ViewerDataService.CpuTrendSql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", ViewerDataService.CpuTrendSql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY collection_time", ViewerDataService.CpuTrendSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY collection_time", ViewerDataService.CpuTrendSql, StringComparison.Ordinal);
    }

    [Fact]
    public void CpuTrendSql_CastsAveragesToDoublePrecision()
    {
        /* AVG(integer) is numeric in Postgres; without the cast the typed GetDouble reader throws
           (the same reason the Queries read casts its SUMs back to bigint). */
        Assert.Contains("CAST(AVG(sqlserver_cpu_utilization) AS double precision)", ViewerDataService.CpuTrendSql, StringComparison.Ordinal);
        Assert.Contains("CAST(AVG(other_process_cpu_utilization) AS double precision)", ViewerDataService.CpuTrendSql, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitCategoryTrendSql_ReadsEveryTypeOverTheWindow_NewestByTime_NoTopNFilter()
    {
        Assert.Contains("FROM wait_stats", ViewerDataService.WaitCategoryTrendSql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", ViewerDataService.WaitCategoryTrendSql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", ViewerDataService.WaitCategoryTrendSql, StringComparison.Ordinal);
        Assert.Contains("wait_type", ViewerDataService.WaitCategoryTrendSql, StringComparison.Ordinal);
        Assert.Contains("delta_wait_time_ms", ViewerDataService.WaitCategoryTrendSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY collection_time", ViewerDataService.WaitCategoryTrendSql, StringComparison.Ordinal);

        /* The C# roll-up needs all types (top categories are not the top types), so the SQL must
           NOT pre-filter to a top-N or pre-aggregate away individual wait types. */
        Assert.DoesNotContain("LIMIT", ViewerDataService.WaitCategoryTrendSql, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP BY", ViewerDataService.WaitCategoryTrendSql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(ViewerDataService.CpuTrendSql))]
    [InlineData(nameof(ViewerDataService.WaitCategoryTrendSql))]
    public void TrendSql_PgDialect_PositionalParams_NoBareNow_NoNLiterals(string which)
    {
        var sql = which == nameof(ViewerDataService.CpuTrendSql)
            ? ViewerDataService.CpuTrendSql
            : ViewerDataService.WaitCategoryTrendSql;

        Assert.DoesNotContain("now(", sql.ToLowerInvariant());
        Assert.DoesNotContain("N'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
        Assert.Contains("$1", sql, StringComparison.Ordinal);
        Assert.Contains("$2", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CpuTrendSql_ReadsColumnsThatExistInTheGeneratedCpuTable()
    {
        /* The read's FROM table and columns must match the collector-generated schema. */
        Assert.Equal("cpu_utilization_stats", CpuUtilizationCollector.Instance.TargetTable);

        var ddl = PgSchemaGenerator.CreateTable(CpuUtilizationCollector.Instance);
        foreach (var column in new[] { "collection_time", "sqlserver_cpu_utilization", "other_process_cpu_utilization" })
        {
            Assert.Contains(column, ddl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WaitCategoryTrendSql_ReadsColumnsThatExistInTheGeneratedWaitTable()
    {
        Assert.Equal("wait_stats", WaitStatsCollector.Instance.TargetTable);

        var ddl = PgSchemaGenerator.CreateTable(WaitStatsCollector.Instance);
        foreach (var column in new[] { "collection_time", "wait_type", "delta_wait_time_ms" })
        {
            Assert.Contains(column, ddl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RollUpWaitCategories_SumsPerCategoryAndTime_RanksByTotalDescending()
    {
        var t0 = new DateTime(2026, 7, 1, 10, 0, 0);
        var t1 = t0.AddMinutes(5);

        var points = new[]
        {
            new WaitCategoryTrendPoint(t0, "CXPACKET", 100),      // Parallelism
            new WaitCategoryTrendPoint(t0, "CXCONSUMER", 20),     // Parallelism (same category + time → sums)
            new WaitCategoryTrendPoint(t1, "CXPACKET", 150),      // Parallelism
            new WaitCategoryTrendPoint(t0, "LCK_M_X", 200),       // Lock
            new WaitCategoryTrendPoint(t0, "PAGEIOLATCH_SH", 40), // Buffer IO
        };

        var series = ViewerDataService.RollUpWaitCategories(points);

        /* Totals: Parallelism 270, Lock 200, Buffer IO 40 → ranked by total descending. */
        Assert.Equal(new[] { "Parallelism", "Lock", "Buffer IO" }, series.Select(s => s.Category));

        /* Parallelism's two types share t0 (100+20=120) and CXPACKET holds t1 (150). */
        Assert.Equal(new[] { t0, t1 }, series[0].Times);
        Assert.Equal(new[] { 120.0, 150.0 }, series[0].Values);

        Assert.Equal(new[] { t0 }, series[1].Times);
        Assert.Equal(new[] { 200.0 }, series[1].Values);
    }

    [Fact]
    public void RollUpWaitCategories_UnknownWaitType_ClassifiesAsUnknown()
    {
        var points = new[]
        {
            new WaitCategoryTrendPoint(new DateTime(2026, 7, 1, 10, 0, 0), "TOTALLY_MADE_UP_WAIT", 5),
        };

        Assert.Equal("Unknown", ViewerDataService.RollUpWaitCategories(points).Single().Category);
    }

    [Fact]
    public void RollUpWaitCategories_CapsAtTheRequestedNumberOfCategories()
    {
        var t0 = new DateTime(2026, 7, 1, 10, 0, 0);
        var points = new[]
        {
            new WaitCategoryTrendPoint(t0, "CXPACKET", 300),       // Parallelism
            new WaitCategoryTrendPoint(t0, "LCK_M_X", 200),        // Lock
            new WaitCategoryTrendPoint(t0, "PAGEIOLATCH_SH", 100), // Buffer IO
        };

        var series = ViewerDataService.RollUpWaitCategories(points, topCategories: 2);

        /* Only the two highest-total categories survive the cap. */
        Assert.Equal(new[] { "Parallelism", "Lock" }, series.Select(s => s.Category));
    }

    [Fact]
    public void RollUpWaitCategories_EmptyInput_YieldsNoSeries_NullThrows()
    {
        Assert.Empty(ViewerDataService.RollUpWaitCategories(Array.Empty<WaitCategoryTrendPoint>()));
        Assert.Throws<ArgumentNullException>(() => ViewerDataService.RollUpWaitCategories(null!));
    }
}

/// <summary>
/// Gated (DARLING_TEST_PG) live round-trips for the two Overview trend reads: CPU averaging per
/// collection (with a NULL other-process sample reading as 0, the SQL-on-Linux case) and the wait
/// read feeding the category roll-up. Shares the serialized "live-postgres" collection so the row
/// churn can't race another class; uses negative sentinel server_ids and cleans up in finally.
/// </summary>
[Collection("live-postgres")]
public sealed class ViewerTrendsLivePostgresTests
{
    private const int CpuServerId = -929292;
    private const string CpuServerName = "viewer-trends-cpu-e2e";

    private const int WaitServerId = -939393;
    private const string WaitServerName = "viewer-trends-wait-e2e";

    [Fact]
    public async Task CpuTrend_AveragesPerCollection_AndReadsNullOtherAsZero_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live CPU-trend test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteCpuRowsAsync(connection);

        await using var viewer = new ViewerDataService(connectionString!);

        try
        {
            var t1 = TruncateToSeconds(DateTime.UtcNow.AddMinutes(-10));
            var t2 = t1.AddMinutes(5);

            /* One collection with two ring-buffer samples (avg SQL 20 / avg other 10) and a later
               collection with a single sample whose other-process CPU is NULL (SQL on Linux). */
            await InsertCpuRowAsync(connection, 1, t1, sqlCpu: 10, otherCpu: 5);
            await InsertCpuRowAsync(connection, 1, t1, sqlCpu: 30, otherCpu: 15);
            await InsertCpuRowAsync(connection, 2, t2, sqlCpu: 50, otherCpu: null);

            var points = await viewer.GetCpuTrendAsync(CpuServerId, t1.AddMinutes(-1));

            Assert.Equal(2, points.Count);

            /* Ordered by collection_time; averaged per collection. */
            Assert.Equal(t1.Ticks, points[0].CollectionTime.Ticks);
            Assert.Equal(20.0, points[0].SqlServerCpu, precision: 3);
            Assert.Equal(10.0, points[0].OtherProcessCpu, precision: 3);

            Assert.Equal(t2.Ticks, points[1].CollectionTime.Ticks);
            Assert.Equal(50.0, points[1].SqlServerCpu, precision: 3);
            /* NULL other-process CPU reads as 0. */
            Assert.Equal(0.0, points[1].OtherProcessCpu, precision: 3);
        }
        finally
        {
            await DeleteCpuRowsAsync(connection);
        }
    }

    [Fact]
    public async Task WaitCategoryTrend_ReadsRawRows_ThenRollsUpByCategory_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live wait-category test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteWaitRowsAsync(connection);

        await using var viewer = new ViewerDataService(connectionString!);

        try
        {
            var t1 = TruncateToSeconds(DateTime.UtcNow.AddMinutes(-10));
            var t2 = t1.AddMinutes(5);

            await InsertWaitRowAsync(connection, 1, t1, "CXPACKET", 100);       // Parallelism
            await InsertWaitRowAsync(connection, 1, t1, "PAGEIOLATCH_SH", 40);  // Buffer IO
            await InsertWaitRowAsync(connection, 2, t2, "CXPACKET", 150);       // Parallelism
            await InsertWaitRowAsync(connection, 2, t2, "LCK_M_X", 200);        // Lock

            var raw = await viewer.GetWaitCategoryTrendAsync(WaitServerId, t1.AddMinutes(-1));
            Assert.Equal(4, raw.Count);

            var series = ViewerDataService.RollUpWaitCategories(raw);

            /* Totals over the window: Parallelism 250, Lock 200, Buffer IO 40. */
            Assert.Equal(new[] { "Parallelism", "Lock", "Buffer IO" }, series.Select(s => s.Category));
            Assert.Equal(new[] { t1.Ticks, t2.Ticks }, series[0].Times.Select(t => t.Ticks));
            Assert.Equal(new[] { 100.0, 150.0 }, series[0].Values);
        }
        finally
        {
            await DeleteWaitRowsAsync(connection);
        }
    }

    private static async Task InsertCpuRowAsync(
        NpgsqlConnection connection, long collectionId, DateTime collectionTimeUtc, int sqlCpu, int? otherCpu)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO cpu_utilization_stats
    (collection_id, collection_time, server_id, server_name, sample_time,
     sqlserver_cpu_utilization, other_process_cpu_utilization)
VALUES ($1, $2, $3, $4, $5, $6, $7)", connection);
        command.Parameters.AddWithValue(collectionId);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(CpuServerId);
        command.Parameters.AddWithValue(CpuServerName);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(sqlCpu);
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)otherCpu ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertWaitRowAsync(
        NpgsqlConnection connection, long collectionId, DateTime collectionTimeUtc, string waitType, long deltaWaitTimeMs)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO wait_stats
    (collection_id, collection_time, server_id, server_name, wait_type,
     waiting_tasks_count, wait_time_ms, signal_wait_time_ms,
     delta_waiting_tasks, delta_wait_time_ms, delta_signal_wait_time_ms)
VALUES ($1, $2, $3, $4, $5, 0, 0, 0, 0, $6, 0)", connection);
        command.Parameters.AddWithValue(collectionId);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(WaitServerId);
        command.Parameters.AddWithValue(WaitServerName);
        command.Parameters.AddWithValue(waitType);
        command.Parameters.AddWithValue(deltaWaitTimeMs);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        DateTime.SpecifyKind(new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond)), DateTimeKind.Unspecified);

    private static async Task DeleteCpuRowsAsync(NpgsqlConnection connection)
    {
        using var cleanup = new NpgsqlCommand(
            $"DELETE FROM cpu_utilization_stats WHERE server_id = {CpuServerId};", connection);
        await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task DeleteWaitRowsAsync(NpgsqlConnection connection)
    {
        using var cleanup = new NpgsqlCommand(
            $"DELETE FROM wait_stats WHERE server_id = {WaitServerId};", connection);
        await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
