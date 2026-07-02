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
    private const string UpsertServerSql = @"
INSERT INTO servers (server_id, server_name, display_name, is_enabled, sql_engine_edition, sql_major_version, created_date, modified_date)
VALUES ($1, $2, $3, TRUE, $4, $5, $6, $6)
ON CONFLICT (server_id) DO UPDATE SET
    server_name = EXCLUDED.server_name,
    display_name = EXCLUDED.display_name,
    is_enabled = TRUE,
    sql_engine_edition = EXCLUDED.sql_engine_edition,
    sql_major_version = EXCLUDED.sql_major_version,
    modified_date = EXCLUDED.modified_date;";

    private const string InsertCollectionLogSql = @"
INSERT INTO collection_log (log_id, server_id, server_name, collector_name, collection_time, duration_ms, status, error_message, rows_collected, sql_duration_ms, duckdb_duration_ms)
VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11);";

    /// <summary>
    /// Registers (or refreshes) a connected server in the registry: created_date is written only
    /// on first insert, modified_date on every connect, and a disabled row is re-enabled — a
    /// server present in darling.json is by definition monitored.
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
}
