/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PerformanceMonitor.Darling.Storage;

/// <summary>
/// Darling's versioned schema migrations — plain SQL scripts the service applies on startup
/// (headless plan: no migration framework). Each script runs once, inside its own transaction,
/// tracked in darling_schema_version. V1 is generated from the collector definitions
/// (<see cref="PgSchemaGenerator.GenerateFullSchema"/>); later versions are appended, never
/// edited. Migrations stay engine-plain on purpose: TimescaleDB hypertable conversion is
/// RUNTIME setup (<see cref="TimescaleSupport"/>), applied by the service only when the
/// extension is detected — the same store must work on plain PostgreSQL.
/// </summary>
public static class PgMigrations
{
    public sealed class Migration
    {
        public Migration(int version, string name, string sql)
        {
            Version = version;
            Name = name;
            Sql = sql;
        }

        public int Version { get; }

        public string Name { get; }

        public string Sql { get; }
    }

    public static IReadOnlyList<Migration> Scripts { get; } = new[]
    {
        new Migration(1, "collector-tables", PgSchemaGenerator.GenerateFullSchema()),
        new Migration(2, "server-registry-and-collection-log", V2Sql),
        new Migration(3, "alerting-stores", V3Sql),
        new Migration(4, "analysis-tables", V4Sql),
        new Migration(5, "viewer-passthrough-views", V5Sql),
        new Migration(6, "memory-tab-passthrough-views", V6Sql),
        new Migration(7, "viewer-plan-capture-columns", V7Sql),
        new Migration(8, "schema-split-collect-config", PgSchemaGenerator.GenerateV8Move()),
        new Migration(9, "server-inventory-cost-fields", V9Sql),
        new Migration(10, "latch-spinlock-collectors", PgSchemaGenerator.GenerateV10AddLatchSpinlock()),
        new Migration(11, "cpu-scheduler-plan-cache-collectors", PgSchemaGenerator.GenerateV11AddCpuSchedulerPlanCache()),
        new Migration(12, "session-summary-collector", PgSchemaGenerator.GenerateV12AddSessionSummary()),
        new Migration(13, "system-health-events-collector", PgSchemaGenerator.GenerateV13AddSystemHealthEvents()),
        new Migration(14, "refresh-passthrough-views", PgSchemaGenerator.GenerateV14RefreshViews()),
        new Migration(15, "index-metadata-columns", V15Sql),
        new Migration(16, "server-utc-offset", V16Sql),
    };

    /// <summary>
    /// V2 — the service's observability store: the servers registry (upserted on every
    /// successful connect) and the per-run collection_log. Column names deliberately mirror
    /// Lite's DuckDB schema so viewer/analysis SQL can twin across stores — including
    /// duckdb_duration_ms, which in Darling records the Postgres storage phase. Lite's servers
    /// table also carries auth columns (use_windows_auth/username); Darling deliberately omits
    /// them because auth lives in darling.json. servers keeps its PRIMARY KEY (a registry, not
    /// a hypertable candidate); collection_log has none, same reasoning as the collector tables.
    /// </summary>
    private const string V2Sql = @"
CREATE TABLE IF NOT EXISTS servers (
    server_id integer NOT NULL PRIMARY KEY,
    server_name text NOT NULL,
    display_name text,
    is_enabled boolean NOT NULL DEFAULT TRUE,
    sql_engine_edition integer,
    sql_major_version integer,
    created_date timestamp,
    modified_date timestamp
);

CREATE TABLE IF NOT EXISTS collection_log (
    log_id bigint NOT NULL,
    server_id integer NOT NULL,
    server_name text,
    collector_name text NOT NULL,
    collection_time timestamp NOT NULL,
    duration_ms integer,
    status text NOT NULL,
    error_message text,
    rows_collected integer,
    sql_duration_ms integer,
    duckdb_duration_ms integer
);

CREATE INDEX IF NOT EXISTS idx_collection_log_time ON collection_log(server_id, collection_time);";

