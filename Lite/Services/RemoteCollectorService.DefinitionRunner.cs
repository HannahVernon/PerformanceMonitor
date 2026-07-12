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
            IsAwsRds = status.IsAwsRds,
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

        /* Numeric (bigint) watermark = the host store's latest already-collected value of the definition's
           monotonic identity column (job_history's instance_id) — the bigint twin of the timestamp watermark
           above, for exact-and-complete dedup that survives server-side purges. Null for every collector that
           declares no numeric watermark (the common case), so no extra query runs for them. */
        long? numericWatermark = definition.NumericWatermarkColumn is null
            ? null
            : await GetLastCollectedInstanceIdAsync(serverId, definition.TargetTable, definition.NumericWatermarkColumn, cancellationToken);

        /* Only when the watermark came back null (hot store empty): tell a TRUE first run from a store merely
           emptied by archival, so a definition like default_trace_events doesn't re-scan source data already
           in the parquet archive (CollectorContext.HasCollectedBefore). Skipped in the common (non-null
           watermark) path — no extra query. */
        bool hasCollectedBefore = definition.WatermarkColumn is not null
            && watermark is null
            && await HasPriorCollectorSuccessAsync(serverId, definition.Name, cancellationToken);

        var context = new CollectorContext
        {
            ServerId = serverId,
            ServerName = GetServerNameForStorage(server),
            CollectionTime = collectionTime,
            Deltas = _deltaCalculator,
            Target = target,
            Watermark = watermark,
            NumericWatermark = numericWatermark,
            HasCollectedBefore = hasCollectedBefore,
            IgnoredWaitTypes = _ignoredWaitTypes.Value,
            ExcludedDatabases = server.ExcludedDatabases?.ToArray() ?? Array.Empty<string>(),
            PerfmonCounterOverride = GetPerfmonCounterOverride(),
        };

        var sqlSw = Stopwatch.StartNew();
        List<TRow> rows;

        if (definition.RunsPerDatabase(context.Target))
        {
            /* Azure SQL DB scopes some DMVs to the connected database — run the query once per
               database, skipping (and debug-logging) databases that error, matching the original
               hand-rolled collectors. */
            var plan = definition.BuildQuery(context);
            var commandTimeout = definition.CommandTimeoutSecondsOverride ?? CommandTimeoutSeconds;
            rows = new List<TRow>();
            var databases = await GetAzureDatabaseListAsync(server, cancellationToken);

            foreach (var databaseName in databases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var dbConnection = await OpenAzureDatabaseConnectionAsync(server, databaseName, cancellationToken);
                    using var dbCommand = CreateCollectorCommand(plan, dbConnection, commandTimeout);
                    using var dbReader = await dbCommand.ExecuteReaderAsync(cancellationToken);
                    rows.AddRange(await definition.ReadAsync(dbReader, context, cancellationToken));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger?.LogDebug("Skipping database '{Database}' for {Collector}: {Error}", databaseName, definition.Name, ex.Message);
                }
            }
        }
        else
        {
            using var sqlConnection = await CreateConnectionAsync(server, cancellationToken);

            var enumerationPlan = definition.BuildEnumerationQuery(context);
            if (enumerationPlan is not null)
            {
                /* Enumeration shape (the [db].sys.sp_executesql idiom): list items first, then
                   run one query per item ON THE SAME CONNECTION; an item that fails with a
                   SqlException is skipped with a warning, matching the original collectors. */
                var items = new List<string>();
                /* Enumeration always uses the host default timeout, matching the originals —
                   the per-collector override applies only to the heavy per-item commands. */
                using (var enumerationCommand = CreateCollectorCommand(enumerationPlan, sqlConnection, CommandTimeoutSeconds))
                using (var enumerationReader = await enumerationCommand.ExecuteReaderAsync(cancellationToken))
                {
                    while (await enumerationReader.ReadAsync(cancellationToken))
                    {
                        items.Add(enumerationReader.GetString(0));
                    }
                }

                if (items.Count == 0)
                {
                    /* No items → no storage phase, matching the original's early return. */
                    return 0;
                }

                /* Optional quick scalar probe (e.g. query_store's live PRODUCTVERSION check,
                   deliberately probed per cycle rather than trusting cached status). Best-effort
                   on a 10-second budget, matching the original; failure leaves the definition on
                   its documented default via a null EnumerationProbeResult. */
                var probePlan = definition.BuildEnumerationProbe(context);
                if (probePlan is not null)
                {
                    try
                    {
                        using var probeCommand = CreateCollectorCommand(probePlan, sqlConnection, 10);
                        var probeResult = await probeCommand.ExecuteScalarAsync(cancellationToken);
                        if (probeResult is not null && probeResult != DBNull.Value)
                        {
                            context.EnumerationProbeResult = probeResult;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogDebug("Enumeration probe for {Collector} failed; using defaults: {Error}",
                            definition.Name, ex.Message);
                    }
                }

                var itemTimeout = definition.CommandTimeoutSecondsOverride ?? CommandTimeoutSeconds;
                rows = new List<TRow>();
                foreach (var item in items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var itemCommand = CreateCollectorCommand(definition.BuildPerItemQuery(item, context), sqlConnection, itemTimeout);
                        using var itemReader = await itemCommand.ExecuteReaderAsync(cancellationToken);
                        await definition.ReadItemAsync(item, itemReader, rows, context, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger?.LogWarning("Failed to collect {Collector} from [{Database}] on '{Server}': {Message}",
                            definition.Name, item, server.DisplayName, ex.Message);
                    }
                }

            }
            else
            {
                var plan = definition.BuildQuery(context);
                using (var command = CreateCollectorCommand(plan, sqlConnection, definition.CommandTimeoutSecondsOverride ?? CommandTimeoutSeconds))
                using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    rows = await definition.ReadAsync(reader, context, cancellationToken);
                }

                /* Optional best-effort second query on the same connection (e.g. server_properties'
                   WS5 health probe). Failure-isolated: it can never fail the primary rows. Skipped
                   when the primary produced no rows, matching the originals (which only ran their
                   second query after a successful primary read). */
                var supplementalPlan = definition.BuildSupplementalQuery(context);
                if (supplementalPlan is not null && rows.Count > 0)
                {
                    try
                    {
                        using var supplementalCommand = CreateCollectorCommand(supplementalPlan, sqlConnection, CommandTimeoutSeconds);
                        using var supplementalReader = await supplementalCommand.ExecuteReaderAsync(cancellationToken);
                        await definition.ApplySupplementalAsync(rows, supplementalReader, context, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Supplemental query for {Collector} failed; continuing without it", definition.Name);
                    }
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

                    if (definition.IncludesCollectionId)
                    {
                        row.AppendValue(GenerateCollectionId()); /* collection_id BIGINT */
                    }

                    row.AppendValue(collectionTime)              /* collection_time TIMESTAMP */
                       .AppendValue(serverId)                    /* server_id INTEGER */
                       .AppendValue(context.ServerName);         /* server_name VARCHAR */

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

    private static SqlCommand CreateCollectorCommand(CollectorQuery plan, SqlConnection connection, int commandTimeoutSeconds)
    {
        var command = new SqlCommand(plan.Text, connection) { CommandTimeout = commandTimeoutSeconds };

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
        CollectorParameterType.Int32 => new SqlParameter(parameter.Name, SqlDbType.Int) { Value = parameter.Value ?? DBNull.Value },
        CollectorParameterType.BigInt => new SqlParameter(parameter.Name, SqlDbType.BigInt) { Value = parameter.Value ?? DBNull.Value },
        _ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter.Type, "Unmapped collector parameter type"),
    };
}
