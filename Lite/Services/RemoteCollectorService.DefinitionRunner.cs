/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
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
        var target = new CollectorTargetInfo
        {
            IsAzureSqlDb = status.SqlEngineEdition == 5,
            IsAzureManagedInstance = status.SqlEngineEdition == 8,
            SqlMajorVersion = status.SqlMajorVersion,
        };

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
            ExcludedDatabases = server.ExcludedDatabases?.ToArray() ?? Array.Empty<string>(),
        };

        var plan = definition.BuildQuery(context);

        var sqlSw = Stopwatch.StartNew();
        List<TRow> rows;

        if (definition.RunsPerDatabase(context.Target))
        {
            /* Azure SQL DB scopes some DMVs to the connected database — run the query once per
               database, skipping (and debug-logging) databases that error, matching the original
               hand-rolled collectors. */
            rows = new List<TRow>();
            var databases = await GetAzureDatabaseListAsync(server, cancellationToken);

            foreach (var databaseName in databases)
            {
                try
                {
                    using var dbConnection = await OpenAzureDatabaseConnectionAsync(server, databaseName, cancellationToken);
                    using var dbCommand = CreateCollectorCommand(plan, dbConnection);
                    using var dbReader = await dbCommand.ExecuteReaderAsync(cancellationToken);
                    rows.AddRange(await definition.ReadAsync(dbReader, context, cancellationToken));
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug("Skipping database '{Database}' for {Collector}: {Error}", databaseName, definition.Name, ex.Message);
                }
            }
        }
        else
        {
            using var sqlConnection = await CreateConnectionAsync(server, cancellationToken);
            using (var command = CreateCollectorCommand(plan, sqlConnection))
            using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                rows = await definition.ReadAsync(reader, context, cancellationToken);
            }

            /* Optional best-effort second query on the same connection (e.g. server_properties'
               WS5 health probe). Failure-isolated: it can never fail the primary rows. */
            var supplementalPlan = definition.BuildSupplementalQuery(context);
            if (supplementalPlan is not null)
            {
                try
                {
                    using var supplementalCommand = CreateCollectorCommand(supplementalPlan, sqlConnection);
                    using var supplementalReader = await supplementalCommand.ExecuteReaderAsync(cancellationToken);
                    await definition.ApplySupplementalAsync(rows, supplementalReader, context, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Supplemental query for {Collector} failed; continuing without it", definition.Name);
                }
            }
        }

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

    private static SqlCommand CreateCollectorCommand(CollectorQuery plan, SqlConnection connection)
    {
        var command = new SqlCommand(plan.Text, connection) { CommandTimeout = CommandTimeoutSeconds };

        foreach (var parameter in plan.Parameters)
        {
            command.Parameters.Add(ToSqlParameter(parameter));
        }

        return command;
    }

    private static SqlParameter ToSqlParameter(CollectorParameter parameter) => parameter.Type switch
    {
        CollectorParameterType.DateTime2 => new SqlParameter(parameter.Name, SqlDbType.DateTime2) { Value = parameter.Value ?? DBNull.Value },
        CollectorParameterType.NVarChar128 => new SqlParameter(parameter.Name, SqlDbType.NVarChar, 128) { Value = parameter.Value ?? DBNull.Value },
        _ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter.Type, "Unmapped collector parameter type"),
    };
}