    /// <summary>
    /// V3 — the alerting stores behind the Phase-5 shared alert engine (slice D), each mirroring
    /// its Lite DuckDB twin column-for-column so viewer/analysis SQL can twin across stores:
    /// <c>config_alert_log</c> (one combined history row per fired alert — Lite's Schema.cs
    /// CreateAlertLogTable), <c>config_edge_trigger_watermarks</c> (the #1091 rolling-count
    /// watermarks + the time-based failed-job watermark, #1145 restart survival — Lite's
    /// CreateEdgeTriggerWatermarksTable, including its (server_id, metric_name) primary key the
    /// upserts conflict on), and <c>config_mute_rules</c> (Lite's CreateMuteRulesTable). The
    /// alert-log index serves the per-(server, metric) MAX(alert_time) cooldown seeds.
    /// </summary>
    private const string V3Sql = @"
CREATE TABLE IF NOT EXISTS config_alert_log (
    alert_time timestamp NOT NULL,
    server_id integer NOT NULL,
    server_name text NOT NULL,
    metric_name text NOT NULL,
    current_value double precision NOT NULL,
    threshold_value double precision NOT NULL,
    alert_sent boolean NOT NULL DEFAULT FALSE,
    notification_type text NOT NULL DEFAULT 'tray',
    send_error text,
    dismissed boolean NOT NULL DEFAULT FALSE,
    muted boolean NOT NULL DEFAULT FALSE,
    detail_text text,
    context_json text
);

CREATE INDEX IF NOT EXISTS idx_config_alert_log_time ON config_alert_log(server_id, metric_name, alert_time);

CREATE TABLE IF NOT EXISTS config_edge_trigger_watermarks (
    server_id integer NOT NULL,
    metric_name text NOT NULL,
    watermark integer NOT NULL,
    watermark_time timestamp,
    updated_at timestamp NOT NULL,
    PRIMARY KEY (server_id, metric_name)
);

CREATE TABLE IF NOT EXISTS config_mute_rules (
    id text NOT NULL PRIMARY KEY,
    enabled boolean NOT NULL DEFAULT TRUE,
    created_at_utc timestamp NOT NULL,
    expires_at_utc timestamp,
    reason text,
    server_name text,
    metric_name text,
    database_pattern text,
    query_text_pattern text,
    wait_type_pattern text,
    job_name_pattern text
);";

