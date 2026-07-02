/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Npgsql;
using PerformanceMonitor.Collectors;
using PerformanceMonitor.Darling.Storage;

namespace PerformanceMonitor.Darling.Service;

/// <summary>Per-run outcome the worker logs (mirrors Lite's fetch/store phase split, #1180).</summary>
public sealed record CollectorRunResult(int Rows, long SqlMs, long StorageMs);

/// <summary>
/// Runs a shared collector definition against one monitored server and binary-COPYs the rows
/// into Postgres — the Darling counterpart of Lite's RemoteCollectorService.DefinitionRunner,
/// ported semantics-for-semantics: AppliesTo skip, host-store watermarks, the three execution
/// paths (per-database Azure connections; enumeration with the optional scalar probe; plain
/// single query with best-effort supplemental), cancellation-aware per-item catches, and the
/// separated SQL/storage phase timing. The definitions and the delta/ignored-wait/schedule
/// defaults are the shared brain; only the storage engine differs.
/// </summary>
public sealed class DarlingCollectorRunner
{
    private readonly NpgsqlDataSource _postgres;
    private readonly CollectorDeltaCalculator _deltas;
    private readonly ILogger? _logger;

    /* Azure SQL DB logins without master access fall back to single-database mode, cached per
       server so master isn't retried every cycle (#857 — mirrors Lite). */
    private readonly ConcurrentDictionary<int, bool> _azureMasterInaccessible = new();

    public const int CommandTimeoutSeconds = 60;

    public DarlingCollectorRunner(NpgsqlDataSource postgres, CollectorDeltaCalculator deltas, ILogger? logger = null)
    {
        _postgres = postgres ?? throw new ArgumentNullException(nameof(postgres));
        _deltas = deltas ?? throw new ArgumentNullException(nameof(deltas));
        _logger = logger;
    }

    public async Task<CollectorRunResult> RunAsync<TRow>(
        ICollectorDefinition<TRow> definition,
        ServerRuntime server,
        CancellationToken cancellationToken)
    {
        var collectionTime = DateTime.UtcNow;

        /* Some collectors don't exist on some targets (e.g. ring buffers on Azure SQL DB) —
           skip the cycle entirely, matching Lite. */
        if (!definition.AppliesTo(server.Target))
        {
            return new CollectorRunResult(0, 0, 0);
        }

        /* Watermark = the newest already-collected value of the definition's time column,
           read from Postgres (Lite reads DuckDB here). */
        DateTime? watermark = definition.WatermarkColumn is null
            ? null
            : await GetLastCollectedTimeAsync(server.ServerId, definition.TargetTable, definition.WatermarkColumn, cancellationToken);

        var context = new CollectorContext
        {
            ServerId = server.ServerId,
            ServerName = server.StorageName,
            CollectionTime = collectionTime,
            Deltas = _deltas,
            Target = server.Target,
            Watermark = watermark,
            IgnoredWaitTypes = IgnoredWaitDefaults.All,
            ExcludedDatabases = server.Config.ExcludedDatabases?.ToArray() ?? Array.Empty<string>(),
            PerfmonCounterOverride = null,
        };

        var sqlSw = Stopwatch.StartNew();
        List<TRow> rows;

        if (definition.RunsPerDatabase(context.Target))
        {
            /* Azure SQL DB scopes some DMVs to the connected database — run the query once per
               database, skipping (and debug-logging) databases that error, matching Lite. */
            var plan = definition.BuildQuery(context);
            rows = new List<TRow>();
            var databases = await GetAzureDatabaseListAsync(server, cancellationToken);

            foreach (var databaseName in databases)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var dbConnection = await OpenAzureDatabaseConnectionAsync(server, databaseName, cancellationToken);
                    using var dbCommand = CreateCollectorCommand(plan, dbConnection, CommandTimeoutSeconds);
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
            using var sqlConnection = new SqlConnection(server.ConnectionString);
            await sqlConnection.OpenAsync(cancellationToken);

            var enumerationPlan = definition.BuildEnumerationQuery(context);
            if (enumerationPlan is not null)
            {
                /* Enumeration shape (the [db].sys.sp_executesql idiom): list items first, then
                   run one query per item ON THE SAME CONNECTION; an item that fails is skipped
                   with a warning, matching Lite. */
                var items = new List<string>();
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
                    return new CollectorRunResult(0, sqlSw.ElapsedMilliseconds, 0);
                }

                /* Optional quick scalar probe (query_store's live PRODUCTVERSION check) —
                   best-effort on a 10-second budget; failure leaves the documented default. */
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
                            definition.Name, item, server.Config.DisplayName, ex.Message);
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

                /* Optional best-effort second query on the same connection (server_properties'
                   health probe). Failure-isolated; skipped on an empty primary, matching Lite. */
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

        var storageSw = Stopwatch.StartNew();
        var rowsWritten = 0;

        await using (var pgConnection = await _postgres.OpenConnectionAsync(cancellationToken))
        {
            var writer = new PgCollectorRowWriter();
            using var importer = await pgConnection.BeginBinaryImportAsync(
                PgCollectorRowWriter.CopyCommandFor(definition), cancellationToken);
            writer.Importer = importer;

            /* Naive-UTC storage — see PgCollectorRowWriter. */
            var storedCollectionTime = DateTime.SpecifyKind(collectionTime, DateTimeKind.Unspecified);

            foreach (var row in rows)
            {
                await importer.StartRowAsync(cancellationToken);

                if (definition.IncludesCollectionId)
                {
                    writer.Value(CollectionIdGenerator.Next());
                }

                writer.Value(storedCollectionTime)
                      .Value(server.ServerId)
                      .Value(server.StorageName);

                definition.WritePayload(row, writer, context);
                rowsWritten++;
            }

            await importer.CompleteAsync(cancellationToken);
        }

        storageSw.Stop();

        _logger?.LogDebug("Collected {RowCount} {Collector} rows for server '{Server}'",
            rowsWritten, definition.Name, server.Config.DisplayName);
        return new CollectorRunResult(rowsWritten, sqlSw.ElapsedMilliseconds, storageSw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Gets the most recent value of a timestamp column from Postgres for incremental collection.
    /// Returns null on first run or if the query fails (caller uses a fallback window) — the
    /// Postgres twin of Lite's GetLastCollectedTimeAsync.
    /// </summary>
    public async Task<DateTime?> GetLastCollectedTimeAsync(
        int serverId, string tableName, string columnName, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(
                $"SELECT MAX({columnName}) FROM {tableName} WHERE server_id = $1", connection);
            command.Parameters.AddWithValue(serverId);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is DateTime dt)
            {
                return dt;
            }
        }
        catch
        {
            /* If the Postgres query fails, caller uses fallback window */
        }
        return null;
    }

