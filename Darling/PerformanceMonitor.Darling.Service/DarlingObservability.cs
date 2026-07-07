/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using PerformanceMonitor.Collectors;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// The service's writes into the V2 observability store: the servers registry (upserted on every
/// successful connect) and the per-run collection_log (one row per collector cycle, Lite's status
/// vocabulary: SUCCESS / PERMISSIONS / ERROR). Column names mirror Lite's DuckDB tables so
/// viewer/analysis SQL can twin — duckdb_duration_ms records the Postgres storage phase here.
/// Both writes are failure-isolated: they log at Debug and never throw, because an observability
/// write must never break the collection loop.
/// </summary>
public static class DarlingObservability
{
    /* is_enabled is written ONLY on first insert (a server reaching a connect is enabled by definition —
       only enabled servers are in the loop). The ON CONFLICT re-connect deliberately does NOT touch
       is_enabled, so a control-plane disable (config_monitored_servers.is_enabled = FALSE, mirrored onto
       this observed row by SyncServerEnabledStatesAsync) is never clobbered back to TRUE on the next
       connect. Before Stage 2 this forced is_enabled = TRUE on every connect and nothing ever read it. */
    private const string UpsertServerSql = @"
INSERT INTO servers (server_id, server_name, display_name, is_enabled, sql_engine_edition, sql_major_version, created_date, modified_date, monthly_cost_usd)
VALUES ($1, $2, $3, TRUE, $4, $5, $6, $6, $7)
ON CONFLICT (server_id) DO UPDATE SET
    server_name = EXCLUDED.server_name,
    display_name = EXCLUDED.display_name,
    sql_engine_edition = EXCLUDED.sql_engine_edition,
    sql_major_version = EXCLUDED.sql_major_version,
    modified_date = EXCLUDED.modified_date,
    monthly_cost_usd = EXCLUDED.monthly_cost_usd;";

    /* Mirror the DESIRED config (config.config_monitored_servers) onto the OBSERVED registry
       (collect.servers) for the two fields the viewer/FinOps read straight off collect.servers: is_enabled
       and monthly_cost_usd. A disabled server drops out of the collection loop and stops upserting, so without
       this its observed row would keep its last (TRUE) value forever; likewise a cost-only edit reaches the
       FinOps display (which reads collect.servers) through THIS sync, so a cost change needs no
       disconnect+reconnect (see DarlingWorker.ServerDefinitionEquals, which deliberately no longer compares
       cost). Fires when EITHER field drifts. Internal so a pure test can pin the SHAPE. Runs on every reload. */
    internal const string SyncEnabledStatesSql = @"
UPDATE collect.servers s
SET is_enabled = c.is_enabled,
    monthly_cost_usd = c.monthly_cost_usd,
    modified_date = (now() AT TIME ZONE 'UTC')
FROM config.config_monitored_servers c
WHERE s.server_id = c.server_id
  AND (s.is_enabled IS DISTINCT FROM c.is_enabled
       OR s.monthly_cost_usd IS DISTINCT FROM c.monthly_cost_usd);";

    private const string InsertCollectionLogSql = @"
INSERT INTO collection_log (log_id, server_id, server_name, collector_name, collection_time, duration_ms, status, error_message, rows_collected, sql_duration_ms, duckdb_duration_ms)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11);";

    /* The per-server analysis-state marker (V19): the analysis pass's insufficient-data determination,
       persisted so the Viewer's Recommendations tab can tell "still collecting" (a young deployment under
       the 24h data-span gate) apart from a genuine all-clear. One row per server, upserted after each
       completed pass on the natural key (server_id). The bare name resolves through the collect/config
       search path to collect.analysis_state — the observed-output schema, like servers/collection_log.
       Internal so a pure test can pin the UPSERT shape. */
    internal const string WriteAnalysisStateSql = @"