    /// <summary>
    /// V4 — the analysis-engine stores (Phase-5 analysis slice AN1) plus the passthrough views
    /// the ported analysis SQL reads. <c>analysis_findings</c> is Lite's AnalysisSchema v3 shape
    /// column-for-column PLUS the Dashboard's <c>remediation_action_json</c> (recommendations
    /// rebuild D2: the BUILT RemediationAction persisted on the row, serialized by the shared
    /// AlertContextSerializer) — no primary key, same hypertable/COPY reasoning as the collector
    /// tables (TimescaleDB hypertables require the partition column in any unique constraint, and
    /// bulk ingest doesn't want one); finding_id stays a NOT NULL bigint, and the two Lite index
    /// column sets carry over. <c>analysis_muted</c> is a small mute registry and KEEPS its
    /// primary key (like V2's servers); Lite's DuckDB <c>DEFAULT CURRENT_TIMESTAMP</c> on
    /// muted_date is deliberately NOT carried — in Postgres that default would stamp the PG
    /// server's LOCAL clock, and the store always supplies naive-UTC explicitly.
    ///
    /// The <c>v_&lt;table&gt;</c> views exist so analysis SQL ports VERBATIM (AN2/AN3): in Lite
    /// they union the hot DuckDB table with the parquet archive; Darling has no parquet tier, so
    /// they are plain passthroughs. Seventeen views — the fourteen the fact collectors read
    /// (wait/query/query-store/cpu/memory-grant/memory/perfmon/session/file-io stats,
    /// blocked_process_reports, deadlocks, dmv_blocking_snapshots, index_object_stats,
    /// database_size_stats) plus the three Lite's drill-down/storage collectors also read
    /// (v_tempdb_stats in DuckDbFactCollector.Storage + DrillDownCollector.Storage,
    /// v_query_snapshots in DrillDownCollector.Queries, v_database_config in
    /// DrillDownCollector.Config). Every view targets a V1 collector table, pinned by test.
    /// </summary>
    private const string V4Sql = @"
CREATE TABLE IF NOT EXISTS analysis_findings (
    finding_id bigint NOT NULL,
    analysis_time timestamp NOT NULL,
    server_id integer NOT NULL,
    server_name text NOT NULL,
    database_name text,
    time_range_start timestamp,
    time_range_end timestamp,
    severity double precision NOT NULL,
    confidence double precision NOT NULL,
    category text NOT NULL,
    story_path text NOT NULL,
    story_path_hash text NOT NULL,
    story_text text NOT NULL,
    root_fact_key text NOT NULL,
    root_fact_value double precision,
    leaf_fact_key text,
    leaf_fact_value double precision,
    fact_count integer NOT NULL,
    incident_id text,
    remediation_action_json text
);

CREATE INDEX IF NOT EXISTS idx_analysis_findings_time ON analysis_findings(server_id, analysis_time);
CREATE INDEX IF NOT EXISTS idx_analysis_findings_hash ON analysis_findings(story_path_hash);

CREATE TABLE IF NOT EXISTS analysis_muted (
    mute_id bigint NOT NULL PRIMARY KEY,
    server_id integer,
    database_name text,
    story_path_hash text NOT NULL,
    story_path text NOT NULL,
    muted_date timestamp NOT NULL,
    reason text
);

CREATE INDEX IF NOT EXISTS idx_analysis_muted_hash ON analysis_muted(story_path_hash);

CREATE OR REPLACE VIEW v_wait_stats AS SELECT * FROM wait_stats;
CREATE OR REPLACE VIEW v_query_stats AS SELECT * FROM query_stats;
CREATE OR REPLACE VIEW v_query_store_stats AS SELECT * FROM query_store_stats;
CREATE OR REPLACE VIEW v_cpu_utilization_stats AS SELECT * FROM cpu_utilization_stats;
CREATE OR REPLACE VIEW v_memory_grant_stats AS SELECT * FROM memory_grant_stats;
CREATE OR REPLACE VIEW v_memory_stats AS SELECT * FROM memory_stats;
CREATE OR REPLACE VIEW v_perfmon_stats AS SELECT * FROM perfmon_stats;
CREATE OR REPLACE VIEW v_session_stats AS SELECT * FROM session_stats;
CREATE OR REPLACE VIEW v_file_io_stats AS SELECT * FROM file_io_stats;
CREATE OR REPLACE VIEW v_blocked_process_reports AS SELECT * FROM blocked_process_reports;
CREATE OR REPLACE VIEW v_deadlocks AS SELECT * FROM deadlocks;
CREATE OR REPLACE VIEW v_dmv_blocking_snapshots AS SELECT * FROM dmv_blocking_snapshots;
CREATE OR REPLACE VIEW v_index_object_stats AS SELECT * FROM index_object_stats;
CREATE OR REPLACE VIEW v_database_size_stats AS SELECT * FROM database_size_stats;
CREATE OR REPLACE VIEW v_tempdb_stats AS SELECT * FROM tempdb_stats;
CREATE OR REPLACE VIEW v_query_snapshots AS SELECT * FROM query_snapshots;
CREATE OR REPLACE VIEW v_database_config AS SELECT * FROM database_config;";

    /* V5 — the five passthrough views V4 left out, completing the v_* twin of Lite's DuckDB
       view layer so every ported viewer query stays byte-identical to Lite's (the copy-parity
       program's tail tabs read these: Running Jobs, Configuration ×3, Daily Summary /
       Collection Health). Tables all exist since V1/V2; views only. */
    private const string V5Sql = @"
CREATE OR REPLACE VIEW v_running_jobs AS SELECT * FROM running_jobs;
CREATE OR REPLACE VIEW v_server_config AS SELECT * FROM server_config;
CREATE OR REPLACE VIEW v_database_scoped_config AS SELECT * FROM database_scoped_config;
CREATE OR REPLACE VIEW v_trace_flags AS SELECT * FROM trace_flags;
CREATE OR REPLACE VIEW v_collection_log AS SELECT * FROM collection_log;";

    /* V6 — the two memory passthrough views V4/V5 left out, needed by the Memory tab port (W1j): the
       Memory Clerks + Memory Pressure Events sub-tabs read v_memory_clerks / v_memory_pressure_events,
       mirroring Lite, so their ported SQL stays byte-identical. Tables exist since V1; views only. */
    private const string V6Sql = @"
CREATE OR REPLACE VIEW v_memory_clerks AS SELECT * FROM memory_clerks;
CREATE OR REPLACE VIEW v_memory_pressure_events AS SELECT * FROM memory_pressure_events;";

