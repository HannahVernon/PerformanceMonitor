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
/// Pins the optional-TimescaleDB contract. Ungated: the hypertable scope is EXACTLY the shared
/// collector catalog (the registry/config/analysis tables can never sneak in), every
/// create_hypertable partitions by_range on the definition's own prefix time column
/// (collection_time almost everywhere; the config snapshots' capture_time) with if_not_exists +
/// migrate_data, compression segments by server_id, and the policy is the hardcoded 1-day
/// if_not_exists shape. Gated on DARLING_TEST_PG (the dev fixture has the extension): detect →
/// convert (idempotent) → a 40-day-old wait_stats row and a 70-day-old collection_log row are removed
/// by the drop_chunks-based purge (collection_log is a hypertable since V23) while a fresh row holds →
/// the compression policy applies idempotently and lands in timescaledb_information.jobs.
/// </summary>
/* Live-fixture tests share one Postgres store; the collection serializes them so
   cross-test row churn (inserts/purges/deletes/chunk drops) cannot race another class. */
[Collection("live-postgres")]
public sealed class TimescaleSupportTests
{
    /// <summary>Distinctive fake id — a real server_id is a storage-name hash, never this.</summary>
    private const int TestServerId = -717171;

    [Fact]
    public void HypertableScope_IsExactlyTheCollectorCatalog()
    {
        /* Scope = the catalog, table-for-table: 26 collector tables, nothing else. */
        Assert.Equal(
            CollectorCatalog.All.Select(s => s.TargetTable).ToArray(),
            TimescaleSupport.HypertableTables.Select(s => s.TargetTable).ToArray());

        /* The registry/config/analysis tables stay plain: registries keep their PRIMARY KEYs
           (which hypertables reject unless they include the partition column), and
           analysis_findings — designed keyless so it COULD convert later — is a deliberate
           not-yet. Widening the scope must consciously break this pin. */
        var hypertables = TimescaleSupport.HypertableTables.Select(s => s.TargetTable).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var excluded in new[]
        {
            "servers",
            "config_alert_log", "config_edge_trigger_watermarks", "config_mute_rules",
            "analysis_findings", "analysis_muted", "darling_schema_version",
        })
        {
            Assert.False(hypertables.Contains(excluded), $"'{excluded}' must never be converted to a hypertable");
        }

