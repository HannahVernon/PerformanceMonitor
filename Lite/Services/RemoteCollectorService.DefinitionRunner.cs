/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Models;

namespace PerformanceMonitorLite.Services;

public partial class RemoteCollectorService
{
    /// <summary>
    /// Runs a shared collector definition (PerformanceMonitor.Collectors) against one server:
    /// SQL phase (definition reads/filters rows) and storage phase (appender write with the
    /// standard prefix columns) are timed separately, preserving the #1180 fetch-side metrics.
    /// Collectors migrate onto this runner one PR at a time (headless plan v5.1); it reproduces
    /// the hand-rolled per-collector loop byte-for-byte at the storage layer.
    /// </summary>
    private async Task<int> RunCollectorDefinitionAsync<TRow>(
        ICollectorDefinition<TRow> definition,
        ServerConnection server,
        CancellationToken cancellationToken)
    {
        var serverId = GetServerId(server);
        var collectionTime = DateTime.UtcNow;
        _lastSqlMs = 0;
        _lastDuckDbMs = 0;

        var status = _serverManager.GetConnectionStatus(server.Id);
        var target = new CollectorTargetInfo { IsAzureSqlDb = status.SqlEngineEdition == 5 };

        /* Some collectors don't exist on some targets (e.g. ring buffers on Azure SQL DB) —
           skip the cycle entirely, matching the original hand-rolled collectors. */
        if (!definition.AppliesTo(target))
        {
            return 0;
        }

        /* Watermark = the host store's latest already-collected value of the definition's time
           column (Darling reads Postgres here instead) — feeds server-side filters + client dedup. */
        DateTime? watermark = definition.WatermarkColumn is null
            ? null
            : await GetLastCollectedTimeAsync(serverId, definition.TargetTable, definition.WatermarkColumn, cancellationToken);

        var context = new CollectorContext
        {
            ServerId = serverId,
            ServerName = GetServerNameForStorage(server),
            CollectionTime = collectionTime,
            Deltas = _deltaCalculator,
            Target = target,
            Watermark = watermark,
            IgnoredWaitTypes = _ignoredWaitTypes.Value,
        };

        var plan = definition.BuildQuery(context);

        var sqlSw = Stopwatch.StartNew();
        using var sqlConnection = await CreateConnectionAsync(server, cancellationToken);
        using var command = new SqlCommand(plan.Text, sqlConnection);
        command.CommandTimeout = CommandTimeoutSeconds;

        foreach (var parameter in plan.Parameters)
        {
            command.Parameters.Add(new SqlParameter(parameter.Name, ToSqlDbType(parameter.Type))
            {
                Value = parameter.Value ?? DBNull.Value,
            });
        }

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = await definition.ReadAsync(reader, context, cancellationToken);
        sqlSw.Stop();
        _lastSqlMs = sqlSw.ElapsedMilliseconds;

        var duckSw = Stopwatch.StartNew();
        var rowsWritten = 0;

        using (var duckConnection = _duckDb.CreateConnection())
        {
            await duckConnection.OpenAsync(cancellationToken);

            using (var appender = duckConnection.CreateAppender(definition.TargetTable))
            {
                var writer = new AppenderCollectorRowWriter();

                foreach (var item in rows)
                {
                    var row = appender.CreateRow();
                    row.AppendValue(GenerateCollectionId())     /* collection_id BIGINT */
                       .AppendValue(collectionTime)             /* collection_time TIMESTAMP */
                       .AppendValue(serverId)                   /* server_id INTEGER */
                       .AppendValue(context.ServerName);        /* server_name VARCHAR */

                    writer.CurrentRow = row;
                    definition.WritePayload(item, writer, context);
                    row.EndRow();

                    rowsWritten++;
                }
            }
        }

        duckSw.Stop();
        _lastDuckDbMs = duckSw.ElapsedMilliseconds;

        _logger?.LogDebug("Collected {RowCount} {Collector} rows for server '{Server}'", rowsWritten, definition.Name, server.DisplayName);
        return rowsWritten;
    }

    private static SqlDbType ToSqlDbType(CollectorParameterType type) => type switch
    {
        CollectorParameterType.DateTime2 => SqlDbType.DateTime2,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unmapped collector parameter type"),
    };
}