    /* V7 — the deferred-plan-capture columns the viewer's blocked-process/deadlock/procedure "View
       Plan" surfaces read (PR #1262, extending the #1349 pattern to three more collectors). All are
       nullable text appended to existing collector tables, so a store already at V1's pre-plan shape
       comes up to shape with one ADD COLUMN each; a fresh install already has them (V1 is generated
       from the current collector definitions, which now include these columns), and ADD COLUMN IF NOT
       EXISTS makes that a harmless no-op. Darling captures the plans because DarlingConfig.CapturePlans
       defaults true (CollectorContext.CapturePlanXml) — no config change; Lite never sets the flag and
       always writes NULL here. Appended (not inserted) so a fresh V1 store and an upgraded V7 store keep
       an identical physical column order. */
    private const string V7Sql = @"
ALTER TABLE procedure_stats ADD COLUMN IF NOT EXISTS query_plan_xml text;
ALTER TABLE blocked_process_reports ADD COLUMN IF NOT EXISTS blocked_query_plan_xml text;
ALTER TABLE blocked_process_reports ADD COLUMN IF NOT EXISTS blocking_query_plan_xml text;
ALTER TABLE deadlocks ADD COLUMN IF NOT EXISTS victim_query_plan_xml text;";

    /// <summary>
    /// V9 — the FinOps copy-parity fields that were user-input config or previously live-only:
    /// <c>server_properties</c> gains the three inventory columns the shared ServerPropertiesCollector now
    /// SELECTs (start time / host OS / AG replica role — Lite's FinOps Server Inventory previously read
    /// them from a LIVE query the headless viewer can't run), and <c>servers</c> gains
    /// <c>monthly_cost_usd</c> (the per-server FinOps budget — Lite's <c>ServerConnection.MonthlyCostUsd</c>,
    /// user config, upserted from darling.json). All are nullable appended columns: a fresh store's V1
    /// server_properties is generated from the current collector (which already includes the three), and
    /// V2's servers table gets the cost column here — so <c>ADD COLUMN IF NOT EXISTS</c> is a harmless
    /// no-op on the generated columns and the real add on an upgraded store. Appended (not inserted) so a
    /// fresh V1 store and an upgraded store keep an identical physical column order for the binary COPY.
    /// The bare names resolve through the migrate session's <c>search_path = collect, config, public</c>
    /// (V8) to <c>collect.server_properties</c> / <c>collect.servers</c>.
    /// </summary>
    private const string V9Sql = @"
ALTER TABLE server_properties ADD COLUMN IF NOT EXISTS sqlserver_start_time timestamp;
ALTER TABLE server_properties ADD COLUMN IF NOT EXISTS host_os_version text;
ALTER TABLE server_properties ADD COLUMN IF NOT EXISTS ag_replica_role text;
ALTER TABLE servers ADD COLUMN IF NOT EXISTS monthly_cost_usd numeric;";

    /// <summary>
    /// V15 — the per-index DEFINITION metadata monitor-side UNUSED/DUPLICATE index analysis needs
    /// (FinOps Index Analysis, Stage 1), added additively to <c>index_object_stats</c>: the ordered
    /// <c>key_columns</c> / <c>included_columns</c> lists (sp_IndexCleanup's delimited representation,
    /// so the Stage-2 analyzer's string-comparison dedupe ports cleanly), <c>filter_definition</c>,
    /// the uniqueness/constraint/FK discriminators (<c>is_unique_constraint</c>, <c>is_foreign_key</c>,
    /// <c>is_foreign_key_reference</c>) + <c>is_disabled</c>, and the reconstruct-a-CREATE options
    /// (<c>data_compression_desc</c>, <c>optimize_for_sequential_key</c>, <c>fill_factor</c>,
    /// <c>is_padded</c>, <c>allow_page_locks</c>, <c>allow_row_locks</c>). Every column is nullable and
    /// appended, so a fresh store's V1 <c>index_object_stats</c> (generated from the current collector
    /// definition, which now includes them) already has them and <c>ADD COLUMN IF NOT EXISTS</c>
    /// no-ops, while an upgraded store gets the real add — with an identical physical column order for
    /// the binary COPY either way. The trailing <c>CREATE OR REPLACE VIEW</c> re-expands
    /// <c>v_index_object_stats</c>' pinned <c>SELECT *</c> (Postgres freezes it at CREATE, so an
    /// upgraded store's view — last refreshed by V14 before these columns existed — would otherwise
    /// omit them; append-only ADDs keep the refresh legal). Runs after V8, so the bare names resolve
    /// through <c>search_path = collect, config, public</c>.
    /// </summary>
    private const string V15Sql = @"
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS key_columns text;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS included_columns text;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS filter_definition text;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_unique_constraint boolean;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_foreign_key boolean;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_foreign_key_reference boolean;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_disabled boolean;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS data_compression_desc text;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS optimize_for_sequential_key boolean;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS fill_factor smallint;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_padded boolean;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS allow_page_locks boolean;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS allow_row_locks boolean;
ALTER TABLE index_object_stats ADD COLUMN IF NOT EXISTS is_indexed_view boolean;

CREATE OR REPLACE VIEW v_index_object_stats AS SELECT * FROM index_object_stats;";