        /* collection_log IS a hypertable (since V23) but is deliberately NOT in the catalog: it is converted +
           compressed DIRECTLY — authoritatively by EnsureCollectionLogHypertableAsync at runtime, plus a
           best-effort V23-migration fast-path — and purged directly by DarlingRetention, so the catalog-driven
           runtime loops (ConvertToHypertables / ApplyCompressionPolicy) must never touch it. Its +1 IS reflected
           in the worker-sizing count, though (HypertableCount). */
        Assert.False(hypertables.Contains("collection_log"),
            "collection_log must stay OUT of the collector catalog — it is converted directly, not via the catalog loop");
        Assert.Equal(TimescaleSupport.HypertableTables.Count + 1, TimescaleSupport.HypertableCount);
    }

    [Fact]
    public void CreateHypertableSql_PartitionsByEachDefinitionsOwnTimeColumn()
    {
        var byName = CollectorCatalog.All.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            "SELECT create_hypertable('wait_stats', by_range('collection_time', INTERVAL '1 days'), if_not_exists => true, migrate_data => true)",
            TimescaleSupport.CreateHypertableSql(byName["wait_stats"]));

        /* The config snapshots partition on their capture_time, not collection_time. */
        Assert.Equal(
            "SELECT create_hypertable('server_config', by_range('capture_time', INTERVAL '1 days'), if_not_exists => true, migrate_data => true)",
            TimescaleSupport.CreateHypertableSql(byName["server_config"]));
        Assert.Equal(
            "SELECT create_hypertable('trace_flags', by_range('capture_time', INTERVAL '1 days'), if_not_exists => true, migrate_data => true)",
            TimescaleSupport.CreateHypertableSql(byName["trace_flags"]));

        /* Every table: its own prefix time column, 1-day chunk interval, idempotent, and existing
           plain-PG data migrates into chunks. */
        foreach (var schema in CollectorCatalog.All)
        {
            var sql = TimescaleSupport.CreateHypertableSql(schema);
            Assert.Contains($"create_hypertable('{schema.TargetTable}', by_range('{schema.PrefixTimeColumnName}', INTERVAL '1 days')", sql, StringComparison.Ordinal);
            Assert.Contains("if_not_exists => true", sql, StringComparison.Ordinal);
            Assert.Contains("migrate_data => true", sql, StringComparison.Ordinal);
        }

        /* collection_log's runtime conversion (the raw-name overload, since it has no ICollectorSchemaInfo) —
           the AUTHORITATIVE path EnsureCollectionLogHypertableAsync runs, identical shape to the collectors. */
        Assert.Equal(
            "SELECT create_hypertable('collection_log', by_range('collection_time', INTERVAL '1 days'), if_not_exists => true, migrate_data => true)",
            TimescaleSupport.CreateHypertableSql(TimescaleSupport.CollectionLogTable, TimescaleSupport.CollectionLogTimeColumn));
    }

    [Fact]
    public void CompressionSql_SegmentsByServerId_OneDayPolicy_IfNotExists()
    {
        var byName = CollectorCatalog.All.ToDictionary(d => d.Name, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            "ALTER TABLE wait_stats SET (timescaledb.compress, timescaledb.compress_segmentby = 'server_id')",
            TimescaleSupport.EnableCompressionSql(byName["wait_stats"]));
        Assert.Equal(
            "SELECT add_compression_policy('wait_stats', compress_after => INTERVAL '1 days', if_not_exists => true)",
            TimescaleSupport.AddCompressionPolicySql(byName["wait_stats"]));

        /* 1 day matches the 1-day chunk interval so chunks become compressible quickly, keeping the
           managed store compact (#1458). */
        Assert.Equal(1, TimescaleSupport.CompressAfterDays);

        foreach (var schema in CollectorCatalog.All)
        {
            Assert.Contains("timescaledb.compress_segmentby = 'server_id'",
                TimescaleSupport.EnableCompressionSql(schema), StringComparison.Ordinal);
            Assert.Contains("if_not_exists => true",
                TimescaleSupport.AddCompressionPolicySql(schema), StringComparison.Ordinal);
        }

        /* collection_log gets the identical compression via the raw-name overloads (the runtime path). */
        Assert.Equal(
            "ALTER TABLE collection_log SET (timescaledb.compress, timescaledb.compress_segmentby = 'server_id')",
            TimescaleSupport.EnableCompressionSql(TimescaleSupport.CollectionLogTable));
        Assert.Equal(
            "SELECT add_compression_policy('collection_log', compress_after => INTERVAL '1 days', if_not_exists => true)",
            TimescaleSupport.AddCompressionPolicySql(TimescaleSupport.CollectionLogTable));
    }

    [Fact]
    public async Task EndToEnd_DetectConvertAndDropChunksPurge_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string (with TimescaleDB installed) to run the live Timescale test.");

        var ct = TestContext.Current.CancellationToken;

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);

        /* The dev fixture has the extension (validated live on 2.28.1): enable must succeed and
           detection must agree. */
        Assert.True(await TimescaleSupport.TryEnableAsync(connection, null, ct),
            "the dev fixture is expected to have TimescaleDB installed");
        Assert.True(await TimescaleSupport.DetectAsync(connection, ct));

        /* Conversion covers every collector table and is idempotent (if_not_exists no-ops). */
        Assert.Equal(CollectorCatalog.All.Count, await TimescaleSupport.ConvertToHypertablesAsync(connection, null, ct));
        Assert.Equal(CollectorCatalog.All.Count, await TimescaleSupport.ConvertToHypertablesAsync(connection, null, ct));

        /* wait_stats really is a hypertable now — so the purge below genuinely exercises
           drop_chunks, not the per-table DELETE fallback. */
        using (var isHypertable = new NpgsqlCommand(
            "SELECT COUNT(*) FROM timescaledb_information.hypertables WHERE hypertable_name = 'wait_stats'", connection))
        {
            Assert.Equal(1L, await isHypertable.ExecuteScalarAsync(ct));
        }

        /* collection_log is ALSO a hypertable now — but NOT via ConvertToHypertablesAsync (it is outside the
           collector catalog). The V23 migration converts it only on an upgrade where the extension already
           exists; on a store whose migrations ran BEFORE CREATE EXTENSION (this shared test database, and any
           fresh managed store) V23's guard skips and the AUTHORITATIVE runtime path is
           EnsureCollectionLogHypertableAsync — the same call the service makes right after TryEnableAsync on
           every start. Exercise it exactly like the service does, then the purge below genuinely hits
           drop_chunks, not the DELETE fallback. */
        Assert.True(await TimescaleSupport.EnsureCollectionLogHypertableAsync(connection, null, ct),
            "EnsureCollectionLogHypertableAsync is expected to convert (or no-op on) collection_log once the extension is enabled");

        using (var logIsHypertable = new NpgsqlCommand(
            "SELECT COUNT(*) FROM timescaledb_information.hypertables WHERE hypertable_name = 'collection_log'", connection))
        {
            Assert.Equal(1L, await logIsHypertable.ExecuteScalarAsync(ct));
        }

        /* Clear leftovers from an earlier aborted run so the assertions below are deterministic. */
        await DeleteTestRowsAsync(connection);

        await using var postgres = NpgsqlDataSource.Create(connectionString!);

        try
        {
            /* All timestamps Kind-Unspecified — naive-UTC storage, see PgCollectorRowWriter. */
            var utcNow = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

            /* wait_stats retention is 30 days. The old row is 40 days back so its WHOLE chunk
               (7-day default width → spanning at most now-43d..now-36d) is past the horizon —
               drop_chunks only drops fully-expired chunks. The fresh row lives in the current
               chunk, which can never be fully expired. */
            using (var insert = new NpgsqlCommand(
                "INSERT INTO wait_stats (collection_id, collection_time, server_id, server_name) VALUES ($1, $2, $3, $4)", connection))
            {
                insert.Parameters.AddWithValue(1L);
                insert.Parameters.AddWithValue(utcNow.AddDays(-40));
                insert.Parameters.AddWithValue(TestServerId);
                insert.Parameters.AddWithValue("timescale-e2e");
                await insert.ExecuteNonQueryAsync(ct);
            }

            using (var insert = new NpgsqlCommand(
                "INSERT INTO wait_stats (collection_id, collection_time, server_id, server_name) VALUES ($1, $2, $3, $4)", connection))
            {
                insert.Parameters.AddWithValue(2L);
                insert.Parameters.AddWithValue(utcNow.AddHours(-1));
                insert.Parameters.AddWithValue(TestServerId);
                insert.Parameters.AddWithValue("timescale-e2e");
                await insert.ExecuteNonQueryAsync(ct);
            }

            /* collection_log is a hypertable since V23, so in Timescale mode it purges via drop_chunks too.
               drop_chunks only drops WHOLE expired chunks, so this row must be past collection_log's own 2x
               horizon (60 days) for its 1-day chunk to be fully expired: 70 days back. (A row inside the 60-day
               window would survive — exercised on the plain-PG DELETE path in DarlingRetentionTests.) */
            using (var insert = new NpgsqlCommand(
                "INSERT INTO collection_log (log_id, server_id, server_name, collector_name, collection_time, status) VALUES ($1, $2, $3, $4, $5, $6)", connection))
            {
                insert.Parameters.AddWithValue(1L);
                insert.Parameters.AddWithValue(TestServerId);
                insert.Parameters.AddWithValue("timescale-e2e");
                insert.Parameters.AddWithValue("wait_stats");
                insert.Parameters.AddWithValue(utcNow.AddDays(-70));
                insert.Parameters.AddWithValue("SUCCESS");
                await insert.ExecuteNonQueryAsync(ct);
            }

            /* The Timescale purge. Deliberately NO assertion on the returned global activity count
               (#1564): chunk drops are per-table + time-window across the WHOLE shared store, so sibling
               collection classes' rows make the global number order-dependent. The contract is the
               OWN-SCOPED evidence below: this server's fresh row survives, its old rows are gone — plus
               the is-hypertable assertions above proving the drop_chunks branch was in play. If
               drop_chunks transiently fails (e.g. a lock clash with the shared fixture's compression
               policy jobs, which run mid-suite), the time-sliced DELETE fallback now clears the rows even
               inside a compressed chunk — the capturing logger surfaces any such fallback in the failure
               text instead of silencing it (a silent skip was #1564's whole failure mode). */
            var purgeLog = new CapturingTestLogger();
            await DarlingRetention.PurgeAsync(postgres, timescaleAvailable: true, purgeLog, ct);

            using (var read = new NpgsqlCommand(
                "SELECT collection_time FROM wait_stats WHERE server_id = $1", connection))
            {
                read.Parameters.AddWithValue(TestServerId);
                using var reader = await read.ExecuteReaderAsync(ct);
                Assert.True(await reader.ReadAsync(ct), $"the fresh wait_stats row did not survive the drop_chunks purge; {purgeLog.Joined}");
                var survivor = reader.GetDateTime(0);
                Assert.True(survivor > utcNow.AddDays(-1), $"the surviving row should be the 1-hour one, got {survivor:O}; {purgeLog.Joined}");
                Assert.False(await reader.ReadAsync(ct), $"the 40-day wait_stats row survived the drop_chunks purge; {purgeLog.Joined}");
            }

            /* The 70-day collection_log row went — via drop_chunks (past the 60-day horizon), or via the
               DELETE fallback if drop_chunks transiently failed. */
            using (var read = new NpgsqlCommand(
                "SELECT COUNT(*) FROM collection_log WHERE server_id = $1", connection))
            {
                read.Parameters.AddWithValue(TestServerId);
                var remaining = (long)(await read.ExecuteScalarAsync(ct))!;
                Assert.True(remaining == 0L, $"the 70-day collection_log row survived the purge ({remaining} row(s)); {purgeLog.Joined}");
            }
        }
        finally
        {
            await DeleteTestRowsAsync(connection);
        }
    }

    [Fact]
    public async Task EndToEnd_CompressionPolicyApplies_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string (with TimescaleDB installed) to run the live compression test.");

        var ct = TestContext.Current.CancellationToken;

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await PgMigrations.MigrateAsync(connection, ct);

        Assert.True(await TimescaleSupport.TryEnableAsync(connection, null, ct),
            "the dev fixture is expected to have TimescaleDB installed");

        /* Compression needs hypertables first — idempotent, so safe regardless of test order. */
        Assert.Equal(CollectorCatalog.All.Count, await TimescaleSupport.ConvertToHypertablesAsync(connection, null, ct));

        /* Applies cleanly and idempotently (the second pass re-runs ALTER SET and the policy
           no-ops on if_not_exists). */
        Assert.Equal(CollectorCatalog.All.Count, await TimescaleSupport.ApplyCompressionPolicyAsync(connection, null, ct));
        Assert.Equal(CollectorCatalog.All.Count, await TimescaleSupport.ApplyCompressionPolicyAsync(connection, null, ct));

        /* The background job really exists. proc_name is 'policy_compression' on the long-stable
           API; the LIKE also tolerates the 2.18+ columnstore rebrand's naming. */
        using (var job = new NpgsqlCommand(@"
SELECT COUNT(*)
FROM timescaledb_information.jobs
WHERE hypertable_name = 'wait_stats'
  AND (proc_name LIKE '%compression%' OR proc_name LIKE '%columnstore%')", connection))
        {
            var jobs = (long)(await job.ExecuteScalarAsync(ct))!;
            Assert.True(jobs >= 1, "expected a compression policy job on wait_stats in timescaledb_information.jobs");
        }

        /* Deliberately NO policy removal on cleanup: the applied policies are the service's
           real end state on this fixture, and if_not_exists keeps every rerun a no-op. */
    }

    private static async Task DeleteTestRowsAsync(NpgsqlConnection connection)
    {
        using var cleanup = new NpgsqlCommand(
            $"DELETE FROM wait_stats WHERE server_id = {TestServerId}; DELETE FROM collection_log WHERE server_id = {TestServerId};", connection);
        await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
