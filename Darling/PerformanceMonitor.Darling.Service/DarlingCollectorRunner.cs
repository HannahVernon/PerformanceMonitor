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

    /* Feeds CollectorContext.CapturePlanXml on every cycle — the query_stats / query_store
       collectors capture the execution plan when true (darling.json "capturePlans", default true).
       Lite never sets the context flag; this is what makes Darling the plan-capturing SKU. Read
       through a provider (not a captured bool) so a control-plane store reload of config_service's
       capture_plans is honored on the NEXT cycle without reconstructing the runner. */
    private readonly Func<bool> _capturePlans;

    /* Feeds CollectorContext.CollectSchemaChangeEvents on every cycle — the default_trace_events
       collector drops its Object:Created/Altered/Deleted (schema DDL) slice when false (darling.json
       "collectSchemaChangeEvents", default true). Lite never sets the context flag, so it keeps
       collecting Object DDL. Read through a provider (not a captured bool) for symmetry with
       _capturePlans, so a future live reload is honored on the NEXT cycle without rebuilding. */
    private readonly Func<bool> _collectSchemaChanges;

    /* Azure SQL DB logins without master access fall back to single-database mode, throttled per
       server so master isn't retried every cycle (#857 — mirrors Lite).

       Stores WHEN the verdict was formed, not just that it was: it expires after
       AzureMasterRecheckInterval, and OnServerReconnected drops it outright. Both escape hatches
       exist because this used to latch until the process was restarted, so a transient Azure error
       could permanently demote a healthy server to single-database collection (#1506). */
    private readonly ConcurrentDictionary<int, DateTime> _azureMasterInaccessibleSince = new();

    private static readonly TimeSpan AzureMasterRecheckInterval = TimeSpan.FromMinutes(15);

    public const int CommandTimeoutSeconds = 60;

    /// <param name="capturePlans">
    /// Live provider for the plan-capture flag; null defaults to always-on (Darling's SKU default).
    /// The worker passes <c>() =&gt; config.CapturePlans</c> so a store reload takes effect next cycle;
    /// tests pass a constant lambda.
    /// </param>
    /// <param name="collectSchemaChanges">
    /// Live provider for the schema-change (Object DDL) collection flag; null defaults to on (today's
    /// behavior). The worker passes <c>() =&gt; config.CollectSchemaChangeEvents</c> so a noisy/benchmark box
    /// can suppress the default-trace Object:Created/Deleted flood; tests pass a constant lambda.
    /// </param>
    public DarlingCollectorRunner(NpgsqlDataSource postgres, CollectorDeltaCalculator deltas, ILogger? logger = null, Func<bool>? capturePlans = null, Func<bool>? collectSchemaChanges = null)
    {
        _postgres = postgres ?? throw new ArgumentNullException(nameof(postgres));
        _deltas = deltas ?? throw new ArgumentNullException(nameof(deltas));
        _logger = logger;
        _capturePlans = capturePlans ?? (() => true);
        _collectSchemaChanges = collectSchemaChanges ?? (() => true);
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

        /* Numeric (bigint) watermark = the newest already-collected value of the definition's monotonic
           identity column (job_history's instance_id), read from Postgres — the bigint twin of the timestamp
           watermark above. Null for every collector that declares no numeric watermark (the common case),
           so no extra query runs for them. */
        long? numericWatermark = definition.NumericWatermarkColumn is null
            ? null
            : await GetLastCollectedInstanceIdAsync(server.ServerId, definition.TargetTable, definition.NumericWatermarkColumn, cancellationToken);

        /* Only when the watermark came back null: tell a TRUE first run from a store merely emptied by
           retention, so default_trace_events uses a bounded window instead of re-scanning all .trc history
           (CollectorContext.HasCollectedBefore). Skipped in the common (non-null watermark) path. */
        bool hasCollectedBefore = definition.WatermarkColumn is not null
            && watermark is null
            && await HasPriorCollectorSuccessAsync(server.ServerId, definition.Name, cancellationToken);

        var context = new CollectorContext
        {
            ServerId = server.ServerId,
            ServerName = server.StorageName,
            CollectionTime = collectionTime,
            Deltas = _deltas,
            Target = server.Target,
            Watermark = watermark,
            NumericWatermark = numericWatermark,
            HasCollectedBefore = hasCollectedBefore,
            IgnoredWaitTypes = IgnoredWaitDefaults.All,
            ExcludedDatabases = server.Config.ExcludedDatabases?.ToArray() ?? Array.Empty<string>(),
            PerfmonCounterOverride = null,
            CapturePlanXml = _capturePlans(),
            CollectSchemaChangeEvents = _collectSchemaChanges(),
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
    /// Gets the most recent value of a monotonic bigint identity column from Postgres for incremental
    /// collection — the numeric twin of <see cref="GetLastCollectedTimeAsync"/> (job_history dedups on
    /// <c>instance_id</c>, sysjobhistory's IDENTITY bigint). Returns null on first run or if the query
    /// fails (caller uses its documented first-run/fallback path).
    /// </summary>
    public async Task<long?> GetLastCollectedInstanceIdAsync(
        int serverId, string tableName, string columnName, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(
                $"SELECT MAX({columnName}) FROM {tableName} WHERE server_id = $1", connection);
            command.Parameters.AddWithValue(serverId);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is not null && result != DBNull.Value)
            {
                return Convert.ToInt64(result);
            }
        }
        catch
        {
            /* If the Postgres query fails, caller uses fallback window */
        }
        return null;
    }

    /// <summary>
    /// Whether a prior SUCCESS row exists in collection_log for this collector+server — the "has collected
    /// before" signal (<see cref="CollectorContext.HasCollectedBefore"/>), consulted only when the watermark
    /// is null. Returns false on any failure, which errs toward the all-history first run (correct for a
    /// genuinely fresh store).
    /// </summary>
    public async Task<bool> HasPriorCollectorSuccessAsync(int serverId, string collectorName, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(
                "SELECT EXISTS(SELECT 1 FROM collection_log WHERE server_id = $1 AND collector_name = $2 AND status = 'SUCCESS')", connection);
            command.Parameters.AddWithValue(serverId);
            command.Parameters.AddWithValue(collectorName);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is bool b && b;
        }
        catch
        {
            /* Fail toward first-run (all-history) — matches a fresh store with no log yet. */
            return false;
        }
    }

    /// <summary>
    /// Drops any cached master-inaccessible verdict for a server that has just reconnected.
    ///
    /// Azure SQL DB reports "this login may not read master" and "you cannot reach this server right
    /// now" with overlapping error numbers, so a verdict formed while a server was failing is not
    /// trustworthy. The moment it answers again is the moment to discard that verdict and re-probe.
    /// Without this, a transient outage permanently misfiles a login that CAN read master, and
    /// database-scoped collection stays degraded until the service restarts (#1506).
    /// </summary>
    public void OnServerReconnected(int serverId)
    {
        if (_azureMasterInaccessibleSince.TryRemove(serverId, out _))
        {
            _logger?.LogInformation("[server_id {ServerId}] reconnected — re-probing master for database-scoped collectors.", serverId);
        }
    }

    /// <summary>
    /// Lists databases on an Azure SQL DB logical server, mirroring Lite's #857 behavior: try
    /// master enumeration first (with the per-server exclusion filter), and on a master-access
    /// error fall back to the connection's own database, throttling re-probes per server.
    /// </summary>
    private async Task<List<string>> GetAzureDatabaseListAsync(ServerRuntime server, CancellationToken cancellationToken)
    {
        var targetDb = new SqlConnectionStringBuilder(server.ConnectionString).InitialCatalog;

        if (IsMasterProbeThrottled(server.ServerId))
        {
            return FallbackDatabaseList(server, targetDb, reason: "master previously inaccessible");
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

            _azureMasterInaccessibleSince.TryRemove(server.ServerId, out _);
            return databases;
        }
        catch (SqlException ex) when (IsMasterAccessDeniedError(ex))
        {
            _azureMasterInaccessibleSince[server.ServerId] = DateTime.UtcNow;

            return FallbackDatabaseList(server, targetDb, reason: $"master DB inaccessible (SQL error {ex.Number})");
        }
    }

    /// <summary>
    /// True while a recent master-inaccessible verdict still stands. It expires so a server whose
    /// access was restored recovers on its own rather than staying degraded until restart (#1506).
    /// </summary>
    private bool IsMasterProbeThrottled(int serverId)
    {
        if (!_azureMasterInaccessibleSince.TryGetValue(serverId, out var deniedAt))
        {
            return false;
        }

        if (DateTime.UtcNow - deniedAt < AzureMasterRecheckInterval)
        {
            return true;
        }

        _azureMasterInaccessibleSince.TryRemove(serverId, out _);
        return false;
    }

    /// <summary>
    /// The database list to use when master cannot be enumerated: the connection's own catalog.
    ///
    /// When there isn't one, database-scoped collectors have nowhere to read from. That used to be a
    /// warning and an empty list, which made every one of them report success having collected zero
    /// rows. Throwing puts the failure where it can actually be seen (#1506).
    /// </summary>
    private List<string> FallbackDatabaseList(ServerRuntime server, string? targetDb, string reason)
    {
        var fallback = SingleDbOrEmpty(targetDb);

        if (fallback.Count == 0)
        {
            throw new InvalidOperationException(
                $"{reason}, and this connection has no target database to fall back to (it resolves to " +
                $"master). Set a database for '{server.Config.DisplayName}' so database-scoped collectors " +
                $"have something to read.");
        }

        _logger?.LogInformation("[{Server}] {Reason} — collecting from '{Database}' only.",
            server.Config.DisplayName, reason, targetDb);
        return fallback;
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
    /// <summary>
    /// Error numbers meaning this login cannot read master on Azure SQL DB.
    ///
    /// These must be about the login's *rights*, never the server's *reachability*. 40613 ("not
    /// currently available — retry later") and 40615 ("client IP is not allowed to access the
    /// server") were on this list and are the cause of #1506: both are temporary conditions, and
    /// treating them as a permanent verdict about master left collection degraded until restart.
    /// A firewall rejection in particular says nothing about master — and falling back to a user
    /// database is pointless, since the same rule blocks that connection too.
    ///
    /// 4060 and 18456 stay: a contained user that exists only in a user database really does get
    /// them when opening master (#857). The verdict they produce now expires, because they can be
    /// thrown transiently as well.
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
        CollectorParameterType.Int32 => new SqlParameter(parameter.Name, SqlDbType.Int) { Value = parameter.Value ?? DBNull.Value },
        CollectorParameterType.BigInt => new SqlParameter(parameter.Name, SqlDbType.BigInt) { Value = parameter.Value ?? DBNull.Value },
        _ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter.Type, "Unmapped collector parameter type"),
    };
}
