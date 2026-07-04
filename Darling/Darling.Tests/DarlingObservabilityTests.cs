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
/// Pins the V2 observability migration (the servers registry + collection_log, column names
/// mirroring Lite's DuckDB schema) and exercises the service's writes end-to-end against a dev
/// Postgres when DARLING_TEST_PG is set: migrate (idempotent), upsert a fake server twice (the
/// second must not throw and must refresh modified_date), write one SUCCESS collection_log row,
/// read both back, clean up.
/// </summary>
/* Live-fixture tests share one Postgres store; the collection serializes them so
   cross-test row churn (inserts/purges/deletes) cannot race another class's assertions. */
[Collection("live-postgres")]
public sealed class DarlingObservabilityTests
{
    /// <summary>Distinctive fake id — a real server_id is a storage-name hash, never this.</summary>
    private const int TestServerId = -424242;

    [Fact]
    public void MigrationScripts_EightVersions_V6MemoryViews_V7PlanColumns_V8SchemaSplit()
    {
        Assert.Equal(8, PgMigrations.Scripts.Count);
        Assert.Equal(1, PgMigrations.Scripts[0].Version);
        Assert.Equal(2, PgMigrations.Scripts[1].Version);
        Assert.Equal(3, PgMigrations.Scripts[2].Version);
        Assert.Equal(4, PgMigrations.Scripts[3].Version);
        Assert.Equal(5, PgMigrations.Scripts[4].Version);
        Assert.Equal(6, PgMigrations.Scripts[5].Version);
        Assert.Equal(7, PgMigrations.Scripts[6].Version);
        Assert.Equal(8, PgMigrations.Scripts[7].Version);
        Assert.Equal(8, StorageVersion.SchemaVersion);

        /* V5 completes the v_* twin of Lite's DuckDB view layer -- the copy-parity tail tabs
           (Running Jobs, Configuration, Daily Summary, Collection Health) read these five, so
           their ported SQL stays byte-identical to Lite's. */
        var v5 = PgMigrations.Scripts[4].Sql;
        Assert.Contains("CREATE OR REPLACE VIEW v_running_jobs AS SELECT * FROM running_jobs;", v5, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW v_server_config AS SELECT * FROM server_config;", v5, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW v_database_scoped_config AS SELECT * FROM database_scoped_config;", v5, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW v_trace_flags AS SELECT * FROM trace_flags;", v5, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW v_collection_log AS SELECT * FROM collection_log;", v5, StringComparison.Ordinal);

        /* V6 adds the two memory passthrough views the Memory tab port (W1j) reads -- the Memory
           Clerks + Pressure Events sub-tabs run FROM v_memory_clerks / v_memory_pressure_events,
           byte-identical to Lite. */
        var v6 = PgMigrations.Scripts[5].Sql;
        Assert.Contains("CREATE OR REPLACE VIEW v_memory_clerks AS SELECT * FROM memory_clerks;", v6, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW v_memory_pressure_events AS SELECT * FROM memory_pressure_events;", v6, StringComparison.Ordinal);

        /* V7 adds the deferred-plan-capture columns (#1262) additively — one ADD COLUMN IF NOT
           EXISTS per column so a pre-plan store comes up to shape and a fresh V1 store no-ops. */
        var v7 = PgMigrations.Scripts[6].Sql;
        Assert.Equal("viewer-plan-capture-columns", PgMigrations.Scripts[6].Name);
        Assert.Contains("ALTER TABLE procedure_stats ADD COLUMN IF NOT EXISTS query_plan_xml text;", v7, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE blocked_process_reports ADD COLUMN IF NOT EXISTS blocked_query_plan_xml text;", v7, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE blocked_process_reports ADD COLUMN IF NOT EXISTS blocking_query_plan_xml text;", v7, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE deadlocks ADD COLUMN IF NOT EXISTS victim_query_plan_xml text;", v7, StringComparison.Ordinal);

        /* V8 is the collect/config security split (#1262): creates the two schemas and moves every
           existing object out of public with ALTER ... SET SCHEMA (generated from the catalog so new
           collectors move automatically). The DDL is generated, so pin the SHAPE, not exact text —
           the ALTER DATABASE search_path default is deliberately NOT here (best-effort in MigrateAsync). */
        var v8 = PgMigrations.Scripts[7].Sql;
        Assert.Equal("schema-split-collect-config", PgMigrations.Scripts[7].Name);
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS collect AUTHORIZATION darling;", v8, StringComparison.Ordinal);
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS config AUTHORIZATION darling;", v8, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE IF EXISTS public.wait_stats SET SCHEMA collect;", v8, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE IF EXISTS public.servers SET SCHEMA collect;", v8, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE IF EXISTS public.collection_log SET SCHEMA collect;", v8, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE IF EXISTS public.analysis_findings SET SCHEMA collect;", v8, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE IF EXISTS public.darling_schema_version SET SCHEMA collect;", v8, StringComparison.Ordinal);
        Assert.Contains("ALTER VIEW IF EXISTS public.v_wait_stats SET SCHEMA collect;", v8, StringComparison.Ordinal);
        Assert.Contains("ALTER VIEW IF EXISTS public.v_collection_log SET SCHEMA collect;", v8, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE IF EXISTS public.config_mute_rules SET SCHEMA config;", v8, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE IF EXISTS public.config_alert_log SET SCHEMA config;", v8, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE IF EXISTS public.config_edge_trigger_watermarks SET SCHEMA config;", v8, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE IF EXISTS public.analysis_muted SET SCHEMA config;", v8, StringComparison.Ordinal);
        /* The database-default search_path is set by MigrateAsync (best-effort), not baked into V8. */
        Assert.DoesNotContain("ALTER DATABASE", v8, StringComparison.Ordinal);

        var v2 = PgMigrations.Scripts[1].Sql;
        Assert.Contains("CREATE TABLE IF NOT EXISTS servers (", v2, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS collection_log (", v2, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IF NOT EXISTS idx_collection_log_time ON collection_log(server_id, collection_time)",
            v2, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EndToEnd_UpsertServerAndLogCollection_AgainstDevPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable("DARLING_TEST_PG");
        Assert.SkipWhen(string.IsNullOrEmpty(connectionString),
            "Set DARLING_TEST_PG to a Postgres connection string to run the live observability test.");

        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        /* Migrations are idempotent — an older store comes up to current, a current store no-ops. */
        await PgMigrations.MigrateAsync(connection, TestContext.Current.CancellationToken);

        using (var versions = new NpgsqlCommand("SELECT COUNT(*) FROM darling_schema_version", connection))
        {
            Assert.Equal(8L, await versions.ExecuteScalarAsync(TestContext.Current.CancellationToken));
        }

        /* Clear leftovers from an earlier aborted run so the assertions below are deterministic. */
        await DeleteTestRowsAsync(connection);

        var server = new ServerRuntime
        {
            Config = new MonitoredServer { Name = "obs-e2e", Host = "obs-e2e-host" },
            ConnectionString = "Server=obs-e2e-host",
            Target = new CollectorTargetInfo { SqlMajorVersion = 16 },
            StorageName = "obs-e2e-host",
            ServerId = TestServerId,
            EngineEdition = 3,
        };

        await using var postgres = NpgsqlDataSource.Create(connectionString!);

        await DarlingObservability.UpsertServerAsync(postgres, server, null, TestContext.Current.CancellationToken);

        DateTime firstModified;
        using (var read = new NpgsqlCommand(
            "SELECT server_name, display_name, is_enabled, sql_engine_edition, sql_major_version, created_date, modified_date FROM servers WHERE server_id = $1", connection))
        {
            read.Parameters.AddWithValue(TestServerId);
            using var reader = await read.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken), "servers row missing after upsert");
            Assert.Equal("obs-e2e-host", reader.GetString(0));
            Assert.Equal("obs-e2e", reader.GetString(1));
            Assert.True(reader.GetBoolean(2));
            Assert.Equal(3, reader.GetInt32(3)); /* the real probed engine edition (3 = Enterprise), not a derived 5/8/0 */
            Assert.Equal(16, reader.GetInt32(4));
            Assert.False(reader.IsDBNull(5));
            firstModified = reader.GetDateTime(6);
        }

        /* The second upsert must not throw and must refresh modified_date. */
        await Task.Delay(50, TestContext.Current.CancellationToken);
        await DarlingObservability.UpsertServerAsync(postgres, server, null, TestContext.Current.CancellationToken);

        using (var read = new NpgsqlCommand("SELECT modified_date FROM servers WHERE server_id = $1", connection))
        {
            read.Parameters.AddWithValue(TestServerId);
            var secondModified = Assert.IsType<DateTime>(await read.ExecuteScalarAsync(TestContext.Current.CancellationToken));
            Assert.True(secondModified > firstModified, "second upsert did not refresh modified_date");
        }

        await DarlingObservability.LogCollectionAsync(
            postgres, server, "wait_stats", "SUCCESS", 42, 100, 25, null, null, TestContext.Current.CancellationToken);

        using (var read = new NpgsqlCommand(
            "SELECT collector_name, status, rows_collected, duration_ms, sql_duration_ms, duckdb_duration_ms, error_message FROM collection_log WHERE server_id = $1", connection))
        {
            read.Parameters.AddWithValue(TestServerId);
            using var reader = await read.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken), "collection_log row missing");
            Assert.Equal("wait_stats", reader.GetString(0));
            Assert.Equal("SUCCESS", reader.GetString(1));
            Assert.Equal(42, reader.GetInt32(2));
            Assert.Equal(125, reader.GetInt32(3)); /* sql + storage */
            Assert.Equal(100, reader.GetInt32(4));
            Assert.Equal(25, reader.GetInt32(5)); /* the storage (Postgres) phase, under Lite's column name */
            Assert.True(reader.IsDBNull(6));
            Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken), "expected exactly one collection_log row");
        }

        await DeleteTestRowsAsync(connection);
    }

    private static async Task DeleteTestRowsAsync(NpgsqlConnection connection)
    {
        using var cleanup = new NpgsqlCommand(
            $"DELETE FROM collection_log WHERE server_id = {TestServerId}; DELETE FROM servers WHERE server_id = {TestServerId};", connection);
        await cleanup.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
