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
/// Pins the CPU Scheduler sub-tab's two reads against the Darling store contract (no live Postgres): the
/// pressure trend (runnable / blocked / queued task counts per collection, a point-in-time collector so
/// no delta math) and the latest-snapshot read (the single most recent cpu_scheduler_stats row in the
/// window). Both run on the <c>v_cpu_scheduler_stats</c> passthrough view.
/// </summary>
public sealed class ViewerCpuSchedulerSqlTests
{
    [Fact]
    public void CpuSchedulerTrendSql_SelectsPressureCounts_OverTheWindow_OrderedByTime()
    {
        var sql = ViewerDataService.CpuSchedulerTrendSql;

        Assert.Contains("FROM v_cpu_scheduler_stats", sql, StringComparison.Ordinal);
        Assert.Contains("total_runnable_tasks_count", sql, StringComparison.Ordinal);
        Assert.Contains("total_blocked_task_count", sql, StringComparison.Ordinal);
        Assert.Contains("total_queued_request_count", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", sql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", sql, StringComparison.Ordinal);
        Assert.Contains("collection_time <= $3", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY collection_time", sql, StringComparison.Ordinal);

        /* A point-in-time snapshot collector: the counts plot directly — no averaging, no delta. */
        Assert.DoesNotContain("AVG(", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("LAG(", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CpuSchedulerSnapshotSql_LatestRowInWindow_AllPressureColumns()
    {
        var sql = ViewerDataService.CpuSchedulerSnapshotSql;

        Assert.Contains("FROM v_cpu_scheduler_stats", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY collection_time DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 1", sql, StringComparison.Ordinal);

        /* The warning flags + NUMA / OS-memory columns the metric grid renders. */
        foreach (var column in new[]
        {
            "worker_thread_exhaustion_warning", "runnable_tasks_warning", "blocked_tasks_warning",
            "queued_requests_warning", "physical_memory_pressure_warning", "offline_cpu_warning",
            "runnable_percent", "system_memory_state_desc", "total_node_count",
        })
        {
            Assert.Contains(column, sql, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("trend")]
    [InlineData("snapshot")]
    public void CpuSchedulerSql_PgDialect_PositionalParams_NoBareNow_NoNLiterals(string which)
    {
        var sql = which == "trend" ? ViewerDataService.CpuSchedulerTrendSql : ViewerDataService.CpuSchedulerSnapshotSql;

        Assert.DoesNotContain("now(", sql.ToLowerInvariant());
        Assert.DoesNotContain("N'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
        Assert.Contains("$1", sql, StringComparison.Ordinal);
        Assert.Contains("$2", sql, StringComparison.Ordinal);
        Assert.Contains("$3", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CpuSchedulerSql_ReadsColumnsThatExistInTheGeneratedTable()
    {
        Assert.Equal("cpu_scheduler_stats", CpuSchedulerStatsCollector.Instance.TargetTable);

        var ddl = PgSchemaGenerator.CreateTable(CpuSchedulerStatsCollector.Instance);
        foreach (var column in new[]
        {
            "collection_time", "total_runnable_tasks_count", "total_blocked_task_count",
            "total_queued_request_count", "max_workers_count", "total_current_workers_count",
            "runnable_percent", "worker_thread_exhaustion_warning", "system_memory_state_desc",
            "total_physical_memory_kb", "nodes_online_count",
        })
        {
            Assert.Contains(column, ddl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CpuSchedulerView_IsPinnedInTheAuthoritativeViewList()
        => Assert.Contains("v_cpu_scheduler_stats", PgSchemaGenerator.AllPassthroughViews);
}

/// <summary>
/// Pins the CPU Scheduler latest-snapshot metric-grid projection (<see
/// cref="ViewerServerTab.BuildCpuSchedulerMetrics"/>): the collector's pressure flags land on the right
/// labelled rows (driving the grid highlight), worker-utilization is current/max*100, nullable request
/// columns degrade to "N/A", and a null snapshot yields an empty grid.
/// </summary>
public sealed class ViewerCpuSchedulerProjectionTests
{
    private static CpuSchedulerSnapshot Snapshot(
        bool workerExhaustion = false, bool runnableWarn = false, bool blockedWarn = false,
        bool queuedWarn = false, bool memPressure = false, bool offlineWarn = false,
        int maxWorkers = 512, int currentWorkers = 128,
        int? runnableRequests = 3, decimal? runnablePercent = 12.5m)
        => new(
            CollectionTime: new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Unspecified),
            MaxWorkersCount: maxWorkers,
            SchedulerCount: 8,
            CpuCount: 8,
            TotalRunnableTasksCount: 4,
            TotalWorkQueueCount: 0,
            TotalCurrentWorkersCount: currentWorkers,
            AvgRunnableTasksCount: 0.5m,
            TotalActiveRequestCount: 2,
            TotalQueuedRequestCount: 0,
            TotalBlockedTaskCount: 0,
            TotalActiveParallelThreadCount: 0,
            RunnableRequestCount: runnableRequests,
            TotalRequestCount: runnableRequests.HasValue ? 20 : (int?)null,
            RunnablePercent: runnablePercent,
            WorkerThreadExhaustionWarning: workerExhaustion,
            RunnableTasksWarning: runnableWarn,
            BlockedTasksWarning: blockedWarn,
            QueuedRequestsWarning: queuedWarn,
            TotalPhysicalMemoryKb: 67_108_864, // 64 GB
            AvailablePhysicalMemoryKb: 8_388_608, // 8 GB
            SystemMemoryStateDesc: "Available physical memory is high",
            PhysicalMemoryPressureWarning: memPressure,
            TotalNodeCount: 2,
            NodesOnlineCount: 2,
            OfflineCpuCount: 0,
            OfflineCpuWarning: offlineWarn);

    [Fact]
    public void BuildMetrics_NullSnapshot_YieldsEmptyGrid()
        => Assert.Empty(ViewerServerTab.BuildCpuSchedulerMetrics(null));

    [Fact]
    public void BuildMetrics_WarningFlags_HighlightTheirOwnRows()
    {
        var rows = ViewerServerTab.BuildCpuSchedulerMetrics(
            Snapshot(runnableWarn: true, blockedWarn: true, memPressure: true));

        Assert.True(rows.Single(r => r.Metric == "Runnable Tasks").IsWarning);
        Assert.True(rows.Single(r => r.Metric == "Blocked Tasks").IsWarning);
        Assert.True(rows.Single(r => r.Metric == "Physical Memory Pressure").IsWarning);
        Assert.True(rows.Single(r => r.Metric == "System Memory State").IsWarning);

        /* A metric whose flag is off must NOT highlight. */
        Assert.False(rows.Single(r => r.Metric == "Queued Requests").IsWarning);
        Assert.False(rows.Single(r => r.Metric == "Offline CPUs").IsWarning);
    }

    [Fact]
    public void BuildMetrics_WorkerUtilization_IsCurrentOverMaxTimes100()
    {
        var rows = ViewerServerTab.BuildCpuSchedulerMetrics(Snapshot(maxWorkers: 400, currentWorkers: 100));

        /* 100 / 400 * 100 = 25.0 %. */
        Assert.Equal("25.0", rows.Single(r => r.Metric == "Worker Utilization %").Value);
    }

    [Fact]
    public void BuildMetrics_WorkerUtilization_ZeroMaxWorkers_DoesNotDivideByZero()
    {
        var rows = ViewerServerTab.BuildCpuSchedulerMetrics(Snapshot(maxWorkers: 0, currentWorkers: 0));

        Assert.Equal("0.0", rows.Single(r => r.Metric == "Worker Utilization %").Value);
    }

    [Fact]
    public void BuildMetrics_NullableRequestColumns_DegradeToNa()
    {
        var rows = ViewerServerTab.BuildCpuSchedulerMetrics(Snapshot(runnableRequests: null, runnablePercent: null));

        Assert.Equal("N/A", rows.Single(r => r.Metric == "Runnable Requests").Value);
        Assert.Equal("N/A", rows.Single(r => r.Metric == "Total Requests").Value);
        Assert.Equal("N/A", rows.Single(r => r.Metric == "Runnable %").Value);
    }
}

/// <summary>
/// Gated (DARLING_TEST_PG) live round-trips for the CPU Scheduler reads: the pressure trend order and the
/// latest-snapshot selection (newest row in the window, all pressure columns round-tripped). Shares the
/// serialized "live-postgres" collection; uses a negative sentinel server_id and cleans up in finally.
/// </summary>
[Collection("live-postgres")]
public sealed class ViewerCpuSchedulerLivePostgresTests
{
    private const int ServerId = -940003;
    private const string ServerName = "viewer-cpusched-e2e";

    [Fact]
    public async Task Trend_OrdersByCollectionTime_AndSnapshot_TakesNewestRow_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live cpu-scheduler test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteAsync(connection, ServerId);

        await using var viewer = new ViewerDataService(connectionString!);

        try
        {
            var t1 = TruncateToSeconds(DateTime.UtcNow.AddMinutes(-10));
            var t2 = t1.AddMinutes(5);

            await InsertAsync(connection, 1, t1, runnable: 2, blocked: 0, queued: 0, workerWarn: false);
            await InsertAsync(connection, 2, t2, runnable: 9, blocked: 4, queued: 1, workerWarn: true);

            var trend = await viewer.GetCpuSchedulerTrendAsync(ServerId, t1.AddMinutes(-1), t2.AddMinutes(1));
            Assert.Equal(2, trend.Count);
            Assert.Equal(new[] { t1.Ticks, t2.Ticks }, trend.Select(p => p.CollectionTime.Ticks));
            Assert.Equal(new[] { 2, 9 }, trend.Select(p => p.RunnableTasks));
            Assert.Equal(new[] { 0, 4 }, trend.Select(p => p.BlockedTasks));

            /* Snapshot = the newest row (t2), warning flag round-trips. */
            var snapshot = await viewer.GetCpuSchedulerSnapshotAsync(ServerId, t1.AddMinutes(-1), t2.AddMinutes(1));
            Assert.NotNull(snapshot);
            Assert.Equal(t2.Ticks, snapshot!.CollectionTime.Ticks);
            Assert.Equal(9, snapshot.TotalRunnableTasksCount);
            Assert.True(snapshot.WorkerThreadExhaustionWarning);
        }
        finally
        {
            await DeleteAsync(connection, ServerId);
        }
    }

    private static async Task InsertAsync(
        NpgsqlConnection connection, long collectionId, DateTime collectionTimeUtc,
        int runnable, int blocked, int queued, bool workerWarn)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO cpu_scheduler_stats
    (collection_id, collection_time, server_id, server_name,
     max_workers_count, scheduler_count, cpu_count,
     total_runnable_tasks_count, total_work_queue_count, total_current_workers_count,
     avg_runnable_tasks_count, total_active_request_count, total_queued_request_count,
     total_blocked_task_count, total_active_parallel_thread_count,
     runnable_request_count, total_request_count, runnable_percent,
     worker_thread_exhaustion_warning, runnable_tasks_warning, blocked_tasks_warning,
     queued_requests_warning, total_physical_memory_kb, available_physical_memory_kb,
     system_memory_state_desc, physical_memory_pressure_warning,
     total_node_count, nodes_online_count, offline_cpu_count, offline_cpu_warning)
VALUES ($1, $2, $3, $4, 512, 8, 8, $5, 0, 128, 0.5, 2, $6, $7, 0, 3, 20, 12.5,
        $8, false, false, false, 67108864, 8388608, 'ok', false, 2, 2, 0, false)", connection);
        command.Parameters.AddWithValue(collectionId);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(ServerId);
        command.Parameters.AddWithValue(ServerName);
        command.Parameters.AddWithValue(runnable);
        command.Parameters.AddWithValue(queued);
        command.Parameters.AddWithValue(blocked);
        command.Parameters.AddWithValue(workerWarn);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        DateTime.SpecifyKind(new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond)), DateTimeKind.Unspecified);

    private static async Task DeleteAsync(NpgsqlConnection connection, int serverId)
    {
        using var cleanup = new NpgsqlCommand($"DELETE FROM cpu_scheduler_stats WHERE server_id = {serverId};", connection);
        await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