    /// <summary>
    /// V16 — the monitored server's UTC offset, added additively to <c>server_properties</c> so the
    /// headless viewer can render timestamps in the server's own local time (the Server-time display
    /// mode ported from Lite). The store is naive-UTC; Server-time = UTC + this offset. It is nullable
    /// and appended, so a fresh store's V1 <c>server_properties</c> (generated from the current collector
    /// definition, which now includes it) already has it and <c>ADD COLUMN IF NOT EXISTS</c> no-ops,
    /// while an upgraded store gets the real add — with an identical physical column order for the binary
    /// COPY either way. <c>server_properties</c> has no <c>v_*</c> passthrough view, so nothing to refresh.
    /// Runs after V8, so the bare name resolves through <c>search_path = collect, config, public</c>.
    /// </summary>
    private const string V16Sql = @"
ALTER TABLE server_properties ADD COLUMN IF NOT EXISTS utc_offset_minutes integer;";

    private const string VersionTableSql = @"
CREATE TABLE IF NOT EXISTS darling_schema_version (
    version integer NOT NULL PRIMARY KEY,
    name text NOT NULL,
    applied_at timestamp NOT NULL
);";

    /// <summary>
    /// Session-scoped advisory lock key serializing concurrent migrators — two connections
    /// racing MigrateAsync (a second service instance misconfigured onto the same store, or
    /// parallel test classes) would otherwise both read the same current version and collide on
    /// the darling_schema_version primary key. Released explicitly and on connection close.
    /// </summary>
    private const long MigrationLockKey = 0x4441524C_494E47; /* "DARLING" */

    /// <summary>
    /// Applies every migration newer than the store's current version, each in its own
    /// transaction, stamping darling_schema_version as it goes. Idempotent — a fully migrated
    /// store is a no-op — and safe under concurrent callers (advisory-locked). The connection
    /// must be open.
    /// </summary>
    public static Task<int> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
        => MigrateAsync(connection, logger: null, cancellationToken);

    /// <summary>
    /// The logger-aware overload: after applying migrations it best-effort sets the database-default
    /// <c>search_path = collect, config, public</c> (V8 split) so EVERY future connection resolves the
    /// bare table names without a per-connection setting — the store-establishing side of migration.
    /// Best-effort because <c>ALTER DATABASE</c> needs the database-owner privilege a least-privilege
    /// BYO login may lack; a failure is warned (via <paramref name="logger"/>) but never fails the
    /// migration, since the moves already committed and the managed connection strings carry Search
    /// Path anyway (see <see cref="TrySetDatabaseSearchPathAsync"/>).
    /// </summary>
    public static async Task<int> MigrateAsync(
        NpgsqlConnection connection, ILogger? logger, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        using (var acquireLock = new NpgsqlCommand("SELECT pg_advisory_lock($1)", connection))
        {
            acquireLock.Parameters.AddWithValue(MigrationLockKey);
            await acquireLock.ExecuteNonQueryAsync(cancellationToken);
        }

        int applied;
        try
        {
            applied = await MigrateLockedAsync(connection, cancellationToken);
        }
        finally
        {
            try
            {
                using var releaseLock = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", connection);
                releaseLock.Parameters.AddWithValue(MigrationLockKey);
                await releaseLock.ExecuteNonQueryAsync(CancellationToken.None);
            }
            catch
            {
                /* Connection close releases session advisory locks anyway. */
            }
        }

        /* Outside the advisory lock and the per-migration transactions: establish the durable
           database-default search_path for all future connections. Idempotent, best-effort. */
        await TrySetDatabaseSearchPathAsync(connection, logger, cancellationToken);
        return applied;
    }

