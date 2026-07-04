/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
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
/// Pins the Overview server-cards reads (W2a viewer copy-parity, copied from Lite's
/// LocalDataService.Overview.cs) against the Darling store contract — no live Postgres. The five
/// per-server summary reads run over the same v_* passthrough views the other tabs read; these pins are
/// the load-bearing clauses (source view, per-server filter, one-hour window on the event/deadlock time,
/// XE→DMV blocking fallback, newest-first LIMIT 1) plus the Pg dialect.
/// </summary>
public sealed class ViewerOverviewSqlTests
{
    [Fact]
    public void SummaryCpuSql_ReadsLatestSample_FromCpuView()
    {
        var sql = ViewerDataService.ServerSummaryCpuSql;
        Assert.Contains("FROM v_cpu_utilization_stats", sql, StringComparison.Ordinal);
        Assert.Contains("sqlserver_cpu_utilization", sql, StringComparison.Ordinal);
        Assert.Contains("other_process_cpu_utilization", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY sample_time DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 1", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryMemorySql_ReadsLatestTotalServerMemory_CastToDouble()
    {
        var sql = ViewerDataService.ServerSummaryMemorySql;
        Assert.Contains("FROM v_memory_stats", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(total_server_memory_mb AS double precision)", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY collection_time DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 1", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryBlockingSql_XeCount_FallsBackToDmvSnapshot_OverTheWindow()
    {
        var sql = ViewerDataService.ServerSummaryBlockingSql;
        /* COALESCE(NULLIF(xe,0), dmv) — Lite's fallback shape. */
        Assert.Contains("COALESCE(NULLIF(", sql, StringComparison.Ordinal);
        Assert.Contains("FROM v_blocked_process_reports", sql, StringComparison.Ordinal);
        Assert.Contains("FROM v_dmv_blocking_snapshots", sql, StringComparison.Ordinal);
        Assert.Contains("event_time >= $2", sql, StringComparison.Ordinal);
        Assert.Contains("server_id = $1", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryDeadlockSql_CountsOverTheWindow_OnDeadlockTime()
    {
        var sql = ViewerDataService.ServerSummaryDeadlockSql;
        Assert.Contains("FROM v_deadlocks", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", sql, StringComparison.Ordinal);
        Assert.Contains("deadlock_time >= $2", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryLastCollectionSql_TakesTheNewestCollectionTime()
    {
        var sql = ViewerDataService.ServerSummaryLastCollectionSql;
        Assert.Contains("MAX(collection_time)", sql, StringComparison.Ordinal);
        Assert.Contains("FROM v_collection_log", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SummaryReads_ArePgDialect_PositionalParams_NoBareNow_NoNLiterals()
    {
        foreach (var sql in new[]
        {
            ViewerDataService.ServerSummaryCpuSql,
            ViewerDataService.ServerSummaryMemorySql,
            ViewerDataService.ServerSummaryBlockingSql,
            ViewerDataService.ServerSummaryDeadlockSql,
            ViewerDataService.ServerSummaryLastCollectionSql,
        })
        {
            Assert.DoesNotContain("now(", sql.ToLowerInvariant());
            Assert.DoesNotContain("N'", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
            Assert.Contains("$1", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SummaryReads_ReferenceColumnsThatExistInTheGeneratedSourceTables()
    {
        var cpu = PgSchemaGenerator.CreateTable(CpuUtilizationCollector.Instance);
        Assert.Contains("sqlserver_cpu_utilization", cpu, StringComparison.Ordinal);
        Assert.Contains("other_process_cpu_utilization", cpu, StringComparison.Ordinal);
        Assert.Contains("sample_time", cpu, StringComparison.Ordinal);

        Assert.Contains("total_server_memory_mb", PgSchemaGenerator.CreateTable(MemoryStatsCollector.Instance), StringComparison.Ordinal);
        Assert.Contains("event_time", PgSchemaGenerator.CreateTable(BlockedProcessReportCollector.Instance), StringComparison.Ordinal);
        Assert.Contains("event_time", PgSchemaGenerator.CreateTable(DmvBlockingSnapshotCollector.Instance), StringComparison.Ordinal);
        Assert.Contains("deadlock_time", PgSchemaGenerator.CreateTable(DeadlocksCollector.Instance), StringComparison.Ordinal);
    }

    [Fact]
    public void PassthroughViews_ForEverySummarySource_ExistInTheMigrations()
    {
        var allMigrationSql = string.Concat(PgMigrations.Scripts.Select(m => m.Sql));
        foreach (var view in new[]
        {
            "v_cpu_utilization_stats", "v_memory_stats", "v_blocked_process_reports",
            "v_dmv_blocking_snapshots", "v_deadlocks", "v_collection_log",
        })
        {
            Assert.Contains(view, allMigrationSql, StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// The Overview card view-model's pure display + the viewer's freshness-derived status (#1262). No
/// Postgres: <see cref="ServerSummaryItem.ClassifyFreshness"/> is pure over (last-collection, now), and
/// every display property is a pure format. Verifies the one semantic change — status from collection
/// freshness, not a live ping — maps onto Lite's Online / Warning / Offline card states.
/// </summary>
public sealed class ViewerServerSummaryDisplayTests
{
    private static readonly DateTime Now = new(2026, 7, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ClassifyFreshness_NoCollection_IsOffline()
    {
        Assert.Equal(ServerFreshness.Offline, ServerSummaryItem.ClassifyFreshness(null, Now));
    }

    [Theory]
    [InlineData(0, ServerFreshness.Fresh)]      // just collected
    [InlineData(30, ServerFreshness.Fresh)]     // within 2x cadence
    [InlineData(120, ServerFreshness.Fresh)]    // exactly 2x cadence — still fresh
    [InlineData(121, ServerFreshness.Stale)]    // just past 2x cadence
    [InlineData(300, ServerFreshness.Stale)]    // 5 min — stale
    [InlineData(900, ServerFreshness.Stale)]    // exactly 15 min — still stale
    [InlineData(901, ServerFreshness.Offline)]  // just past 15 min
    [InlineData(1200, ServerFreshness.Offline)] // 20 min — offline
    public void ClassifyFreshness_BandsByAge(int ageSeconds, ServerFreshness expected)
    {
        var lastCollection = Now.AddSeconds(-ageSeconds);
        Assert.Equal(expected, ServerSummaryItem.ClassifyFreshness(lastCollection, Now));
    }

    [Fact]
    public void ApplyFreshness_Fresh_IsOnline()
    {
        var item = new ServerSummaryItem { LastCollectionTime = Now.AddSeconds(-30) };
        item.ApplyFreshness(Now);
        Assert.True(item.IsOnline);
        Assert.False(item.HasCollectorErrors);
        Assert.False(item.IsOffline);
        Assert.Equal("Online", item.StatusDisplay);
    }

    [Fact]
    public void ApplyFreshness_Stale_IsWarning()
    {
        var item = new ServerSummaryItem { LastCollectionTime = Now.AddMinutes(-5) };
        item.ApplyFreshness(Now);
        Assert.True(item.IsOnline);
        Assert.True(item.HasCollectorErrors);
        Assert.False(item.IsOffline);
        Assert.Equal("Warning", item.StatusDisplay);
    }

    [Fact]
    public void ApplyFreshness_Offline_ShowsOverlay()
    {
        var offlineByAge = new ServerSummaryItem { LastCollectionTime = Now.AddMinutes(-20) };
        offlineByAge.ApplyFreshness(Now);
        Assert.False(offlineByAge.IsOnline);
        Assert.True(offlineByAge.IsOffline);
        Assert.Equal("Offline", offlineByAge.StatusDisplay);

        var offlineByAbsence = new ServerSummaryItem { LastCollectionTime = null };
        offlineByAbsence.ApplyFreshness(Now);
        Assert.True(offlineByAbsence.IsOffline);
        Assert.Equal("Offline", offlineByAbsence.StatusDisplay);
    }

    [Theory]
    [InlineData(60.0, null, "60%")]                    // SQL-only when other-process is unavailable
    [InlineData(60.0, 15.0, "75% (SQL 60%)")]          // total prominently, SQL alongside
    public void CpuDisplay_ShowsTotalWithSqlAlongside(double sqlCpu, double? otherCpu, string expected)
    {
        var item = new ServerSummaryItem { CpuPercent = sqlCpu, OtherProcessCpuPercent = otherCpu };
        Assert.Equal(expected, item.CpuDisplay);
    }

    [Fact]
    public void CpuDisplay_NoData_IsDashes()
    {
        Assert.Equal("--", new ServerSummaryItem().CpuDisplay);
    }

    [Fact]
    public void CpuPercentForAlert_IsAlwaysTotal_TheViewerHasNoCpuAlertMode()
    {
        var item = new ServerSummaryItem { CpuPercent = 60, OtherProcessCpuPercent = 25 };
        Assert.Equal(85, item.CpuPercentForAlert);
    }

    [Theory]
    [InlineData(2048.0, "2.0 GB")]
    [InlineData(1536.0, "1.5 GB")]
    public void MemoryDisplay_FormatsGb(double memoryMb, string expected)
    {
        Assert.Equal(expected, new ServerSummaryItem { MemoryMb = memoryMb }.MemoryDisplay);
    }

    [Fact]
    public void MemoryDisplay_NoData_IsDashes()
    {
        Assert.Equal("--", new ServerSummaryItem().MemoryDisplay);
    }

    [Fact]
    public void LastCollectionDisplay_TreatsStoredValueAsUtc_ShownLocal()
    {
        var storedUtc = new DateTime(2026, 7, 3, 3, 30, 45, DateTimeKind.Unspecified);
        var expected = DateTime.SpecifyKind(storedUtc, DateTimeKind.Utc).ToLocalTime().ToString("HH:mm:ss");
        Assert.Equal(expected, new ServerSummaryItem { LastCollectionTime = storedUtc }.LastCollectionDisplay);
    }

    [Fact]
    public void LastCollectionDisplay_NoData_IsNever()
    {
        Assert.Equal("Never", new ServerSummaryItem().LastCollectionDisplay);
    }

    [Fact]
    public void CountDisplays_MirrorLite()
    {
        Assert.Equal("0", new ServerSummaryItem { BlockingCount = 0 }.BlockingDisplay);
        Assert.Equal("3", new ServerSummaryItem { BlockingCount = 3 }.BlockingDisplay);
        Assert.Equal("0", new ServerSummaryItem { DeadlockCount = 0 }.DeadlockDisplay);
        Assert.Equal("2", new ServerSummaryItem { DeadlockCount = 2 }.DeadlockDisplay);
        Assert.True(new ServerSummaryItem { DeadlockCount = 1 }.HasAlerts);
        Assert.False(new ServerSummaryItem().HasAlerts);
    }

    [Fact]
    public void CardBorderBrush_RedForDeadlock_DefaultOtherwise()
    {
        var deadlocked = new ServerSummaryItem { DeadlockCount = 1, LastCollectionTime = Now };
        deadlocked.ApplyFreshness(Now);
        Assert.Equal("#FFE57373", deadlocked.CardBorderBrush.Color.ToString());

        var calm = new ServerSummaryItem { LastCollectionTime = Now };
        calm.ApplyFreshness(Now);
        Assert.Equal("#FF2A2D35", calm.CardBorderBrush.Color.ToString());
    }
}

/// <summary>
/// Pins the Alert History parity SQL (W2a): the all-servers read shape, the per-server read carrying the
/// Server column's columns, and the dismiss write path (set-based UPDATE keyed on
/// (alert_time, server_id, metric_name)). No live Postgres.
/// </summary>
public sealed class ViewerAlertHistoryW2aSqlTests
{
    [Fact]
    public void AllServersSql_ReadsConfigAlertLog_NoServerFilter_NewestFirst_ExcludesDismissed()
    {
        var sql = ViewerDataService.AlertHistoryAllServersSql;
        Assert.Contains("FROM config_alert_log", sql, StringComparison.Ordinal);
        Assert.Contains("WHERE alert_time >= $1", sql, StringComparison.Ordinal);
        Assert.Contains("dismissed = FALSE", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY alert_time DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $2", sql, StringComparison.Ordinal);
        /* No per-server predicate in the all-servers read (that's the per-server read's $2). */
        Assert.DoesNotContain("server_id = $", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BothReads_CarryServerColumns_ForTheServerColumnAndDismissKey()
    {
        foreach (var sql in new[] { ViewerDataService.AlertHistorySql, ViewerDataService.AlertHistoryAllServersSql })
        {
            Assert.Contains("server_id", sql, StringComparison.Ordinal);
            Assert.Contains("server_name", sql, StringComparison.Ordinal);
            Assert.Contains("metric_name", sql, StringComparison.Ordinal);
            Assert.Contains("context_json", sql, StringComparison.Ordinal);
        }

        /* Per-server read keeps its scoping predicate + limit. */
        Assert.Contains("server_id = $2", ViewerDataService.AlertHistorySql, StringComparison.Ordinal);
        Assert.Contains("LIMIT $3", ViewerDataService.AlertHistorySql, StringComparison.Ordinal);
    }

    [Fact]
    public void DismissAlertsSql_SetBasedUpdate_KeyedOnTheAlertTuple_GuardsAlreadyDismissed()
    {
        var sql = ViewerDataService.DismissAlertsSql;
        Assert.Contains("UPDATE config_alert_log", sql, StringComparison.Ordinal);
        Assert.Contains("SET    dismissed = TRUE", sql, StringComparison.Ordinal);
        Assert.Contains("dismissed = FALSE", sql, StringComparison.Ordinal);
        Assert.Contains("(alert_time, server_id, metric_name) IN (", sql, StringComparison.Ordinal);
        /* Batch dismiss via unnested arrays — one round-trip for the whole selection. */
        Assert.Contains("unnest($1::timestamp[], $2::integer[], $3::text[])", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DismissAllSql_WindowsThenScopes_GuardsAlreadyDismissed()
    {
        Assert.Contains("UPDATE config_alert_log", ViewerDataService.DismissAllAlertsSql, StringComparison.Ordinal);
        Assert.Contains("alert_time >= $1", ViewerDataService.DismissAllAlertsSql, StringComparison.Ordinal);
        Assert.Contains("dismissed = FALSE", ViewerDataService.DismissAllAlertsSql, StringComparison.Ordinal);
        Assert.DoesNotContain("server_id", ViewerDataService.DismissAllAlertsSql, StringComparison.Ordinal);

        Assert.Contains("alert_time >= $1", ViewerDataService.DismissAllAlertsForServerSql, StringComparison.Ordinal);
        Assert.Contains("server_id = $2", ViewerDataService.DismissAllAlertsForServerSql, StringComparison.Ordinal);
        Assert.Contains("dismissed = FALSE", ViewerDataService.DismissAllAlertsForServerSql, StringComparison.Ordinal);
    }

    [Fact]
    public void AllW2aAlertSql_ArePgDialect_NoBareNow_NoNLiterals()
    {
        foreach (var sql in new[]
        {
            ViewerDataService.AlertHistoryAllServersSql,
            ViewerDataService.DismissAlertsSql,
            ViewerDataService.DismissAllAlertsSql,
            ViewerDataService.DismissAllAlertsForServerSql,
        })
        {
            Assert.DoesNotContain("now(", sql.ToLowerInvariant());
            Assert.DoesNotContain("N'", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
            Assert.Contains("$1", sql, StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// Gated (DARLING_TEST_PG) live round-trips for W2a: the Overview server-summary read against planted
/// metric rows (including the XE→DMV blocking fallback and the freshness-derived status), and the alert
/// dismiss write path (dismissed rows drop out of the default read; the all-servers read spans servers).
/// Shares the serialized "live-postgres" collection; negative sentinel server_ids, cleaned up in finally.
/// </summary>
[Collection("live-postgres")]
public sealed class ViewerW2aLivePostgresTests
{
    private const int SummaryServerId = -929201;
    private const int FallbackServerId = -929202;
    private const int DismissServerA = -929203;
    private const int DismissServerB = -929204;
    private const string ServerName = "viewer-w2a-e2e";

    [Fact]
    public async Task ServerSummary_ReadsPlantedMetrics_AndDerivesOnlineFromFreshCollection_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live server-summary test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteSummaryRowsAsync(connection, SummaryServerId);

        await using var viewer = new ViewerDataService(connectionString!);

        try
        {
            var now = TruncateToSeconds(DateTime.UtcNow);
            var withinHour = now.AddMinutes(-20);

            /* Two CPU samples; the newest (sample_time later) wins. */
            await InsertCpuAsync(connection, SummaryServerId, now.AddMinutes(-2), sampleTime: now.AddMinutes(-2), sqlCpu: 10, otherCpu: 2);
            await InsertCpuAsync(connection, SummaryServerId, now.AddMinutes(-1), sampleTime: now.AddMinutes(-1), sqlCpu: 42, otherCpu: 8);

            /* Two memory rows; the newest collection_time wins. */
            await InsertMemoryAsync(connection, SummaryServerId, now.AddMinutes(-2), totalServerMemoryMb: 4096.00m);
            await InsertMemoryAsync(connection, SummaryServerId, now.AddMinutes(-1), totalServerMemoryMb: 8192.00m);

            /* One blocked-process report + one deadlock inside the one-hour window. */
            await InsertBlockedProcessAsync(connection, SummaryServerId, collectionTime: withinHour, eventTime: withinHour);
            await InsertDeadlockAsync(connection, SummaryServerId, collectionTime: withinHour, deadlockTime: withinHour);

            /* A fresh collection_log row → freshness = Online. */
            await InsertCollectionLogAsync(connection, SummaryServerId, "cpu_utilization", now, "SUCCESS");

            var summary = await viewer.GetServerSummaryAsync(SummaryServerId, "Summary E2E");
            summary.ServerName = ServerName;
            summary.ApplyFreshness(DateTime.UtcNow);

            Assert.Equal(42.0, summary.CpuPercent);
            Assert.Equal(8.0, summary.OtherProcessCpuPercent);
            Assert.Equal(50.0, summary.TotalCpuPercent);
            Assert.Equal(8192.0, summary.MemoryMb!.Value, precision: 1);
            Assert.Equal(1, summary.BlockingCount);          // XE report count
            Assert.Equal(1, summary.DeadlockCount);
            Assert.NotNull(summary.LastCollectionTime);
            Assert.True(summary.IsOnline);                   // fresh collection
            Assert.Equal("Online", summary.StatusDisplay);
        }
        finally
        {
            await DeleteSummaryRowsAsync(connection, SummaryServerId);
        }
    }

    [Fact]
    public async Task ServerSummary_BlockingCount_FallsBackToDmvSnapshot_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live blocking-fallback test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteSummaryRowsAsync(connection, FallbackServerId);

        await using var viewer = new ViewerDataService(connectionString!);

        try
        {
            var now = TruncateToSeconds(DateTime.UtcNow);
            var withinHour = now.AddMinutes(-15);

            /* No XE blocked-process reports; two DMV snapshots → COALESCE(NULLIF(0,0), dmv) = 2. */
            await InsertDmvBlockingAsync(connection, FallbackServerId, collectionTime: withinHour, eventTime: withinHour);
            await InsertDmvBlockingAsync(connection, FallbackServerId, collectionTime: withinHour, eventTime: withinHour);

            var summary = await viewer.GetServerSummaryAsync(FallbackServerId, "Fallback E2E");

            Assert.Equal(2, summary.BlockingCount);
            Assert.Equal(0, summary.DeadlockCount);
        }
        finally
        {
            await DeleteSummaryRowsAsync(connection, FallbackServerId);
        }
    }

    [Fact]
    public async Task Dismiss_HidesRowsFromTheDefaultRead_AllServersSpansServers_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live dismiss test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteAlertRowsAsync(connection);

        await using var viewer = new ViewerDataService(connectionString!);

        try
        {
            var now = TruncateToSeconds(DateTime.UtcNow);
            var since = now.AddHours(-1);

            /* Three visible rows on server A (distinct alert_times so a single dismiss targets one),
               one on server B. */
            await InsertAlertAsync(connection, now, DismissServerA, "server-a", "High CPU");
            await InsertAlertAsync(connection, now.AddMinutes(-1), DismissServerA, "server-a", "Deadlocks Detected");
            await InsertAlertAsync(connection, now.AddMinutes(-2), DismissServerA, "server-a", "Blocking Detected");
            await InsertAlertAsync(connection, now.AddMinutes(-1), DismissServerB, "server-b", "High CPU");

            /* All-servers read spans both servers. */
            var all = await viewer.GetAlertHistoryAsync(since);
            Assert.Equal(4, all.Count(r => r.ServerId is DismissServerA or DismissServerB));
            Assert.Contains(all, r => r.ServerId == DismissServerB);

            /* Per-server read scopes to A. */
            var aRows = await viewer.GetAlertHistoryAsync(since, DismissServerA);
            Assert.Equal(3, aRows.Count);

            /* Dismiss one A row → it drops out; the other two remain. */
            var target = aRows.Single(r => r.MetricName == "Deadlocks Detected");
            var dismissedOne = await viewer.DismissAlertsAsync(new[] { target });
            Assert.Equal(1, dismissedOne);

            var aAfterOne = await viewer.GetAlertHistoryAsync(since, DismissServerA);
            Assert.Equal(2, aAfterOne.Count);
            Assert.DoesNotContain(aAfterOne, r => r.MetricName == "Deadlocks Detected");

            /* Dismissing the same row again is a no-op (the dismissed = FALSE guard). */
            Assert.Equal(0, await viewer.DismissAlertsAsync(new[] { target }));

            /* Dismiss all remaining A rows → A empties, B untouched. */
            var dismissedAll = await viewer.DismissAllVisibleAlertsAsync(since, DismissServerA);
            Assert.Equal(2, dismissedAll);
            Assert.Empty(await viewer.GetAlertHistoryAsync(since, DismissServerA));
            Assert.Single(await viewer.GetAlertHistoryAsync(since, DismissServerB));
        }
        finally
        {
            await DeleteAlertRowsAsync(connection);
        }
    }

    // ── planting helpers ─────────────────────────────────────────────────────────────

    private static async Task InsertCpuAsync(
        NpgsqlConnection connection, int serverId, DateTime collectionTime, DateTime sampleTime, int sqlCpu, int otherCpu)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO cpu_utilization_stats (collection_id, collection_time, server_id, server_name, sample_time, sqlserver_cpu_utilization, other_process_cpu_utilization) VALUES ($1, $2, $3, $4, $5, $6, $7)",
            connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTime, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(ServerName);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(sampleTime, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(sqlCpu);
        command.Parameters.AddWithValue(otherCpu);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertMemoryAsync(
        NpgsqlConnection connection, int serverId, DateTime collectionTime, decimal totalServerMemoryMb)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO memory_stats (collection_id, collection_time, server_id, server_name, total_server_memory_mb) VALUES ($1, $2, $3, $4, $5)",
            connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTime, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(ServerName);
        command.Parameters.AddWithValue(totalServerMemoryMb);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertBlockedProcessAsync(
        NpgsqlConnection connection, int serverId, DateTime collectionTime, DateTime eventTime)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO blocked_process_reports (blocked_report_id, collection_time, server_id, server_name, event_time) VALUES ($1, $2, $3, $4, $5)",
            connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTime, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(ServerName);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(eventTime, DateTimeKind.Unspecified));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertDmvBlockingAsync(
        NpgsqlConnection connection, int serverId, DateTime collectionTime, DateTime eventTime)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO dmv_blocking_snapshots (collection_id, collection_time, server_id, server_name, event_time) VALUES ($1, $2, $3, $4, $5)",
            connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTime, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(ServerName);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(eventTime, DateTimeKind.Unspecified));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertDeadlockAsync(
        NpgsqlConnection connection, int serverId, DateTime collectionTime, DateTime deadlockTime)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO deadlocks (deadlock_id, collection_time, server_id, server_name, deadlock_time) VALUES ($1, $2, $3, $4, $5)",
            connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTime, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(ServerName);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(deadlockTime, DateTimeKind.Unspecified));
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertCollectionLogAsync(
        NpgsqlConnection connection, int serverId, string collectorName, DateTime collectionTime, string status)
    {
        using var command = new NpgsqlCommand(
            "INSERT INTO collection_log (log_id, server_id, server_name, collector_name, collection_time, status) VALUES ($1, $2, $3, $4, $5, $6)",
            connection);
        command.Parameters.AddWithValue(CollectionIdGenerator.Next());
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(ServerName);
        command.Parameters.AddWithValue(collectorName);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTime, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(status);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertAlertAsync(
        NpgsqlConnection connection, DateTime alertTimeUtc, int serverId, string serverName, string metric)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO config_alert_log
    (alert_time, server_id, server_name, metric_name, current_value, threshold_value,
     alert_sent, notification_type, send_error, dismissed, muted, detail_text, context_json)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)", connection);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(alertTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(serverName);
        command.Parameters.AddWithValue(metric);
        command.Parameters.AddWithValue(1.0);
        command.Parameters.AddWithValue(1.0);
        command.Parameters.AddWithValue(false);
        command.Parameters.AddWithValue("tray");
        command.Parameters.Add(new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.AddWithValue(false);
        command.Parameters.AddWithValue(false);
        command.Parameters.Add(new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        DateTime.SpecifyKind(new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond)), DateTimeKind.Unspecified);

    private static async Task DeleteSummaryRowsAsync(NpgsqlConnection connection, int serverId)
    {
        foreach (var table in new[]
        {
            "cpu_utilization_stats", "memory_stats", "blocked_process_reports",
            "dmv_blocking_snapshots", "deadlocks", "collection_log",
        })
        {
            using var cleanup = new NpgsqlCommand($"DELETE FROM {table} WHERE server_id = {serverId};", connection);
            await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task DeleteAlertRowsAsync(NpgsqlConnection connection)
    {
        using var cleanup = new NpgsqlCommand(
            $"DELETE FROM config_alert_log WHERE server_id IN ({DismissServerA}, {DismissServerB});", connection);
        await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