INSERT INTO analysis_state (server_id, insufficient_data, message, analysis_time)
VALUES ($1, $2, $3, $4)
ON CONFLICT (server_id) DO UPDATE SET
    insufficient_data = EXCLUDED.insufficient_data,
    message = EXCLUDED.message,
    analysis_time = EXCLUDED.analysis_time;";

    /// <summary>
    /// Registers (or refreshes) a connected server in the registry: created_date is written only on first
    /// insert, modified_date on every connect. is_enabled is set TRUE on first insert only and left
    /// UNTOUCHED on re-connect (Stage 2) so a control-plane disable is not resurrected — the desired enable
    /// state is mirrored onto this observed row by <see cref="SyncServerEnabledStatesAsync"/> on each reload.
    /// </summary>
    public static async Task UpsertServerAsync(NpgsqlDataSource postgres, ServerRuntime server, ILogger? logger, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(UpsertServerSql, connection);
            command.Parameters.AddWithValue(server.ServerId);
            command.Parameters.AddWithValue(server.StorageName);
            command.Parameters.AddWithValue(server.Config.DisplayName);
            /* The raw probed SERVERPROPERTY('EngineEdition') — real box editions (2/3/4...), not
               just the 5/8 Azure classifications. */
            command.Parameters.AddWithValue(server.EngineEdition);
            command.Parameters.AddWithValue(server.Target.SqlMajorVersion);
            /* Naive-UTC storage: Npgsql 6+ rejects Kind=Utc against `timestamp` — see PgCollectorRowWriter. */
            command.Parameters.AddWithValue(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified));
            /* Per-server FinOps budget from darling.json (0 = hide cost in the viewer, like Lite). */
            command.Parameters.AddWithValue(server.Config.MonthlyCostUsd);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            /* Failure-isolated by design — an observability write must never break the collection loop. */
            logger?.LogDebug("Observability: servers upsert for '{Server}' failed: {Message}",
                server.Config.DisplayName, ex.Message);
        }
    }

    /// <summary>
    /// Mirrors the DESIRED config (<c>config.config_monitored_servers</c>) onto the OBSERVED registry
    /// (<c>collect.servers</c>) for the two fields the viewer/FinOps read straight off <c>collect.servers</c>:
    /// <c>is_enabled</c> and <c>monthly_cost_usd</c>. So a control-plane <c>disable_server</c> flips the
    /// observed row to FALSE even though the disabled server stops upserting (a later <c>enable_server</c>
    /// flips it back), and a cost-only edit reaches the FinOps display through this reload sync with NO
    /// disconnect+reconnect. Runs on each control-plane reload. Failure-isolated (Debug + no-op) like the
    /// other observability writes.
    /// </summary>
    public static async Task SyncServerEnabledStatesAsync(NpgsqlDataSource postgres, ILogger? logger, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(SyncEnabledStatesSql, connection);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken);
            if (changed > 0)
            {
                logger?.LogInformation("Synced observed enable-state/cost for {Count} server(s) from the desired config", changed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* Failure-isolated by design — an observability write must never break the collection loop. */
            logger?.LogDebug("Observability: server enable-state sync failed: {Message}", ex.Message);
        }
    }

    /// <summary>
    /// Records one collector run's outcome in collection_log. duration_ms is the SQL fetch plus
    /// the storage phase; duckdb_duration_ms carries the storage (Postgres) milliseconds under
    /// Lite's column name so analysis SQL can twin.
    /// </summary>
    public static async Task LogCollectionAsync(
        NpgsqlDataSource postgres,
        ServerRuntime server,
        string collectorName,
        string status,
        int rowsCollected,
        long sqlMs,
        long storageMs,
        string? errorMessage,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var message = errorMessage;
            if (message is not null && message.Length > 4000)
            {
                message = message.Substring(0, 4000);
            }

            await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(InsertCollectionLogSql, connection);
            command.Parameters.AddWithValue(CollectionIdGenerator.Next());
            command.Parameters.AddWithValue(server.ServerId);
            command.Parameters.AddWithValue(server.StorageName);
            command.Parameters.AddWithValue(collectorName);
            /* Naive-UTC storage: Npgsql 6+ rejects Kind=Utc against `timestamp` — see PgCollectorRowWriter. */
            command.Parameters.AddWithValue(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified));
            command.Parameters.AddWithValue((int)(sqlMs + storageMs));
            command.Parameters.AddWithValue(status);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)message ?? DBNull.Value });
            command.Parameters.AddWithValue(rowsCollected);
            command.Parameters.AddWithValue((int)sqlMs);
            command.Parameters.AddWithValue((int)storageMs);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            /* Failure-isolated by design — an observability write must never break the collection loop. */
            logger?.LogDebug("Observability: collection_log write for '{Server}' / {Collector} failed: {Message}",
                server.Config.DisplayName, collectorName, ex.Message);
        }
    }

    /// <summary>
    /// Upserts the per-server analysis-state marker (V19) after an analysis pass: <c>insufficient_data =
    /// true</c> plus the engine's message when the pass hit the 24h data-span gate, or <c>false</c> + null
    /// when a real pass completed on enough data. The engine ALREADY makes this determination
    /// (<c>DarlingAnalysisService.InsufficientDataMessage</c>); this persists it so the Viewer's
    /// Recommendations tab — which never calls the engine — shows "still collecting" instead of a false
    /// all-clear on a young deployment's zero-finding read. One row per server, upserted on
    /// <c>server_id</c>. Failure-isolated (Debug + no-op) like the other observability writes — an
    /// analysis-state write must never break the collection loop.
    /// </summary>
    public static async Task WriteAnalysisStateAsync(
        NpgsqlDataSource postgres,
        int serverId,
        bool insufficientData,
        string? message,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(WriteAnalysisStateSql, connection);
            command.Parameters.AddWithValue(serverId);
            command.Parameters.AddWithValue(insufficientData);
            command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)message ?? DBNull.Value });
            /* Naive-UTC storage: Npgsql 6+ rejects Kind=Utc against `timestamp` — see PgCollectorRowWriter. */
            command.Parameters.AddWithValue(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* Failure-isolated by design — an observability write must never break the collection loop. */
            logger?.LogDebug("Observability: analysis-state write for server {ServerId} failed: {Message}",
                serverId, ex.Message);
        }
    }
}
