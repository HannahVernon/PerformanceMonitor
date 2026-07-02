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
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the retention contract. Ungated: every collector in the shared catalog has a
/// <see cref="CollectorScheduleDefaults"/> entry with a positive RetentionDays (the purge can
/// never skip a table silently or compute a nonsense cutoff), the generated DELETE targets
/// each definition's own prefix time column (collection_time almost everywhere; the config
/// snapshots' capture_time), and the Timescale branch's drop_chunks statement carries each
/// table's own shared horizon (the drop_chunks purge end-to-end lives in
/// TimescaleSupportTests). Gated on DARLING_TEST_PG: the DELETE-path purge end-to-end against
/// a dev Postgres — expired wait_stats and collection_log rows go, a fresh row survives.
/// </summary>
/* Live-fixture tests share one Postgres store; the collection serializes them so
   cross-test row churn (inserts/purges/deletes) cannot race another class's assertions. */
[Collection("live-postgres")]
public sealed class DarlingRetentionTests
{
    /// <summary>Distinctive fake id — a real server_id is a storage-name hash, never this.</summary>
    private const int TestServerId = -616161;

    [Fact]
    public void EveryCatalogCollector_HasAPositiveSharedRetention()
    {
        foreach (var definition in CollectorCatalog.All)
        {
            Assert.True(CollectorScheduleDefaults.All.TryGetValue(definition.Name, out var schedule),
                $"collector '{definition.Name}' has no CollectorScheduleDefaults entry — its table '{definition.TargetTable}' would never be purged");
            Assert.True(schedule!.RetentionDays > 0,
                $"collector '{definition.Name}' has RetentionDays {schedule.RetentionDays} — the purge cutoff would be nonsensical");
        }
    }

    [Fact]
    public void DeleteSql_TargetsEachDefinitionsOwnTimeColumn()
    {
        var byName = CollectorCatalog.All.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("DELETE FROM wait_stats WHERE collection_time < $1",
            DarlingRetention.DeleteSqlFor(byName["wait_stats"]));
        Assert.Equal("DELETE FROM trace_flags WHERE capture_time < $1",
            DarlingRetention.DeleteSqlFor(byName["trace_flags"]));
    }

    /// <summary>
    /// The Timescale branch: drop_chunks per table with the table's OWN shared horizon flowing
    /// into make_interval (no time column appears — the partition column is implicit in the
    /// hypertable dimension, so capture_time tables get the identical shape). The
    /// DELETE-vs-drop_chunks branch itself is exercised end-to-end in TimescaleSupportTests.
    /// </summary>
    [Fact]
    public void DropChunksSql_CarriesEachTablesOwnRetentionDays()
    {
        var byName = CollectorCatalog.All.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

        Assert.Equal("SELECT drop_chunks('wait_stats', older_than => make_interval(days => 30))",
            DarlingRetention.DropChunksSqlFor(byName["wait_stats"], CollectorScheduleDefaults.All["wait_stats"].RetentionDays));
        Assert.Equal("SELECT drop_chunks('trace_flags', older_than => make_interval(days => 30))",
            DarlingRetention.DropChunksSqlFor(byName["trace_flags"], CollectorScheduleDefaults.All["trace_flags"].RetentionDays));
        Assert.Equal("SELECT drop_chunks('index_object_stats', older_than => make_interval(days => 90))",
            DarlingRetention.DropChunksSqlFor(byName["index_object_stats"], CollectorScheduleDefaults.All["index_object_stats"].RetentionDays));
        Assert.Equal("SELECT drop_chunks('server_properties', older_than => make_interval(days => 365))",
            DarlingRetention.DropChunksSqlFor(byName["server_properties"], CollectorScheduleDefaults.All["server_properties"].RetentionDays));
    }

    [Fact]
    public async Task EndToEnd_PurgeDeletesExpiredRowsAndKeepsFreshOnes_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live retention test.");

