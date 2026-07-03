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
using PerformanceMonitor.Alerting;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the W1e Blocking-depth reads (widened Blocked Process Reports grid, the block-chain pair-row fetch,
/// the Deadlocks grid, and the two slicer bucket reads) against the Darling store contract, no live Postgres.
/// All mirror Lite's <c>LocalDataService.Blocking.cs</c> queries: the 37-column BPR read (including
/// <c>blocked_process_report_xml</c>) over <c>v_blocked_process_reports</c> with the 19-column DMV fallback;
/// the pair-row read built from the shared <c>PgBlockingPairRowQuery</c> fragments (so the apex + column
/// order can't drift); the deadlock read over <c>v_deadlocks</c>; and the hourly slicer buckets (blocking
/// XE-preferred with the DMV fallback exclusion, deadlocks by collection_time).
/// </summary>
public sealed class ViewerBlockingDepthSqlTests
{
    [Fact]
    public void BlockedProcessReportsSql_FullColumnSet_IncludesReportXml_OverBprView()
    {
        Assert.Contains("FROM v_blocked_process_reports", ViewerDataService.BlockedProcessReportsSql, StringComparison.Ordinal);
        Assert.Contains("blocked_process_report_xml", ViewerDataService.BlockedProcessReportsSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY event_time DESC", ViewerDataService.BlockedProcessReportsSql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 200", ViewerDataService.BlockedProcessReportsSql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", ViewerDataService.BlockedProcessReportsSql, StringComparison.Ordinal);

        /* The widened grid columns Lite's row carries that the old 9-column viewer read dropped. */
        foreach (var column in new[]
        {
            "blocked_ecid", "blocking_ecid", "wait_resource", "blocked_status", "blocking_status",
            "blocked_isolation_level", "blocking_isolation_level", "blocked_log_used",
            "blocked_transaction_count", "blocked_priority", "blocking_priority",
            "blocked_transaction_name", "blocking_transaction_name", "monitor_loop",
        })
        {
            Assert.Contains(column, ViewerDataService.BlockedProcessReportsSql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DmvBlockingSnapshotsSql_FallbackColumns_NoReportXml_OverDmvView()
    {
        Assert.Contains("FROM v_dmv_blocking_snapshots", ViewerDataService.DmvBlockingSnapshotsSql, StringComparison.Ordinal);
        /* dmv_blocking_snapshots has no report XML column — it must not be selected. */
        Assert.DoesNotContain("blocked_process_report_xml", ViewerDataService.DmvBlockingSnapshotsSql, StringComparison.Ordinal);
        Assert.Contains("blocking_status", ViewerDataService.DmvBlockingSnapshotsSql, StringComparison.Ordinal);
        Assert.Contains("monitor_loop", ViewerDataService.DmvBlockingSnapshotsSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY event_time DESC", ViewerDataService.DmvBlockingSnapshotsSql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 200", ViewerDataService.DmvBlockingSnapshotsSql, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockingPairRowsSql_BuiltFromSharedFragments_SpidFilter_EventTimeWindow()
    {
        Assert.Contains("FROM v_blocked_process_reports", ViewerDataService.BlockingPairRowsSql, StringComparison.Ordinal);
        /* The shared PgBlockingPairRowQuery fragments — spid:ecid + monitor_loop are the reconstruction identity. */
        Assert.Contains("blocked_ecid", ViewerDataService.BlockingPairRowsSql, StringComparison.Ordinal);
        Assert.Contains("blocking_ecid", ViewerDataService.BlockingPairRowsSql, StringComparison.Ordinal);
        Assert.Contains("monitor_loop", ViewerDataService.BlockingPairRowsSql, StringComparison.Ordinal);
        /* SpidFilter drops the missing-blocker sentinel so no consumer invents a SPID-0 apex. */
        Assert.Contains("blocking_spid IS NOT NULL", ViewerDataService.BlockingPairRowsSql, StringComparison.Ordinal);
        Assert.Contains("blocking_spid <> 0", ViewerDataService.BlockingPairRowsSql, StringComparison.Ordinal);
        Assert.Contains("event_time >= $2 AND event_time <= $3", ViewerDataService.BlockingPairRowsSql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 5000", ViewerDataService.BlockingPairRowsSql, StringComparison.Ordinal);
    }

    [Fact]
    public void RecentDeadlocksSql_CarriesGraphXml_OrdersByDeadlockTime()
    {
        Assert.Contains("FROM v_deadlocks", ViewerDataService.RecentDeadlocksSql, StringComparison.Ordinal);
        Assert.Contains("deadlock_graph_xml", ViewerDataService.RecentDeadlocksSql, StringComparison.Ordinal);
        Assert.Contains("victim_process_id", ViewerDataService.RecentDeadlocksSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY deadlock_time DESC", ViewerDataService.RecentDeadlocksSql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 50", ViewerDataService.RecentDeadlocksSql, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockingSlicerSql_XePreferred_DmvFallbackOnlyWhenNoXe_HourlyBuckets()
    {
        Assert.Contains("FROM v_blocked_process_reports", ViewerDataService.BlockingSlicerSql, StringComparison.Ordinal);
        Assert.Contains("FROM v_dmv_blocking_snapshots", ViewerDataService.BlockingSlicerSql, StringComparison.Ordinal);
        Assert.Contains("WHERE NOT EXISTS (SELECT 1 FROM bpr)", ViewerDataService.BlockingSlicerSql, StringComparison.Ordinal);
        Assert.Contains("date_trunc('hour', collection_time)", ViewerDataService.BlockingSlicerSql, StringComparison.Ordinal);
        Assert.Contains("COUNT(DISTINCT blocking_spid)", ViewerDataService.BlockingSlicerSql, StringComparison.Ordinal);
    }

    [Fact]
    public void DeadlockSlicerSql_HourlyBuckets_OverDeadlocksView()
    {
        Assert.Contains("FROM v_deadlocks", ViewerDataService.DeadlockSlicerSql, StringComparison.Ordinal);
        Assert.Contains("date_trunc('hour', collection_time)", ViewerDataService.DeadlockSlicerSql, StringComparison.Ordinal);
        Assert.Contains("COUNT(*) AS deadlock_count", ViewerDataService.DeadlockSlicerSql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bpr")]
    [InlineData("dmv")]
    [InlineData("pairs")]
    [InlineData("deadlocks")]
    [InlineData("blocking-slicer")]
    [InlineData("deadlock-slicer")]
    public void W1eReads_PgDialect_PositionalParams_NoBareNow_NoNLiterals(string which)
    {
        var sql = which switch
        {
            "bpr" => ViewerDataService.BlockedProcessReportsSql,
            "dmv" => ViewerDataService.DmvBlockingSnapshotsSql,
            "pairs" => ViewerDataService.BlockingPairRowsSql,
            "deadlocks" => ViewerDataService.RecentDeadlocksSql,
            "blocking-slicer" => ViewerDataService.BlockingSlicerSql,
            _ => ViewerDataService.DeadlockSlicerSql,
        };

        Assert.DoesNotContain("now(", sql.ToLowerInvariant());
        Assert.DoesNotContain("N'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("@", sql, StringComparison.Ordinal);
        Assert.Contains("$1", sql, StringComparison.Ordinal);
        Assert.Contains("$2", sql, StringComparison.Ordinal);
        Assert.Contains("$3", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BprReads_ReadColumnsThatExistInTheGeneratedBlockedProcessReportsTable()
    {
        Assert.Equal("blocked_process_reports", BlockedProcessReportCollector.Instance.TargetTable);

        var ddl = PgSchemaGenerator.CreateTable(BlockedProcessReportCollector.Instance);
        foreach (var column in new[]
        {
            "event_time", "database_name", "blocked_spid", "blocked_ecid", "blocking_spid", "blocking_ecid",
            "wait_time_ms", "wait_resource", "lock_mode", "blocked_status", "blocked_isolation_level",
            "blocked_log_used", "blocked_transaction_count", "blocked_client_app", "blocked_host_name",
            "blocked_login_name", "blocked_sql_text", "blocking_status", "blocking_isolation_level",
            "blocking_client_app", "blocking_host_name", "blocking_login_name", "blocking_sql_text",
            "blocked_process_report_xml", "blocked_transaction_name", "blocking_transaction_name",
            "blocked_priority", "blocking_priority", "contentious_object", "monitor_loop",
        })
        {
            Assert.Contains(column, ddl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DeadlockRead_ReadsColumnsThatExistInTheGeneratedDeadlocksTable()
    {
        Assert.Equal("deadlocks", DeadlocksCollector.Instance.TargetTable);

        var ddl = PgSchemaGenerator.CreateTable(DeadlocksCollector.Instance);
        foreach (var column in new[] { "collection_time", "deadlock_time", "victim_process_id", "victim_sql_text", "deadlock_graph_xml" })
        {
            Assert.Contains(column, ddl, StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// Pins the copied <see cref="DeadlockProcessDetail.ParseFromRows"/> — the sp_BlitzLock-style graph walk
/// (victim detection, regular-vs-parallel classification, per-process owner/waiter modes, object names,
/// proc-name resolution) and the malformed-XML fallback. Pure (no Postgres); the parse is byte-identical
/// to Lite's, so a drift here is a real regression.
/// </summary>
public sealed class DeadlockProcessDetailParseTests
{
    private const string TwoProcessDeadlockXml = """
        <deadlock>
          <victim-list>
            <victimProcess id="process1" />
          </victim-list>
          <process-list>
            <process id="process1" spid="55" currentdbname="AppDb" waitresource="KEY: 5:72" waittime="1500" lockMode="X" isolationlevel="read committed (2)" logused="200" trancount="1" clientapp="TestApp" hostname="HOST1" loginname="sa" status="suspended" transactionname="user_transaction" priority="0" lasttranstarted="2026-07-01T10:00:00.000">
              <executionStack>
                <frame procname="AppDb.dbo.usp_Update" line="1">UPDATE dbo.t SET c = 1</frame>
              </executionStack>
              <inputbuf>UPDATE dbo.t SET c = 1</inputbuf>
            </process>
            <process id="process2" spid="60" currentdbname="AppDb" waitresource="KEY: 5:73" waittime="1200" lockMode="X" isolationlevel="read committed (2)" logused="300" trancount="1" clientapp="TestApp2" hostname="HOST2" loginname="sa" status="suspended" transactionname="user_transaction" priority="0">
              <inputbuf>UPDATE dbo.t SET c = 2</inputbuf>
            </process>
          </process-list>
          <resource-list>
            <keylock hobtid="72" dbid="5" objectname="AppDb.dbo.t" indexname="pk_t" mode="X">
              <owner-list>
                <owner id="process2" mode="X" />
              </owner-list>
              <waiter-list>
                <waiter id="process1" mode="X" requestType="wait" />
              </waiter-list>
            </keylock>
          </resource-list>
        </deadlock>
        """;

    [Fact]
    public void ParseFromRows_TwoProcessGraph_ResolvesVictimOwnerWaiterAndObject()
    {
        var rows = new List<ViewerDeadlockRow>
        {
            new() { DeadlockTime = new DateTime(2026, 7, 1, 10, 0, 5, DateTimeKind.Unspecified), VictimProcessId = "process1", DeadlockGraphXml = TwoProcessDeadlockXml },
        };

        var details = DeadlockProcessDetail.ParseFromRows(rows);

        Assert.Equal(2, details.Count);
        Assert.All(details, d => Assert.Equal("Regular", d.DeadlockType));
        Assert.All(details, d => Assert.True(d.HasDeadlockXml));

        var victim = details.Single(d => d.Spid == 55);
        Assert.True(victim.IsVictim);
        Assert.Equal("Victim", victim.VictimDisplay);
        Assert.Equal("AppDb", victim.DatabaseName);
        Assert.Equal("X", victim.LockMode);
        Assert.Equal("X", victim.WaiterMode);              // process1 is the waiter on the keylock
        Assert.Equal("AppDb.dbo.t", victim.ObjectNames);
        Assert.Equal("AppDb.dbo.usp_Update", victim.ProcName);
        Assert.Equal("UPDATE dbo.t SET c = 1", victim.SqlText);
        Assert.Equal(1500, victim.WaitTime);
        Assert.Equal("1,500 ms", victim.WaitTimeFormatted);

        var blocker = details.Single(d => d.Spid == 60);
        Assert.False(blocker.IsVictim);
        Assert.Equal("", blocker.VictimDisplay);
        Assert.Equal("X", blocker.OwnerMode);              // process2 owns the keylock
        Assert.Equal("AppDb.dbo.t", blocker.ObjectNames);
    }

    [Fact]
    public void ParseFromRows_MalformedXml_YieldsSingleVictimFallbackRow()
    {
        var rows = new List<ViewerDeadlockRow>
        {
            new() { DeadlockTime = DateTime.UnixEpoch, VictimSqlText = "UPDATE t SET c = 9", DeadlockGraphXml = "<deadlock" },
        };

        var details = DeadlockProcessDetail.ParseFromRows(rows);

        var only = Assert.Single(details);
        Assert.True(only.IsVictim);
        Assert.Equal("UPDATE t SET c = 9", only.SqlText);
        Assert.True(only.HasDeadlockXml);
    }

    [Fact]
    public void ParseFromRows_EmptyGraphXml_IsSkipped()
    {
        var rows = new List<ViewerDeadlockRow>
        {
            new() { DeadlockTime = DateTime.UnixEpoch, DeadlockGraphXml = "" },
        };

        Assert.Empty(DeadlockProcessDetail.ParseFromRows(rows));
    }
}

/// <summary>
/// Gated (DARLING_TEST_PG) live round-trips for the W1e Blocking-depth reads: the widened 37-column BPR
/// grid read (including the report XML → HasReportXml + the long-block tint + the Source badge), the DMV
/// fallback surfacing when the XE source is empty, the deadlock read feeding the copied parser, and the two
/// hourly slicer bucket reads (blocking XE-preferred with the DMV exclusion; deadlocks). Shares the
/// serialized "live-postgres" collection; uses negative sentinel server_ids and cleans up in finally.
/// </summary>
[Collection("live-postgres")]
public sealed class ViewerBlockingDepthLivePostgresTests
{
    private const int BprServerId = -972001;
    private const int DmvFallbackServerId = -972002;
    private const int DeadlockServerId = -972003;
    private const int BlockingSlicerServerId = -972004;
    private const int DeadlockSlicerServerId = -972005;

    private const string ServerName = "viewer-w1e-e2e";

    [Fact]
    public async Task BprRead_WidenedColumns_ReportXml_LongBlock_SourceBadge_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live BPR-depth test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteRowsAsync(connection, "blocked_process_reports", BprServerId);
        await DeleteRowsAsync(connection, "dmv_blocking_snapshots", BprServerId);

        await using var viewer = new ViewerDataService(connectionString!);

        try
        {
            var collectionTime = TruncateToSeconds(DateTime.UtcNow.AddMinutes(-10));
            /* One long block (45s → IsLongBlock + "45.0 sec") carrying a report XML, one short (500 ms). */
            await InsertBlockedProcessReportAsync(connection, BprServerId, collectionTime, collectionTime.AddSeconds(1),
                waitTimeMs: 45000, reportXml: "<blocked-process-report/>", monitorLoop: 7);
            await InsertBlockedProcessReportAsync(connection, BprServerId, collectionTime, collectionTime.AddSeconds(2),
                waitTimeMs: 500, reportXml: "", monitorLoop: 7);

            var rows = await viewer.GetRecentBlockedProcessReportsAsync(BprServerId, collectionTime.AddMinutes(-1), collectionTime.AddMinutes(1));

            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal(BlockedProcessAlertRow.XeReportSource, r.Source));

            var longBlock = rows.Single(r => r.WaitTimeMs == 45000);
            Assert.True(longBlock.IsLongBlock);
            Assert.Equal("45.0 sec", longBlock.WaitTimeFormatted);
            Assert.True(longBlock.HasReportXml);
            Assert.Equal("<blocked-process-report/>", longBlock.BlockedProcessReportXml);
            /* Widened columns the old 9-column read dropped. */
            Assert.Equal("KEY: 5:72", longBlock.WaitResource);
            Assert.Equal("suspended", longBlock.BlockedStatus);
            Assert.Equal("sleeping", longBlock.BlockingStatus);
            Assert.Equal("read committed (2)", longBlock.BlockedIsolationLevel);
            Assert.Equal("sa", longBlock.BlockedLoginName);
            Assert.Equal(1, longBlock.BlockedTransactionCount);
            Assert.Equal(7, longBlock.MonitorLoop);
            Assert.Equal("dbo.t", longBlock.ContentiousObject);

            var shortBlock = rows.Single(r => r.WaitTimeMs == 500);
            Assert.False(shortBlock.IsLongBlock);
            Assert.Equal("500 ms", shortBlock.WaitTimeFormatted);
            Assert.False(shortBlock.HasReportXml);
        }
        finally
        {
            await DeleteRowsAsync(connection, "blocked_process_reports", BprServerId);
            await DeleteRowsAsync(connection, "dmv_blocking_snapshots", BprServerId);
        }
    }

    [Fact]
    public async Task BprRead_DmvFallback_SurfacesWhenNoXe_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live DMV-fallback test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteRowsAsync(connection, "blocked_process_reports", DmvFallbackServerId);
        await DeleteRowsAsync(connection, "dmv_blocking_snapshots", DmvFallbackServerId);

        await using var viewer = new ViewerDataService(connectionString!);

        try
        {
            var collectionTime = TruncateToSeconds(DateTime.UtcNow.AddMinutes(-10));
            /* No XE report; a DMV snapshot for a different SPID pair must surface with the DMV Source badge. */
            await InsertDmvBlockingSnapshotAsync(connection, DmvFallbackServerId, collectionTime, collectionTime.AddSeconds(3),
                blockedSpid: 155, blockingSpid: 160, waitTimeMs: 900);

            var rows = await viewer.GetRecentBlockedProcessReportsAsync(DmvFallbackServerId, collectionTime.AddMinutes(-1), collectionTime.AddMinutes(1));

            var only = Assert.Single(rows);
            Assert.Equal(BlockedProcessAlertRow.DmvSnapshotSource, only.Source);
            Assert.Equal(155, only.BlockedSpid);
            Assert.Equal(160, only.BlockingSpid);
            Assert.False(only.HasReportXml);   // the DMV read never selects the report XML column
        }
        finally
        {
            await DeleteRowsAsync(connection, "blocked_process_reports", DmvFallbackServerId);
            await DeleteRowsAsync(connection, "dmv_blocking_snapshots", DmvFallbackServerId);
        }
    }

    [Fact]
    public async Task DeadlockRead_CarriesGraphXml_ParsesToProcessRows_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live deadlock-read test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteRowsAsync(connection, "deadlocks", DeadlockServerId);

        await using var viewer = new ViewerDataService(connectionString!);

        try
        {
            var collectionTime = TruncateToSeconds(DateTime.UtcNow.AddMinutes(-10));
            const string graph = """
                <deadlock>
                  <victim-list><victimProcess id="p1" /></victim-list>
                  <process-list>
                    <process id="p1" spid="55" currentdbname="AppDb" lockMode="X"><inputbuf>UPDATE dbo.t SET c = 1</inputbuf></process>
                    <process id="p2" spid="60" currentdbname="AppDb" lockMode="X"><inputbuf>UPDATE dbo.t SET c = 2</inputbuf></process>
                  </process-list>
                  <resource-list></resource-list>
                </deadlock>
                """;
            await InsertDeadlockAsync(connection, DeadlockServerId, collectionTime, collectionTime.AddSeconds(4), "p1", graph);

            var rows = await viewer.GetRecentDeadlocksAsync(DeadlockServerId, collectionTime.AddMinutes(-1), collectionTime.AddMinutes(1));

            var row = Assert.Single(rows);
            Assert.Equal("p1", row.VictimProcessId);
            Assert.True(row.HasDeadlockXml);

            var details = DeadlockProcessDetail.ParseFromRows(rows);
            Assert.Equal(2, details.Count);
            Assert.True(details.Single(d => d.Spid == 55).IsVictim);
            Assert.False(details.Single(d => d.Spid == 60).IsVictim);
        }
        finally
        {
            await DeleteRowsAsync(connection, "deadlocks", DeadlockServerId);
        }
    }

    [Fact]
    public async Task BlockingSlicer_HourlyBuckets_XePreferredDmvExcluded_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live blocking-slicer test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteRowsAsync(connection, "blocked_process_reports", BlockingSlicerServerId);
        await DeleteRowsAsync(connection, "dmv_blocking_snapshots", BlockingSlicerServerId);

        await using var viewer = new ViewerDataService(connectionString!);

        try
        {
            var hourA = TruncateToHour(DateTime.UtcNow.AddHours(-5));
            var hourB = hourA.AddHours(1);
            /* Two XE events in hour A, one in hour B, plus a DMV row that must be excluded while XE exists. */
            await InsertBlockedProcessReportAsync(connection, BlockingSlicerServerId, hourA.AddMinutes(5), hourA.AddMinutes(5), waitTimeMs: 1000, reportXml: "", monitorLoop: 1);
            await InsertBlockedProcessReportAsync(connection, BlockingSlicerServerId, hourA.AddMinutes(20), hourA.AddMinutes(20), waitTimeMs: 2000, reportXml: "", monitorLoop: 1);
            await InsertBlockedProcessReportAsync(connection, BlockingSlicerServerId, hourB.AddMinutes(10), hourB.AddMinutes(10), waitTimeMs: 3000, reportXml: "", monitorLoop: 2);
            await InsertDmvBlockingSnapshotAsync(connection, BlockingSlicerServerId, hourA.AddMinutes(30), hourA.AddMinutes(30), blockedSpid: 200, blockingSpid: 201, waitTimeMs: 9000);

            var buckets = await viewer.GetBlockingSlicerDataAsync(BlockingSlicerServerId, hourA.AddMinutes(-1), hourB.AddHours(1));

            Assert.Equal(2, buckets.Count);
            Assert.Equal(2, buckets[0].SessionCount);   // hour A = 2 XE events (DMV excluded)
            Assert.Equal(1, buckets[1].SessionCount);   // hour B = 1 XE event
            Assert.True(buckets[0].BucketTime < buckets[1].BucketTime);
        }
        finally
        {
            await DeleteRowsAsync(connection, "blocked_process_reports", BlockingSlicerServerId);
            await DeleteRowsAsync(connection, "dmv_blocking_snapshots", BlockingSlicerServerId);
        }
    }

    [Fact]
    public async Task DeadlockSlicer_HourlyBuckets_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live deadlock-slicer test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);
        await DeleteRowsAsync(connection, "deadlocks", DeadlockSlicerServerId);

        await using var viewer = new ViewerDataService(connectionString!);

        try
        {
            var hourA = TruncateToHour(DateTime.UtcNow.AddHours(-5));
            var hourB = hourA.AddHours(1);
            await InsertDeadlockAsync(connection, DeadlockSlicerServerId, hourA.AddMinutes(5), hourA.AddMinutes(5), "p1", "<deadlock/>");
            await InsertDeadlockAsync(connection, DeadlockSlicerServerId, hourA.AddMinutes(25), hourA.AddMinutes(25), "p1", "<deadlock/>");
            await InsertDeadlockAsync(connection, DeadlockSlicerServerId, hourB.AddMinutes(15), hourB.AddMinutes(15), "p1", "<deadlock/>");

            var buckets = await viewer.GetDeadlockSlicerDataAsync(DeadlockSlicerServerId, hourA.AddMinutes(-1), hourB.AddHours(1));

            Assert.Equal(2, buckets.Count);
            Assert.Equal(2, buckets[0].SessionCount);
            Assert.Equal(1, buckets[1].SessionCount);
        }
        finally
        {
            await DeleteRowsAsync(connection, "deadlocks", DeadlockSlicerServerId);
        }
    }

    private static async Task InsertBlockedProcessReportAsync(
        NpgsqlConnection connection, int serverId, DateTime collectionTimeUtc, DateTime eventTimeUtc,
        long waitTimeMs, string reportXml, int monitorLoop)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO blocked_process_reports
    (blocked_report_id, collection_time, server_id, server_name, event_time, database_name,
     blocked_spid, blocked_ecid, blocking_spid, blocking_ecid, wait_time_ms, wait_resource, lock_mode,
     blocked_status, blocked_isolation_level, blocked_log_used, blocked_transaction_count,
     blocked_client_app, blocked_host_name, blocked_login_name, blocked_sql_text,
     blocking_status, blocking_isolation_level, blocking_client_app, blocking_host_name, blocking_login_name,
     blocking_sql_text, blocked_process_report_xml, blocked_transaction_name, blocking_transaction_name,
     blocked_priority, blocking_priority, contentious_object, monitor_loop)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21,
        $22, $23, $24, $25, $26, $27, $28, $29, $30, $31, $32, $33, $34)", connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(ServerName);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(eventTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue("AppDb");
        command.Parameters.AddWithValue(55);
        command.Parameters.AddWithValue(1);
        command.Parameters.AddWithValue(60);
        command.Parameters.AddWithValue(0);
        command.Parameters.AddWithValue(waitTimeMs);
        command.Parameters.AddWithValue("KEY: 5:72");
        command.Parameters.AddWithValue("X");
        command.Parameters.AddWithValue("suspended");
        command.Parameters.AddWithValue("read committed (2)");
        command.Parameters.AddWithValue(200L);
        command.Parameters.AddWithValue(1);
        command.Parameters.AddWithValue("TestApp");
        command.Parameters.AddWithValue("HOST1");
        command.Parameters.AddWithValue("sa");
        command.Parameters.AddWithValue("SELECT 1");
        command.Parameters.AddWithValue("sleeping");
        command.Parameters.AddWithValue("read committed (2)");
        command.Parameters.AddWithValue("TestApp2");
        command.Parameters.AddWithValue("HOST2");
        command.Parameters.AddWithValue("sa");
        command.Parameters.AddWithValue("UPDATE t SET c = 1");
        command.Parameters.AddWithValue(reportXml);
        command.Parameters.AddWithValue("user_transaction");
        command.Parameters.AddWithValue("user_transaction");
        command.Parameters.AddWithValue(0);
        command.Parameters.AddWithValue(0);
        command.Parameters.AddWithValue("dbo.t");
        command.Parameters.AddWithValue(monitorLoop);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertDmvBlockingSnapshotAsync(
        NpgsqlConnection connection, int serverId, DateTime collectionTimeUtc, DateTime eventTimeUtc,
        int blockedSpid, int blockingSpid, long waitTimeMs)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO dmv_blocking_snapshots
    (collection_id, collection_time, server_id, server_name, event_time, database_name,
     blocked_spid, blocking_spid, wait_time_ms, lock_mode, blocking_status,
     blocked_sql_text, blocking_sql_text, contentious_object)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)", connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(ServerName);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(eventTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue("AppDb");
        command.Parameters.AddWithValue(blockedSpid);
        command.Parameters.AddWithValue(blockingSpid);
        command.Parameters.AddWithValue(waitTimeMs);
        command.Parameters.AddWithValue("S");
        command.Parameters.AddWithValue("running");
        command.Parameters.AddWithValue("SELECT 2");
        command.Parameters.AddWithValue("UPDATE t SET c = 2");
        command.Parameters.AddWithValue("dbo.t");
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task InsertDeadlockAsync(
        NpgsqlConnection connection, int serverId, DateTime collectionTimeUtc, DateTime deadlockTimeUtc,
        string victimProcessId, string graphXml)
    {
        using var command = new NpgsqlCommand(@"
INSERT INTO deadlocks
    (deadlock_id, collection_time, server_id, server_name, deadlock_time,
     victim_process_id, victim_sql_text, deadlock_graph_xml)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8)", connection);
        command.Parameters.AddWithValue(1L);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(collectionTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(serverId);
        command.Parameters.AddWithValue(ServerName);
        command.Parameters.AddWithValue(DateTime.SpecifyKind(deadlockTimeUtc, DateTimeKind.Unspecified));
        command.Parameters.AddWithValue(victimProcessId);
        command.Parameters.AddWithValue("UPDATE dbo.t SET c = 1");
        command.Parameters.AddWithValue(graphXml);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static DateTime TruncateToSeconds(DateTime value) =>
        DateTime.SpecifyKind(new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerSecond)), DateTimeKind.Unspecified);

    /* The slicer buckets by date_trunc('hour', ...), so anchor on an hour boundary. */
    private static DateTime TruncateToHour(DateTime value) =>
        DateTime.SpecifyKind(new DateTime(value.Ticks - (value.Ticks % TimeSpan.TicksPerHour)), DateTimeKind.Unspecified);

    private static async Task DeleteRowsAsync(NpgsqlConnection connection, string table, int serverId)
    {
        using var cleanup = new NpgsqlCommand($"DELETE FROM {table} WHERE server_id = {serverId};", connection);
        await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
