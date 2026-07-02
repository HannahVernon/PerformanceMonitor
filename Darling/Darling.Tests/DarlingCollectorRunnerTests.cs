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
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// M2 slice C: the Darling collector runner. The ungated tests pin the target-detection query
/// (verbatim from Lite's ServerManager, so both SKUs classify a server identically). The full
/// SQL Server → runner → Postgres E2E runs only when BOTH DARLING_TEST_PG (Postgres connection
/// string) and DARLING_TEST_SQL (SQL Server host; optional DARLING_TEST_SQL_USER /
/// DARLING_TEST_SQL_PASSWORD for sql auth) are set — it collects real wait_stats through the
/// shared WaitStatsCollector definition twice, proving watermarks, deltas, and binary COPY
/// against live engines.
/// </summary>
/* Live-fixture tests share one Postgres store; the collection serializes them so
   cross-test row churn (inserts/purges/deletes) cannot race another class's assertions. */
[Collection("live-postgres")]
public sealed class DarlingCollectorRunnerTests
{
    [Fact]
    public void DetectionQuery_MatchesLiteServerManagerProbe()
    {
        Assert.Contains("SERVERPROPERTY('ProductMajorVersion')", DarlingServerConnector.DetectionQueryText, StringComparison.Ordinal);
        Assert.Contains("SERVERPROPERTY('EngineEdition')", DarlingServerConnector.DetectionQueryText, StringComparison.Ordinal);
        Assert.Contains("DB_ID('rdsadmin')", DarlingServerConnector.DetectionQueryText, StringComparison.Ordinal);
        Assert.Contains("HAS_DBACCESS(N'msdb')", DarlingServerConnector.DetectionQueryText, StringComparison.Ordinal);
        Assert.Contains("FROM sys.dm_os_sys_info", DarlingServerConnector.DetectionQueryText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndToEnd_CollectWaitStats_FromLiveSqlServer_IntoLivePostgres()
    {
        var pg = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        var sqlHost = Environment.GetEnvironmentVariable("DARLING_TEST_SQL");
        Assert.SkipWhen(string.IsNullOrEmpty(pg) || string.IsNullOrEmpty(sqlHost),
            "Set DARLING_TEST_PG and DARLING_TEST_SQL to run the live collection E2E.");

        var ct = TestContext.Current.CancellationToken;
        var sqlUser = Environment.GetEnvironmentVariable("DARLING_TEST_SQL_USER");
        var config = new MonitoredServer
        {
            Name = "darling-e2e",
            Host = sqlHost!,
            Auth = string.IsNullOrEmpty(sqlUser) ? "integrated" : "sql",
            Username = sqlUser,
            Password = Environment.GetEnvironmentVariable("DARLING_TEST_SQL_PASSWORD"),
            TrustServerCertificate = true,
        };

        await using var dataSource = NpgsqlDataSource.Create(pg!);
        await using (var migrateConnection = await dataSource.OpenConnectionAsync(ct))
        {
            await PgMigrations.MigrateAsync(migrateConnection, ct);
        }

        var runtime = await DarlingServerConnector.ConnectAsync(config, null, ct);
        Assert.True(runtime.Target.SqlMajorVersion > 0, "probe should detect a real major version");
        Assert.Equal(PerformanceMonitor.Common.ServerIdHelper.GetDeterministicHashCode(config.StorageName), runtime.ServerId);

        var runner = new DarlingCollectorRunner(dataSource, new CollectorDeltaCalculator());

        /* Pre-clean: a prior service smoke against the same store leaves rows for this same
           server_id, and the exact-count assertion below would misread them as COPY errors. */
        await using (var precleanConnection = await dataSource.OpenConnectionAsync(ct))
        {
            using var preclean = new NpgsqlCommand("DELETE FROM wait_stats WHERE server_id = $1", precleanConnection);
            preclean.Parameters.AddWithValue(runtime.ServerId);
            await preclean.ExecuteNonQueryAsync(ct);
        }

        try
        {
            /* First cycle: baselines — every wait row's deltas are 0 but rows land. */
            var first = await runner.RunAsync(WaitStatsCollector.Instance, runtime, ct);
            Assert.True(first.Rows > 0, "a live server always has wait stats");

            /* Second cycle: real deltas through the shared CollectorDeltaCalculator. */
            var second = await runner.RunAsync(WaitStatsCollector.Instance, runtime, ct);
            Assert.True(second.Rows > 0);

            await using var verifyConnection = await dataSource.OpenConnectionAsync(ct);
            using var count = new NpgsqlCommand("SELECT COUNT(*) FROM wait_stats WHERE server_id = $1", verifyConnection);
            count.Parameters.AddWithValue(runtime.ServerId);
            Assert.Equal((long)(first.Rows + second.Rows), await count.ExecuteScalarAsync(ct));

            /* The watermark helper sees what was just written. */
            var lastCollected = await runner.GetLastCollectedTimeAsync(runtime.ServerId, "wait_stats", "collection_time", ct);
            Assert.NotNull(lastCollected);
        }
        finally
        {
            await using var cleanupConnection = await dataSource.OpenConnectionAsync(ct);
            using var cleanup = new NpgsqlCommand("DELETE FROM wait_stats WHERE server_id = $1", cleanupConnection);
            cleanup.Parameters.AddWithValue(runtime.ServerId);
            await cleanup.ExecuteNonQueryAsync(ct);
        }
    }
}
