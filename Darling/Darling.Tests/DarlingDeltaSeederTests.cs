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
/// Pins Darling's restart continuity (the Postgres twin of Lite's DuckDB delta seeding): the four
/// seed queries' latest-row form and column lists, and the shared-core inheritance. The
/// end-to-end test runs only when DARLING_TEST_PG points at a dev Postgres: it inserts two
/// wait_stats rows for a fake server at two collection times, runs SeedFromStoreAsync, and proves
/// the LATEST stored row became the delta baseline — so the first collection after a service
/// restart produces a real delta instead of 0.
/// </summary>
public sealed class DarlingDeltaSeederTests
{
    /// <summary>Distinctive fake id — a real server_id is a storage-name hash, never this.</summary>
    private const int TestServerId = -535353;

    private const string TestWaitType = "DARLING_SEED_E2E_WAIT";

    [Fact]
    public void DarlingDeltaCalculator_IsTheSharedCore()
    {
        /* The seeding host must ride the shared baseline / counter-reset / gap-policy semantics —
           same relationship Lite's DeltaCalculator has to the core. */
        Assert.IsAssignableFrom<CollectorDeltaCalculator>(new DarlingDeltaCalculator());
    }

    [Fact]
    public void SeedSql_WaitStats_LatestRowFormAndColumns()
    {
        var sql = DarlingDeltaCalculator.WaitStatsSeedSql;
        Assert.Contains("SELECT server_id, wait_type, waiting_tasks_count, wait_time_ms, signal_wait_time_ms, collection_time",
            sql, StringComparison.Ordinal);
        Assert.Contains("FROM wait_stats", sql, StringComparison.Ordinal);
        Assert.Contains("(server_id, collection_time) IN (", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT server_id, MAX(collection_time) FROM wait_stats GROUP BY server_id",
            sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SeedSql_FileIoStats_LatestRowFormAndColumns()
    {
        var sql = DarlingDeltaCalculator.FileIoStatsSeedSql;
        Assert.Contains("SELECT server_id, database_name, file_name,", sql, StringComparison.Ordinal);
        Assert.Contains("num_of_reads, num_of_writes, read_bytes, write_bytes,", sql, StringComparison.Ordinal);
        Assert.Contains("io_stall_read_ms, io_stall_write_ms,", sql, StringComparison.Ordinal);
        Assert.Contains("io_stall_queued_read_ms, io_stall_queued_write_ms,", sql, StringComparison.Ordinal);
        Assert.Contains("FROM file_io_stats", sql, StringComparison.Ordinal);
        Assert.Contains("(server_id, collection_time) IN (", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT server_id, MAX(collection_time) FROM file_io_stats GROUP BY server_id",
            sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SeedSql_PerfmonStats_LatestRowFormAndColumns()
    {
        var sql = DarlingDeltaCalculator.PerfmonStatsSeedSql;
        Assert.Contains("SELECT server_id, object_name, counter_name, instance_name, cntr_value, collection_time",
            sql, StringComparison.Ordinal);
        Assert.Contains("FROM perfmon_stats", sql, StringComparison.Ordinal);
        Assert.Contains("(server_id, collection_time) IN (", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT server_id, MAX(collection_time) FROM perfmon_stats GROUP BY server_id",
            sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SeedSql_MemoryGrantStats_LatestRowFormAndColumns()
    {
        var sql = DarlingDeltaCalculator.MemoryGrantStatsSeedSql;
        Assert.Contains("SELECT server_id, pool_id, resource_semaphore_id, timeout_error_count, forced_grant_count",
            sql, StringComparison.Ordinal);
        Assert.Contains("FROM memory_grant_stats", sql, StringComparison.Ordinal);
        Assert.Contains("(server_id, collection_time) IN (", sql, StringComparison.Ordinal);
        Assert.Contains("SELECT server_id, MAX(collection_time) FROM memory_grant_stats GROUP BY server_id",
            sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndToEnd_SeedFromStore_LatestRowBecomesBaseline_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live delta-seeding test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        /* Migrations are idempotent — a fresh store comes up, a current store no-ops. */
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);

        try
        {
            /* Clear leftovers from an earlier aborted run so the assertions below are deterministic. */
            await DeleteTestRowsAsync(connection);

            /* Two rows for the same wait type: an older baseline and the latest one, both recent
               enough to stay inside the wait-stats 300-second gap policy. */
            var olderTime = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-2), DateTimeKind.Unspecified);
            var latestTime = DateTime.SpecifyKind(DateTime.UtcNow.AddMinutes(-1), DateTimeKind.Unspecified);
            await InsertWaitStatsRowAsync(connection, olderTime, waitingTasks: 10, waitTimeMs: 2000, signalWaitTimeMs: 500);
            await InsertWaitStatsRowAsync(connection, latestTime, waitingTasks: 40, waitTimeMs: 5000, signalWaitTimeMs: 800);

            var deltas = new DarlingDeltaCalculator();
            await using (var postgres = NpgsqlDataSource.Create(connectionString!))
            {
                await deltas.SeedFromStoreAsync(postgres, null, TestContext.Current.CancellationToken);
            }

            /* The LATEST row (wait_time_ms 5000) is the baseline: current 5100 => delta exactly
               100. Unseeded, this first sighting would return 0; had the OLDER row (2000) seeded
               instead, the delta would be 3100. */
            var now = DateTime.UtcNow;
            Assert.Equal(100, deltas.CalculateDelta(TestServerId, "wait_stats_time", TestWaitType, 5100, now, 300));

            /* The same latest row seeded the other two wait-stats delta groups. */
            Assert.Equal(5, deltas.CalculateDelta(TestServerId, "wait_stats_tasks", TestWaitType, 45, now, 300));
            Assert.Equal(200, deltas.CalculateDelta(TestServerId, "wait_stats_signal", TestWaitType, 1000, now, 300));
        }
        finally
        {
            await DeleteTestRowsAsync(connection);
        }
    }

    private static async Task InsertWaitStatsRowAsync(
        NpgsqlConnection connection, DateTime collectionTime, long waitingTasks, long waitTimeMs, long signalWaitTimeMs)
    {
        using var insert = new NpgsqlCommand(
            "INSERT INTO wait_stats (collection_id, collection_time, server_id, server_name, wait_type, waiting_tasks_count, wait_time_ms, signal_wait_time_ms) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8)", connection);
        insert.Parameters.AddWithValue(1L);
        /* Naive-UTC storage: Npgsql 6+ rejects Kind=Utc against `timestamp` — see PgCollectorRowWriter. */
        insert.Parameters.AddWithValue(collectionTime);
        insert.Parameters.AddWithValue(TestServerId);
        insert.Parameters.AddWithValue("delta-seed-e2e");
        insert.Parameters.AddWithValue(TestWaitType);
        insert.Parameters.AddWithValue(waitingTasks);
        insert.Parameters.AddWithValue(waitTimeMs);
        insert.Parameters.AddWithValue(signalWaitTimeMs);
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task DeleteTestRowsAsync(NpgsqlConnection connection)
    {
        using var cleanup = new NpgsqlCommand(
            $"DELETE FROM wait_stats WHERE server_id = {TestServerId}", connection);
        await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
