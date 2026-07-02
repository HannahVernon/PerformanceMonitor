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
using Npgsql;

namespace PerformanceMonitor.Darling.Storage;

/// <summary>
/// Darling's versioned schema migrations — plain SQL scripts the service applies on startup
/// (headless plan: no migration framework). Each script runs once, inside its own transaction,
/// tracked in darling_schema_version. V1 is generated from the collector definitions
/// (<see cref="PgSchemaGenerator.GenerateFullSchema"/>); later versions are appended, never
/// edited. TimescaleDB hypertable conversion is a future migration, applied only when the
/// extension is present and validated against a live Postgres first.
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
    public static async Task<int> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default)
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

        try
        {
            return await MigrateLockedAsync(connection, cancellationToken);
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
    }

    private static async Task<int> MigrateLockedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
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
}