    private static async Task<int> MigrateLockedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        /* Resolve bare names through collect/config for this migrate session. Load-bearing from V8
           on: V8 moves darling_schema_version into collect, and the version stamp below writes it by
           its bare name — without this the post-move stamp would resolve against the default path
           ("$user", public) and fail. Setting a path whose schemas don't exist yet is legal (pre-V8
           they simply resolve to public, exactly as before), so this is safe on every store version
           and independent of any connection-string Search Path. Session-scoped (outside the
           per-migration transactions), so a migration rollback never unsets it. */
        using (var setPath = new NpgsqlCommand("SET search_path = " + PgSchemaGenerator.SearchPath, connection))
        {
            await setPath.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var createVersionTable = new NpgsqlCommand(VersionTableSql, connection))
        {
            await createVersionTable.ExecuteNonQueryAsync(cancellationToken);
        }

        int currentVersion;
        using (var readVersion = new NpgsqlCommand("SELECT COALESCE(MAX(version), 0) FROM darling_schema_version", connection))
        {
            currentVersion = Convert.ToInt32(await readVersion.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        }

        var applied = 0;
        foreach (var migration in Scripts)
        {
            if (migration.Version <= currentVersion)
            {
                continue;
            }

            using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            using (var apply = new NpgsqlCommand(migration.Sql, connection, transaction))
            {
                await apply.ExecuteNonQueryAsync(cancellationToken);
            }

            using (var stamp = new NpgsqlCommand(
                "INSERT INTO darling_schema_version (version, name, applied_at) VALUES ($1, $2, $3)", connection, transaction))
            {
                stamp.Parameters.AddWithValue(migration.Version);
                stamp.Parameters.AddWithValue(migration.Name);
                /* Naive-UTC storage: Npgsql 6+ rejects Kind=Utc against `timestamp` — see PgCollectorRowWriter. */
                stamp.Parameters.AddWithValue(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified));
                await stamp.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            applied++;
        }

        return applied;
    }

    /// <summary>
    /// Best-effort: make <c>search_path = collect, config, public</c> the database default, so
    /// EVERY future connection (the service pool's collector writes, the MCP host, the Viewer,
    /// <c>psql</c>/<c>pg_dump</c>, BYO) resolves the bare table names to the V8 schemas without any
    /// per-connection setting. Complements — does not replace — the session <c>SET</c> in
    /// <see cref="MigrateAsync"/> (which covers the migrate connection itself) and the
    /// <c>Search Path</c> keyword the managed connection strings carry.
    ///
    /// <para>Deliberately OUTSIDE the V8 transaction and swallowing failure: <c>ALTER DATABASE</c>
    /// needs the database-owner privilege, which a least-privilege bring-your-own-Postgres login may
    /// lack. A failure here must NOT fail the migration (the moves already committed) — it logs a
    /// warning telling the operator to run the statement themselves as owner. Idempotent, so it
    /// re-asserts harmlessly on every start. Targets the connection's live database name (managed =
    /// <c>darling</c>; BYO = whatever the operator connected to), identifier-quoted.</para>
    /// </summary>
    public static async Task TrySetDatabaseSearchPathAsync(
        NpgsqlConnection connection, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        if (connection is null)
        {
            throw new ArgumentNullException(nameof(connection));
        }

        var databaseName = connection.Database;
        if (string.IsNullOrEmpty(databaseName))
        {
            return;
        }

        var quotedDatabase = "\"" + databaseName.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        try
        {
            using var command = new NpgsqlCommand(
                $"ALTER DATABASE {quotedDatabase} SET search_path = {PgSchemaGenerator.SearchPath}", connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogWarning(
                "Could not set the database default search_path on {Database} ({Message}). The managed " +
                "connection strings still carry Search Path, but if you point your own tools at this store, " +
                "run this once as the database owner: ALTER DATABASE {Database} SET search_path = {SearchPath};",
                databaseName, ex.Message, databaseName, PgSchemaGenerator.SearchPath);
        }
    }
}
