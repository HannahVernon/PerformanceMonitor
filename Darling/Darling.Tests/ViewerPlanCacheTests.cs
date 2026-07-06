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
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the Plan Cache sub-tab's two reads against the Darling store contract (no live Postgres): the
/// size trend (single-use vs multi-use MB, summed across every cacheobjtype/objtype group per collection,
/// the Dashboard's single-use bloat shape) and the latest-snapshot composition read (the most recent
/// collection in the window, per group, top by size). Both run on the <c>v_plan_cache_stats</c>
/// passthrough view.
/// </summary>
public sealed class ViewerPlanCacheSqlTests
{
    [Fact]
    public void PlanCacheTrendSql_SumsSingleVsMultiUseMb_PerCollection_OverTheWindow()
    {
        var sql = ViewerDataService.PlanCacheTrendSql;

        Assert.Contains("FROM v_plan_cache_stats", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(SUM(single_use_size_mb) AS double precision)", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(SUM(multi_use_size_mb) AS double precision)", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", sql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", sql, StringComparison.Ordinal);
        Assert.Contains("collection_time <= $3", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY collection_time", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY collection_time", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanCacheSnapshotSql_LatestCollection_Composition_OrderedBySize_CapAt30()
    {
        var sql = ViewerDataService.PlanCacheSnapshotSql;

        Assert.Contains("FROM v_plan_cache_stats", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT MAX(collection_time) AS mx", sql, StringComparison.Ordinal);
        Assert.Contains("collection_time = (SELECT mx FROM latest)", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY total_size_mb DESC, total_plans DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 30", sql, StringComparison.Ordinal);

        /* The composition + stability columns the grid + summary render. */
        foreach (var column in new[]
        {
            "cacheobjtype", "objtype", "total_plans", "single_use_plans", "single_use_size_mb",
            "multi_use_plans", "multi_use_size_mb", "avg_use_count", "avg_size_kb", "oldest_plan_create_time",
        })
        {
            Assert.Contains(column, sql, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("trend")]
    [InlineData("snapshot")]
    public void PlanCacheSql_PgDialect_PositionalParams_NoBareNow_NoNLiterals(string which)
    {
        var sql = which == "trend" ? ViewerDataService.PlanCacheTrendSql : ViewerDataService.PlanCacheSnapshotSql;

        Assert.DoesNotContain("now(", sql.ToLowerInvariant());
        Assert.DoesNotContain("N'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
        Assert.Contains("$1", sql, StringComparison.Ordinal);
        Assert.Contains("$2", sql, StringComparison.Ordinal);
        Assert.Contains("$3", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PlanCacheSql_ReadsColumnsThatExistInTheGeneratedTable()
    {
        Assert.Equal("plan_cache_stats", PlanCacheStatsCollector.Instance.TargetTable);

        var ddl = PgSchemaGenerator.CreateTable(PlanCacheStatsCollector.Instance);
        foreach (var column in new[]
        {
            "collection_time", "cacheobjtype", "objtype", "total_plans", "total_size_mb",
            "single_use_plans", "single_use_size_mb", "multi_use_plans", "multi_use_size_mb",
            "avg_use_count", "avg_size_kb", "oldest_plan_create_time",
        })
        {
            Assert.Contains(column, ddl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PlanCacheView_IsPinnedInTheAuthoritativeViewList()
        => Assert.Contains("v_plan_cache_stats", PgSchemaGenerator.AllPassthroughViews);
}

/// <summary>
/// Gated (DARLING_TEST_PG) live round-trips for the Plan Cache reads: the trend SUMs single/multi-use MB
/// across the per-collection groups, and the snapshot takes the latest collection's composition ordered by
/// size (with oldest_plan_create_time round-tripped for the summary). Shares the serialized "live-postgres"
/// collection; uses a negative sentinel server_id and cleans up in finally.
/// </summary>
[Collection("live-postgres")]
public sealed class ViewerPlanCacheLivePostgresTests
{
    private const int ServerId = -940004;
    private const string ServerName = "viewer-plancache-e2e";

    [Fact]
    public async Task Trend_SumsAcrossGroups_AndSnapshot_TakesLatestCompositionBySize_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live plan-cache test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteAsync(connection, ServerId);

        await using var viewer = new ViewerDataService(connectionString!);

        try
        {
            var t1 = TruncateToSeconds(DateTime.UtcNow.AddMinutes(-10));
            var oldest = t1.AddDays(-2);

            /* Two groups at the same collection: single-use MB 40 + 10 = 50; multi-use 100 + 200 = 300. */
            await InsertAsync(connection, t1, "Compiled Plan", "Proc",
                totalPlans: 5, totalSizeMb: 140, singlePlans: 1, singleMb: 40, multiPlans: 4, multiMb: 100, oldest: oldest);
            await InsertAsync(connection, t1, "Compiled Plan", "Adhoc",
                totalPlans: 30, totalSizeMb: 210, singlePlans: 25, singleMb: 10, multiPlans: 5, multiMb: 200, oldest: oldest);

            var trend = await viewer.GetPlanCacheTrendAsync(ServerId, t1.AddMinutes(-1), t1.AddMinutes(1));
            Assert.Single(trend);
            Assert.Equal(50.0, trend[0].SingleUseSizeMb, precision: 3);
            Assert.Equal(300.0, trend[0].MultiUseSizeMb, precision: 3);

            var snapshot = await viewer.GetPlanCacheSnapshotAsync(ServerId, t1.AddMinutes(-1), t1.AddMinutes(1));
            Assert.Equal(2, snapshot.Count);
            /* Ordered by total_size_mb DESC → the Adhoc group (210) first. */
            Assert.Equal("Adhoc", snapshot[0].Objtype);
            Assert.Equal(210, snapshot[0].TotalSizeMb);
            Assert.Equal(oldest.Ticks, snapshot[0].OldestPlanCreateTime!.Value.Ticks);
        }
        finally
        {
            await DeleteAsync(connection, ServerId);
        }
    }

    private static async Task InsertAsync(
        NpgsqlConnection connection, DateTime collectionTimeUtc, string cacheobjtype, string objtype,
        int totalPlans, int totalSizeMb, int singlePlans, int singleMb, int multiPlans, int multiMb, DateTime oldest)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO plan_cache_stats
    (collection_id, collection_time, server_id, server_name, cacheobjtype, objtype,
     total_plans, total_size_mb, single_use_plans, single_use_size_mb,
     multi_use_plans, multi_use_size_mb, avg_use_count, avg_size_kb, oldest_plan_create_time)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, 1.5, 64, $13)", connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(ServerId);
        command.Parameters.AddWithValue(ServerName);
        command.Parameters.AddWithValue(cacheobjtype);
        command.Parameters.AddWithValue(objtype);
        command.Parameters.AddWithValue(totalPlans);
        command.Parameters.AddWithValue(totalSizeMb);
        command.Parameters.AddWithValue(singlePlans);
        command.Parameters.AddWithValue(singleMb);
        command.Parameters.AddWithValue(multiPlans);
        command.Parameters.AddWithValue(multiMb);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(oldest, DateTimeKind.Unspecified));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        DateTime.SpecifyKind(new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond)), DateTimeKind.Unspecified);

    private static async Task DeleteAsync(NpgsqlConnection connection, int serverId)
    {
        using var cleanup = new NpgsqlCommand($"DELETE FROM plan_cache_stats WHERE server_id = {serverId};", connection);
        await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
