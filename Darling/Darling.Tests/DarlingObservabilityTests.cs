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
    public void MigrationScripts_SixteenVersions_V15IndexMetadata_V16ServerUtcOffset()
    {
        Assert.Equal(16, PgMigrations.Scripts.Count);
        Assert.Equal(1, PgMigrations.Scripts[0].Version);
        Assert.Equal(2, PgMigrations.Scripts[1].Version);
        Assert.Equal(3, PgMigrations.Scripts[2].Version);
        Assert.Equal(4, PgMigrations.Scripts[3].Version);
        Assert.Equal(5, PgMigrations.Scripts[4].Version);
        Assert.Equal(6, PgMigrations.Scripts[5].Version);
        Assert.Equal(7, PgMigrations.Scripts[6].Version);
        Assert.Equal(8, PgMigrations.Scripts[7].Version);
        Assert.Equal(9, PgMigrations.Scripts[8].Version);
        Assert.Equal(10, PgMigrations.Scripts[9].Version);
        Assert.Equal(11, PgMigrations.Scripts[10].Version);
        Assert.Equal(12, PgMigrations.Scripts[11].Version);
        Assert.Equal(13, PgMigrations.Scripts[12].Version);
        Assert.Equal(14, PgMigrations.Scripts[13].Version);
        Assert.Equal(15, PgMigrations.Scripts[14].Version);
        Assert.Equal(16, PgMigrations.Scripts[15].Version);
        Assert.Equal(16, StorageVersion.SchemaVersion);

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

        /* V9 restores the FinOps copy-parity fields additively: server_properties gains the three
           inventory columns the shared collector now SELECTs (start time / host OS / AG role), and
           servers gains the per-server cost budget — one ADD COLUMN IF NOT EXISTS each, appended to keep
           the positional binary COPY aligned. Bare names resolve through V8's search_path to collect.*. */
        var v9 = PgMigrations.Scripts[8].Sql;
        Assert.Equal("server-inventory-cost-fields", PgMigrations.Scripts[8].Name);
        Assert.Contains("ALTER TABLE server_properties ADD COLUMN IF NOT EXISTS sqlserver_start_time timestamp;", v9, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE server_properties ADD COLUMN IF NOT EXISTS host_os_version text;", v9, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE server_properties ADD COLUMN IF NOT EXISTS ag_replica_role text;", v9, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE servers ADD COLUMN IF NOT EXISTS monthly_cost_usd numeric;", v9, StringComparison.Ordinal);

        /* V10 adds the latch_stats + spinlock_stats collector tables and their v_* passthrough views
           (Dashboard->Darling parity, #1262). Generated from the collector definitions so the tables
           match the fresh V1 shape; CREATE TABLE IF NOT EXISTS no-ops on a fresh store and really
           creates them on a store built before these collectors existed. */
        var v10 = PgMigrations.Scripts[9].Sql;
        Assert.Equal("latch-spinlock-collectors", PgMigrations.Scripts[9].Name);
        Assert.Contains("CREATE TABLE IF NOT EXISTS latch_stats (", v10, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS spinlock_stats (", v10, StringComparison.Ordinal);
        Assert.Contains("spins_per_collision double precision", v10, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IF NOT EXISTS idx_latch_stats_time ON latch_stats(server_id, collection_time);", v10, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IF NOT EXISTS idx_spinlock_stats_time ON spinlock_stats(server_id, collection_time);", v10, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW v_latch_stats AS SELECT * FROM latch_stats;", v10, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW v_spinlock_stats AS SELECT * FROM spinlock_stats;", v10, StringComparison.Ordinal);

        /* V11 adds the cpu_scheduler_stats + plan_cache_stats collector tables and their v_* passthrough
           views (Dashboard->Darling parity, #1262). Generated from the collector definitions so the
           tables match the fresh V1 shape; CREATE TABLE IF NOT EXISTS no-ops on a fresh store and really
           creates them on a store built before these collectors existed. The two decimal(38,2) averages
           and the boolean pressure warnings prove the type map carried through the generator. */
        var v11 = PgMigrations.Scripts[10].Sql;
        Assert.Equal("cpu-scheduler-plan-cache-collectors", PgMigrations.Scripts[10].Name);
        Assert.Contains("CREATE TABLE IF NOT EXISTS cpu_scheduler_stats (", v11, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS plan_cache_stats (", v11, StringComparison.Ordinal);
        Assert.Contains("avg_runnable_tasks_count numeric(38,2)", v11, StringComparison.Ordinal);
        Assert.Contains("offline_cpu_warning boolean", v11, StringComparison.Ordinal);
        Assert.Contains("avg_use_count numeric(38,2)", v11, StringComparison.Ordinal);
        Assert.Contains("oldest_plan_create_time timestamp", v11, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IF NOT EXISTS idx_cpu_scheduler_stats_time ON cpu_scheduler_stats(server_id, collection_time);", v11, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IF NOT EXISTS idx_plan_cache_stats_time ON plan_cache_stats(server_id, collection_time);", v11, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW v_cpu_scheduler_stats AS SELECT * FROM cpu_scheduler_stats;", v11, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW v_plan_cache_stats AS SELECT * FROM plan_cache_stats;", v11, StringComparison.Ordinal);

        /* V12 adds the session_summary_stats collector table (server-wide session SUMMARY: the
           connection-leak / idle signal) and its v_* passthrough view (Dashboard->Darling parity,
           #1262). Generated from the collector definition so the table matches the fresh V1 shape;
           CREATE TABLE IF NOT EXISTS no-ops on a fresh store and really creates it on a store built
           before this collector existed. Distinct from the per-application session_stats table. */
        var v12 = PgMigrations.Scripts[11].Sql;
        Assert.Equal("session-summary-collector", PgMigrations.Scripts[11].Name);
        Assert.Contains("CREATE TABLE IF NOT EXISTS session_summary_stats (", v12, StringComparison.Ordinal);
        Assert.Contains("idle_sessions_over_30min integer", v12, StringComparison.Ordinal);
        Assert.Contains("sessions_waiting_for_memory integer", v12, StringComparison.Ordinal);
        Assert.Contains("top_application_name text", v12, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IF NOT EXISTS idx_session_summary_stats_time ON session_summary_stats(server_id, collection_time);", v12, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW v_session_summary_stats AS SELECT * FROM session_summary_stats;", v12, StringComparison.Ordinal);

        /* V13 adds the system_health_events collector table (Stage 1 raw system_health Extended Events
           capture: one row per event, raw XML only, no shredding) and its v_* passthrough view
           (Dashboard->Darling health-parser parity, #1262). Generated from the collector definition so
           the table matches the fresh V1 shape; CREATE TABLE IF NOT EXISTS no-ops on a fresh store and
           really creates it on a store built before this collector existed. */
        var v13 = PgMigrations.Scripts[12].Sql;
        Assert.Equal("system-health-events-collector", PgMigrations.Scripts[12].Name);
        Assert.Contains("CREATE TABLE IF NOT EXISTS system_health_events (", v13, StringComparison.Ordinal);
        Assert.Contains("event_time timestamp", v13, StringComparison.Ordinal);
        Assert.Contains("event_type text", v13, StringComparison.Ordinal);
        Assert.Contains("event_xml text", v13, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX IF NOT EXISTS idx_system_health_events_time ON system_health_events(server_id, collection_time);", v13, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW v_system_health_events AS SELECT * FROM system_health_events;", v13, StringComparison.Ordinal);

        /* V14 refreshes EVERY v_* passthrough view's pinned SELECT * column list (#1262): Postgres
           freezes SELECT * at CREATE, so a store upgraded across a column-adding migration keeps a
           stale view. The plan-bearing views (v_blocked_process_reports / v_deadlocks) are the ones
           V7 left stale — the exact staleness PR #1376 worked around by reading base tables — so pin
           their refresh explicitly. (procedure_stats gained query_plan_xml in V7 but has NO v_ view,
           which is why #1376 read the base table for it; there is nothing to refresh here.) */
        var v14 = PgMigrations.Scripts[13].Sql;
        Assert.Equal("refresh-passthrough-views", PgMigrations.Scripts[13].Name);
        Assert.Contains("CREATE OR REPLACE VIEW v_blocked_process_reports AS SELECT * FROM blocked_process_reports;", v14, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW v_deadlocks AS SELECT * FROM deadlocks;", v14, StringComparison.Ordinal);
        Assert.DoesNotContain("v_procedure_stats", v14, StringComparison.Ordinal);

        /* V14 must refresh the COMPLETE passthrough-view set (V4-V6 + the post-V8 collector views),
           and its list must stay the single source of truth: every CREATE OR REPLACE VIEW emitted by
           ANY migration is covered by AllPassthroughViews and vice-versa, so a future collector view
           can never be added without its refresh. */
        foreach (var view in PgSchemaGenerator.AllPassthroughViews)
        {
            var table = view.Substring("v_".Length);
            Assert.Contains(
                $"CREATE OR REPLACE VIEW {view} AS SELECT * FROM {table};", v14, StringComparison.Ordinal);
        }

        var viewsCreatedByAnyMigration = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in PgMigrations.Scripts)
        {
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                script.Sql, @"CREATE OR REPLACE VIEW (v_\w+) AS SELECT \* FROM"))
            {
                viewsCreatedByAnyMigration.Add(m.Groups[1].Value);
            }
        }

        Assert.Equal(
            new System.Collections.Generic.SortedSet<string>(PgSchemaGenerator.AllPassthroughViews, StringComparer.Ordinal),
            viewsCreatedByAnyMigration);

        /* V15 adds the per-index definition metadata for monitor-side UNUSED/DUPLICATE analysis
           (FinOps Index Analysis, Stage 1) additively — one ADD COLUMN IF NOT EXISTS per column so a
           pre-metadata store comes up to shape and a fresh V1 store no-ops — then CREATE OR REPLACE
           re-expands v_index_object_stats' SELECT * so an upgraded store's view (last refreshed by
           V14, before these columns existed) surfaces them. */
        var v15 = PgMigrations.Scripts[14].Sql;
        Assert.Equal("index-metadata-columns", PgMigrations.Scripts[14].Name);
        Assert.Contains("ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS key_columns text;", v15, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS included_columns text;", v15, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS filter_definition text;", v15, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_unique_constraint boolean;", v15, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_foreign_key boolean;", v15, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_foreign_key_reference boolean;", v15, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_disabled boolean;", v15, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS data_compression_desc text;", v15, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS optimize_for_sequential_key boolean;", v15, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS fill_factor smallint;", v15, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_padded boolean;", v15, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS allow_page_locks boolean;", v15, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS allow_row_locks boolean;", v15, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_indexed_view boolean;", v15, StringComparison.Ordinal);
        Assert.Contains("CREATE OR REPLACE VIEW v_index_object_stats AS SELECT * FROM index_object_stats;", v15, StringComparison.Ordinal);

        /* V16 adds server_properties.utc_offset_minutes additively (the monitored server's UTC offset the
           shared collector now writes), so the headless viewer's Server-time display mode can render
           timestamps in the server's own local time — one nullable ADD COLUMN IF NOT EXISTS, appended to
           keep the positional binary COPY aligned. server_properties has no v_* view, so nothing to refresh. */
        var v16 = PgMigrations.Scripts[15].Sql;
        Assert.Equal("server-utc-offset", PgMigrations.Scripts[15].Name);
        Assert.Contains("ALTER TABLE server_properties ADD COLUMN IF NOT EXISTS utc_offset_minutes integer;", v16, StringComparison.Ordinal);

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
            Assert.Equal(15L, await versions.ExecuteScalarAsync(TestContext.Current.CancellationToken));
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