        var ct = TestContext.Current.CancellationToken;

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);

        /* Clear leftovers from an earlier aborted run so the assertions below are deterministic. */
        await DeleteTestRowsAsync(connection);

        await using var postgres = NpgsqlDataSource.Create(connectionString!);

        try
        {
            /* All timestamps Kind-Unspecified — naive-UTC storage, see PgCollectorRowWriter. */
            var utcNow = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

            /* wait_stats retention is 30 days: one row well past it, one fresh. Payload columns
               are nullable, so the standard prefix is enough. */
            using (var insert = new NpgsqlCommand(
                "INSERT INTO wait_stats (collection_id, collection_time, server_id, server_name) VALUES ($1, $2, $3, $4)", connection))
            {
                insert.Parameters.AddWithValue(1L);
                insert.Parameters.AddWithValue(utcNow.AddDays(-40));
                insert.Parameters.AddWithValue(TestServerId);
                insert.Parameters.AddWithValue("retention-e2e");
                await insert.ExecuteNonQueryAsync(ct);
            }

            using (var insert = new NpgsqlCommand(
                "INSERT INTO wait_stats (collection_id, collection_time, server_id, server_name) VALUES ($1, $2, $3, $4)", connection))
            {
                insert.Parameters.AddWithValue(2L);
                insert.Parameters.AddWithValue(utcNow.AddHours(-1));
                insert.Parameters.AddWithValue(TestServerId);
                insert.Parameters.AddWithValue("retention-e2e");
                await insert.ExecuteNonQueryAsync(ct);
            }

            /* collection_log purges on its own 30-day horizon. */
            using (var insert = new NpgsqlCommand(
                "INSERT INTO collection_log (log_id, server_id, server_name, collector_name, collection_time, status) VALUES ($1, $2, $3, $4, $5, $6)", connection))
            {
                insert.Parameters.AddWithValue(1L);
                insert.Parameters.AddWithValue(TestServerId);
                insert.Parameters.AddWithValue("retention-e2e");
                insert.Parameters.AddWithValue("wait_stats");
                insert.Parameters.AddWithValue(utcNow.AddDays(-40));
                insert.Parameters.AddWithValue("SUCCESS");
                await insert.ExecuteNonQueryAsync(ct);
            }

            /* At least our two 40-day rows go; a shared dev store may shed more. The
               extension-free DELETE path on purpose (timescaleAvailable: false) — it must keep
               working even on a store whose tables ARE hypertables (DELETE is
               hypertable-agnostic); the drop_chunks branch is TimescaleSupportTests' job. */
            var deleted = await DarlingRetention.PurgeAsync(postgres, timescaleAvailable: false, null, ct);
            Assert.True(deleted >= 2, $"expected the purge to delete at least the two expired test rows, got {deleted}");

            using (var read = new NpgsqlCommand(
                "SELECT collection_time FROM wait_stats WHERE server_id = $1", connection))
            {
                read.Parameters.AddWithValue(TestServerId);
                using var reader = await read.ExecuteReaderAsync(ct);
                Assert.True(await reader.ReadAsync(ct), "the fresh wait_stats row did not survive the purge");
                var survivor = reader.GetDateTime(0);
                Assert.True(survivor > utcNow.AddDays(-1), $"the surviving row should be the 1-hour one, got {survivor:O}");
                Assert.False(await reader.ReadAsync(ct), "the 40-day wait_stats row survived the purge");
            }

            using (var read = new NpgsqlCommand(
                "SELECT COUNT(*) FROM collection_log WHERE server_id = $1", connection))
            {
                read.Parameters.AddWithValue(TestServerId);
                Assert.Equal(0L, await read.ExecuteScalarAsync(ct));
            }
        }
        finally
        {
            await DeleteTestRowsAsync(connection);
        }
    }

    private static async Task DeleteTestRowsAsync(NpgsqlConnection connection)
    {
        using var cleanup = new NpgsqlCommand(
            $"DELETE FROM wait_stats WHERE server_id = {TestServerId}; DELETE FROM collection_log WHERE server_id = {TestServerId};", connection);
        await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