    /// <summary>
    /// Lists databases on an Azure SQL DB logical server, mirroring Lite's #857 behavior: try
    /// master enumeration first (with the per-server exclusion filter), and on a master-access
    /// error fall back to the connection's own database, caching that decision per server.
    /// </summary>
    private async Task<List<string>> GetAzureDatabaseListAsync(ServerRuntime server, CancellationToken cancellationToken)
    {
        var targetDb = new SqlConnectionStringBuilder(server.ConnectionString).InitialCatalog;

        if (_azureMasterInaccessible.TryGetValue(server.ServerId, out var knownInaccessible) && knownInaccessible)
        {
            return SingleDbOrEmpty(targetDb);
        }

        var masterConnectionString = new SqlConnectionStringBuilder(server.ConnectionString)
        {
            InitialCatalog = "master",
        }.ConnectionString;

        var (exclusionClause, exclusionParameters) = DatabaseExclusionFilter.Build(
            server.Config.ExcludedDatabases, "name");

        var databases = new List<string>();
        try
        {
            using var connection = new SqlConnection(masterConnectionString);
            await connection.OpenAsync(cancellationToken);
            using var command = new SqlCommand(
                $"SELECT name FROM sys.databases WHERE state_desc = N'ONLINE' AND database_id > 0 {exclusionClause} ORDER BY name;",
                connection)
            { CommandTimeout = CommandTimeoutSeconds };
            foreach (var parameter in exclusionParameters)
            {
                command.Parameters.Add(ToSqlParameter(parameter));
            }
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                databases.Add(reader.GetString(0));
            }
            return databases;
        }
        catch (SqlException ex) when (IsMasterAccessDeniedError(ex))
        {
            _azureMasterInaccessible[server.ServerId] = true;

            var fallback = SingleDbOrEmpty(targetDb);
            if (fallback.Count > 0)
            {
                _logger?.LogInformation("[{Server}] master DB inaccessible (SQL error {Number}) — collecting from '{Database}' only.",
                    server.Config.DisplayName, ex.Number, targetDb);
            }
            else
            {
                _logger?.LogWarning("[{Server}] master DB inaccessible (SQL error {Number}) and no target database in connection string — no data will be collected for database-scoped collectors.",
                    server.Config.DisplayName, ex.Number);
            }
            return fallback;
        }
    }

    private async Task<SqlConnection> OpenAzureDatabaseConnectionAsync(ServerRuntime server, string databaseName, CancellationToken cancellationToken)
    {
        var connectionString = new SqlConnectionStringBuilder(server.ConnectionString)
        {
            InitialCatalog = databaseName,
        }.ConnectionString;

        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static List<string> SingleDbOrEmpty(string? targetDb)
    {
        if (string.IsNullOrEmpty(targetDb) || string.Equals(targetDb, "master", StringComparison.OrdinalIgnoreCase))
        {
            return new List<string>();
        }
        return new List<string> { targetDb };
    }

    /// <summary>
    /// Error numbers indicating the login cannot open or read from master on Azure SQL DB —
    /// trigger the single-database fallback (verbatim from Lite).
    /// </summary>
    private static bool IsMasterAccessDeniedError(SqlException ex)
    {
        return ex.Number switch
        {
            229 => true,   // Permission denied on object
            230 => true,   // Permission denied on column
            916 => true,   // Server principal is not able to access the database under the current security context
            4060 => true,  // Cannot open database requested by the login
            18456 => true, // Login failed for user
            40613 => true, // Database 'master' on server is not currently available
            40615 => true, // Cannot open server — login denied (firewall/auth)
            _ => false
        };
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
        _ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter.Type, "Unmapped collector parameter type"),
    };
}
