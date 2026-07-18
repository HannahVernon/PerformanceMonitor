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
using System.Threading;
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
    public void PurgeSummary_TotalPurged_SumsDeletedRowsAndDroppedChunks()
    {
        /* The single headline count the daily log + the purge_now result_json ("rowsPurged") report. */
        var summary = new PurgeSummary(TablesPurged: 31, RowsDeleted: 1200, ChunksDropped: 42);
        Assert.Equal(1242, summary.TotalPurged);
        Assert.Equal(0, new PurgeSummary(0, 0, 0).TotalPurged);
    }

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
    public void DeleteSql_BatchesOnEachDefinitionsOwnTimeColumn()
    {
        var byName = CollectorCatalog.All.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

        /* The Postgres twin of the Dashboard's DELETE TOP(10000): batched via ctid IN (SELECT ... LIMIT
           10000), with the time predicate REPEATED in the outer delete so a TimescaleDB hypertable's
           per-chunk ctid can never delete a fresh row in another chunk. $1 is bound once and referenced by
           both positions. */
        Assert.Equal(
            "DELETE FROM wait_stats WHERE collection_time < $1 AND ctid IN (SELECT ctid FROM wait_stats WHERE collection_time < $1 LIMIT 10000)",
            DarlingRetention.DeleteSqlFor(byName["wait_stats"]));

        /* The config snapshots batch on their capture_time, not collection_time. */
        Assert.Equal(
            "DELETE FROM trace_flags WHERE capture_time < $1 AND ctid IN (SELECT ctid FROM trace_flags WHERE capture_time < $1 LIMIT 10000)",
            DarlingRetention.DeleteSqlFor(byName["trace_flags"]));

        /* collection_log's PLAIN-PostgreSQL / conversion-failed FALLBACK still runs through the same batched
           builder (since V23 it is a hypertable, so on Timescale it purges via drop_chunks — pinned below). */
        Assert.Equal(
            "DELETE FROM collection_log WHERE collection_time < $1 AND ctid IN (SELECT ctid FROM collection_log WHERE collection_time < $1 LIMIT 10000)",
            DarlingRetention.BatchedDeleteSql("collection_log", "collection_time"));
    }

    [Fact]
    public void CollectionLogRetention_IsTwiceTheBaseWindow()
    {
        /* collection_log is kept 2x the base data-retention window (the Dashboard's retention_date x2) so a
           run-record outlives the metric rows it explains: 60 days vs the dominant 30-day collector horizon. */
        Assert.Equal(30, DarlingRetention.DataRetentionBaseDays);
        Assert.Equal(60, DarlingRetention.CollectionLogRetentionDays);
        Assert.Equal(DarlingRetention.DataRetentionBaseDays * 2, DarlingRetention.CollectionLogRetentionDays);

        /* The base mirrors the dominant collector horizon it is derived from. */
        Assert.Equal(DarlingRetention.DataRetentionBaseDays, CollectorScheduleDefaults.All["wait_stats"].RetentionDays);
    }

    [Fact]
    public void AlertHistoryRetention_IsNinetyDays_AndBatchesOnAlertTime()
    {
        /* config_alert_log (the fired-alert history) is a plain config-schema registry table — not a collector
           (no CollectorScheduleDefaults horizon) and not a hypertable — so it purges through the SAME batched
           DELETE builder as collection_log, on its own alert_time column. Kept 90 days: a bounded but generous
           audit-trail horizon (there is no operator setting governing it today, so the constant is the source
           of truth). */
        Assert.Equal(90, DarlingRetention.AlertHistoryRetentionDays);
        Assert.Equal(
            "DELETE FROM config_alert_log WHERE alert_time < $1 AND ctid IN (SELECT ctid FROM config_alert_log WHERE alert_time < $1 LIMIT 10000)",
            DarlingRetention.BatchedDeleteSql("config_alert_log", "alert_time"));
    }

    /* ---------------- batched-drain loop (pure, injected executor) ---------------- */

    [Fact]
    public async Task DrainBatches_LoopsUntilBatchBelowCap_ThenStops()
    {
        /* [cap, cap, partial] -> 3 executions, summed; the partial batch (< cap) terminates the drain. */
        var batches = new Queue<int>(new[] { 10000, 10000, 3 });
        var calls = 0;
        var total = await DarlingRetention.DrainBatchesAsync(
            _ => { calls++; return Task.FromResult(batches.Dequeue()); }, batchSize: 10000, CancellationToken.None);

        Assert.Equal(20003, total);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task DrainBatches_SingleUnderCapBatch_StopsAfterOne()
    {
        var calls = 0;
        var total = await DarlingRetention.DrainBatchesAsync(
            _ => { calls++; return Task.FromResult(5); }, batchSize: 10000, CancellationToken.None);

        Assert.Equal(5, total);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DrainBatches_ExactMultipleOfCap_TerminatesOnTheEmptyBatch()
    {
        /* Exactly cap, then 0: the full-cap batch forces another round; the empty batch terminates it. */
        var batches = new Queue<int>(new[] { 10000, 0 });
        var calls = 0;
        var total = await DarlingRetention.DrainBatchesAsync(
            _ => { calls++; return Task.FromResult(batches.Dequeue()); }, batchSize: 10000, CancellationToken.None);

        Assert.Equal(10000, total);
        Assert.Equal(2, calls);
    }

    /* ---------------- run-record status/message (pure) ---------------- */

    [Fact]
    public void BuildRunRecordSummary_AllTablesClean_IsSuccess()
    {
        var (status, message) = DarlingRetention.BuildRunRecordSummary(
            tablesPurged: 33, totalRowsDeleted: 1200, totalChunksDropped: 42, tablesFailed: 0);

        Assert.Equal("SUCCESS", status);
        Assert.Contains("33 table(s)", message);
        Assert.Contains("1200 row(s) deleted", message);
        Assert.Contains("42 chunk(s) dropped", message);
        Assert.DoesNotContain("failed", message);
    }

    [Fact]
    public void BuildRunRecordSummary_SomeTablesFailed_IsWarning()
    {
        var (status, message) = DarlingRetention.BuildRunRecordSummary(
            tablesPurged: 30, totalRowsDeleted: 500, totalChunksDropped: 0, tablesFailed: 3);

        Assert.Equal("WARNING", status);
        Assert.Contains("3 failed", message);
    }

    [Fact]
    public async Task Purge_UnexpectedThrowInSweep_DoesNotPropagate_ReturnsPartialSummary()
    {
        /* The daily caller does NOT wrap PurgeAsync, so an unexpected throw inside the sweep would kill the
           collection loop. Here a caller resolver throws on the first collector, driving the outer catch (the
           ERROR path): PurgeAsync must swallow it and return a partial summary. The null data source is never
           dereferenced before the throw, and the ERROR run-record write is itself failure-isolated, so this
           stays a DB-free test. */
        var summary = await DarlingRetention.PurgeAsync(
            postgres: null!, timescaleAvailable: false, logger: null, TestContext.Current.CancellationToken,
            retentionDaysFor: _ => throw new InvalidOperationException("resolver boom"));

        Assert.Equal(0, summary.TablesPurged);
        Assert.Equal(0, summary.TotalPurged);
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

        /* collection_log is a hypertable since V23 (converted directly by the V23 migration, outside the
           collector catalog), so with Timescale it purges via drop_chunks at its own 2x horizon — the raw-table
           overload, since it has no ICollectorSchemaInfo. NOT the batched DELETE (that is the plain-PG fallback). */
        Assert.Equal(
            "SELECT drop_chunks('collection_log', older_than => make_interval(days => 60))",
            DarlingRetention.DropChunksSqlFor("collection_log", DarlingRetention.CollectionLogRetentionDays));
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

            /* collection_log purges on its own 2x horizon (60 days) so a run-record outlives the metric rows
               it explains: a 70-day row is past the horizon and goes, a 45-day row is inside the 60-day
               window (but past the 30-day data window) and SURVIVES — proving the 2x extension. */
            using (var insert = new NpgsqlCommand(
                "INSERT INTO collection_log (log_id, server_id, server_name, collector_name, collection_time, status) VALUES ($1, $2, $3, $4, $5, $6)", connection))
            {
                insert.Parameters.AddWithValue(1L);
                insert.Parameters.AddWithValue(TestServerId);
                insert.Parameters.AddWithValue("retention-e2e");
                insert.Parameters.AddWithValue("wait_stats");
                insert.Parameters.AddWithValue(utcNow.AddDays(-70));
                insert.Parameters.AddWithValue("SUCCESS");
                await insert.ExecuteNonQueryAsync(ct);
            }

            using (var insert = new NpgsqlCommand(
                "INSERT INTO collection_log (log_id, server_id, server_name, collector_name, collection_time, status) VALUES ($1, $2, $3, $4, $5, $6)", connection))
            {
                insert.Parameters.AddWithValue(2L);
                insert.Parameters.AddWithValue(TestServerId);
                insert.Parameters.AddWithValue("retention-e2e");
                insert.Parameters.AddWithValue("wait_stats");
                insert.Parameters.AddWithValue(utcNow.AddDays(-45));
                insert.Parameters.AddWithValue("SUCCESS");
                await insert.ExecuteNonQueryAsync(ct);
            }

            /* config_alert_log (fired-alert history) purges on its own 90-day horizon: a 100-day row is past
               it and goes, a 1-hour row survives. Only alert_time / server_id / server_name / metric_name +
               the two NOT NULL value columns are required (the rest of the V3 columns default). */
            using (var insert = new NpgsqlCommand(
                "INSERT INTO config_alert_log (alert_time, server_id, server_name, metric_name, current_value, threshold_value) VALUES ($1, $2, $3, $4, $5, $6)", connection))
            {
                insert.Parameters.AddWithValue(utcNow.AddDays(-100));
                insert.Parameters.AddWithValue(TestServerId);
                insert.Parameters.AddWithValue("retention-e2e");
                insert.Parameters.AddWithValue("cpu");
                insert.Parameters.AddWithValue(99.0);
                insert.Parameters.AddWithValue(80.0);
                await insert.ExecuteNonQueryAsync(ct);
            }

            using (var insert = new NpgsqlCommand(
                "INSERT INTO config_alert_log (alert_time, server_id, server_name, metric_name, current_value, threshold_value) VALUES ($1, $2, $3, $4, $5, $6)", connection))
            {
                insert.Parameters.AddWithValue(utcNow.AddHours(-1));
                insert.Parameters.AddWithValue(TestServerId);
                insert.Parameters.AddWithValue("retention-e2e");
                insert.Parameters.AddWithValue("cpu");
                insert.Parameters.AddWithValue(99.0);
                insert.Parameters.AddWithValue(80.0);
                await insert.ExecuteNonQueryAsync(ct);
            }

            /* At least our three expired rows go (40-day wait_stats, 70-day collection_log, 100-day
               config_alert_log); a shared dev store may shed more. The extension-free DELETE path on purpose
               (timescaleAvailable: false) — it must keep working even on a store whose tables ARE hypertables
               (DELETE is hypertable-agnostic); the drop_chunks branch is TimescaleSupportTests' job. */
            /* Deliberately NO assertion on the returned global activity count (#1564): the shared dev
               store's contents at purge time depend on sibling-class order, making the global number
               flaky ("expected 3, got 2" on one dispatch). The contract is the OWN-SCOPED evidence
               below, keyed on TestServerId per table, plus the fleet-sentinel audit record. */
            await DarlingRetention.PurgeAsync(postgres, timescaleAvailable: false, null, ct);

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
                "SELECT collection_time FROM collection_log WHERE server_id = $1 ORDER BY collection_time DESC", connection))
            {
                read.Parameters.AddWithValue(TestServerId);
                using var reader = await read.ExecuteReaderAsync(ct);
                Assert.True(await reader.ReadAsync(ct), "the 45-day collection_log row (inside the 60-day 2x horizon) did not survive the purge");
                var survivor = reader.GetDateTime(0);
                Assert.True(survivor < utcNow.AddDays(-44) && survivor > utcNow.AddDays(-46),
                    $"the surviving log row should be the 45-day one, got {survivor:O}");
                Assert.False(await reader.ReadAsync(ct), "the 70-day collection_log row survived past the 60-day horizon");
            }

            using (var read = new NpgsqlCommand(
                "SELECT alert_time FROM config_alert_log WHERE server_id = $1", connection))
            {
                read.Parameters.AddWithValue(TestServerId);
                using var reader = await read.ExecuteReaderAsync(ct);
                Assert.True(await reader.ReadAsync(ct), "the fresh config_alert_log row did not survive the purge");
                var survivor = reader.GetDateTime(0);
                Assert.True(survivor > utcNow.AddDays(-1), $"the surviving alert-history row should be the 1-hour one, got {survivor:O}");
                Assert.False(await reader.ReadAsync(ct), "the 100-day config_alert_log row survived past the 90-day horizon");
            }

            /* The purge writes ONE auditable run-record under the fleet sentinel server_id — SUCCESS here
               (every table purged cleanly on this store). Never attributed to a real monitored server. */
            using (var read = new NpgsqlCommand(
                "SELECT status FROM collection_log WHERE server_id = $1 AND collector_name = 'data_retention' ORDER BY collection_time DESC LIMIT 1", connection))
            {
                read.Parameters.AddWithValue(DarlingObservability.FleetServerId);
                Assert.Equal("SUCCESS", await read.ExecuteScalarAsync(ct));
            }
        }
        finally
        {
            await DeleteTestRowsAsync(connection);
        }
    }

    [Fact]
    public async Task EndToEnd_PurgeResolverThrows_WritesErrorRunRecord_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live ERROR run-record test.");

        var ct = TestContext.Current.CancellationToken;

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);
        await DeleteTestRowsAsync(connection);

        await using var postgres = NpgsqlDataSource.Create(connectionString!);
        try
        {
            /* A resolver that throws drives PurgeAsync into its outer catch, which writes an ERROR run-record
               to the store under the fleet sentinel — the failure is auditable, not a crashed loop. */
            var summary = await DarlingRetention.PurgeAsync(
                postgres, timescaleAvailable: false, null, ct,
                retentionDaysFor: _ => throw new InvalidOperationException("resolver boom"));
            Assert.Equal(0, summary.TablesPurged);

            using var read = new NpgsqlCommand(
                "SELECT status FROM collection_log WHERE server_id = $1 AND collector_name = 'data_retention' ORDER BY collection_time DESC LIMIT 1", connection);
            read.Parameters.AddWithValue(DarlingObservability.FleetServerId);
            Assert.Equal("ERROR", await read.ExecuteScalarAsync(ct));
        }
        finally
        {
            await DeleteTestRowsAsync(connection);
        }
    }

    private static async Task DeleteTestRowsAsync(NpgsqlConnection connection)
    {
        /* Also clears the fleet-sentinel data_retention run-record the purge writes, so the shared dev store
           doesn't accumulate them and the run-record assertion always reads THIS run's row. */
        using var cleanup = new NpgsqlCommand(
            $"DELETE FROM wait_stats WHERE server_id = {TestServerId}; " +
            $"DELETE FROM collection_log WHERE server_id = {TestServerId}; " +
            $"DELETE FROM config_alert_log WHERE server_id = {TestServerId}; " +
            $"DELETE FROM collection_log WHERE server_id = {DarlingObservability.FleetServerId} AND collector_name = 'data_retention';",
            connection);
        await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
