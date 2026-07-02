/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DuckDB.NET.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Models;

namespace PerformanceMonitorLite.Services;

public partial class RemoteCollectorService
{
    /// <summary>
    /// Collects server configuration via the shared <see cref="ServerConfigCollector"/> definition.
    /// On-load only, not scheduled.
    /// </summary>
    private Task<int> CollectServerConfigAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(ServerConfigCollector.Instance, server, cancellationToken);

    /// <summary>
    /// Collects database configuration via the shared <see cref="DatabaseConfigCollector"/>
    /// definition (the 2019/2025 version gates and the exclusion-filter splice live there —
    /// the cross-SKU parity contract). On-load only, not scheduled.
    /// </summary>
    private Task<int> CollectDatabaseConfigAsync(ServerConnection server, CancellationToken cancellationToken)
        => RunCollectorDefinitionAsync(DatabaseConfigCollector.Instance, server, cancellationToken);

    /// <summary>
    /// Collects active trace flags via the shared <see cref="TraceFlagsCollector"/> definition.
    /// Wrapped in a permission-tolerant catch — DBCC may be denied — so a failure degrades to
    /// zero rows with a warning, exactly as the original collector did. On-load only.
    /// </summary>
    private async Task<int> CollectTraceFlagsAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        try
        {
            return await RunCollectorDefinitionAsync(TraceFlagsCollector.Instance, server, cancellationToken);
        }
        catch (SqlException ex)
        {
            _logger?.LogWarning("Failed to collect trace flags on '{Server}' (may lack DBCC permissions): {Message}",
                server.DisplayName, ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// Collects database-scoped configurations from sys.database_scoped_configurations
    /// for each online user database. On-load only, not scheduled.
    /// </summary>
    private async Task<int> CollectDatabaseScopedConfigAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        var serverStatus = _serverManager.GetConnectionStatus(server.Id);
        bool isAzureSqlDb = serverStatus?.SqlEngineEdition == 5;

        string onPremDbQuery = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    d.name
FROM sys.databases AS d
LEFT JOIN sys.dm_hadr_database_replica_states AS drs
    ON d.database_id = drs.database_id
    AND drs.is_local = 1
WHERE (d.database_id > 4 OR d.database_id = 2)
AND   d.database_id < 32761
AND   d.name <> N'PerformanceMonitor'
AND   d.state_desc = N'ONLINE'
AND
(
    drs.database_id IS NULL          /*not in any AG*/
    OR drs.is_primary_replica = 1    /*primary replica*/
)
/*EXCLUSION_FILTER*/
ORDER BY d.name
OPTION(RECOMPILE);";

        string azureDbQuery = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    d.name
FROM sys.databases AS d
WHERE (d.database_id > 4 OR d.database_id = 2)
AND   d.database_id < 32761
AND   d.name <> N'PerformanceMonitor'
AND   d.state_desc = N'ONLINE'
/*EXCLUSION_FILTER*/
ORDER BY d.name
OPTION(RECOMPILE);";

        var (scopedExclusionClause, _) = BuildDatabaseExclusionFilter(server.ExcludedDatabases, "d.name");
        onPremDbQuery = onPremDbQuery.Replace("/*EXCLUSION_FILTER*/", scopedExclusionClause);
        azureDbQuery = azureDbQuery.Replace("/*EXCLUSION_FILTER*/", scopedExclusionClause);

        string dbQuery = isAzureSqlDb ? azureDbQuery : onPremDbQuery;

        var serverId = GetServerId(server);
        var captureTime = DateTime.UtcNow;
        var totalRows = 0;
        _lastSqlMs = 0;
        _lastDuckDbMs = 0;

        var sqlSw = Stopwatch.StartNew();
        using var sqlConnection = await CreateConnectionAsync(server, cancellationToken);

        /* Get list of databases */
        var databases = new List<string>();
        using (var dbCommand = new SqlCommand(dbQuery, sqlConnection))
        {
            dbCommand.CommandTimeout = CommandTimeoutSeconds;
            var (_, scopedExclusionParams) = BuildDatabaseExclusionFilter(server.ExcludedDatabases, "d.name");
            foreach (var p in scopedExclusionParams) dbCommand.Parameters.Add(p);
            using var dbReader = await dbCommand.ExecuteReaderAsync(cancellationToken);
            while (await dbReader.ReadAsync(cancellationToken))
            {
                databases.Add(dbReader.GetString(0));
            }
        }

        if (databases.Count == 0)
        {
            return 0;
        }

        /* Collect all scoped configs from SQL Server first */
        var scopedRows = new List<(string DbName, string ConfigName, string? Value, string? ValueForSecondary)>();

        foreach (var dbName in databases)
        {
            try
            {
                /* Use [dbname].sys.sp_executesql to run in database context (Azure SQL DB compatible) */
                var scopedQuery = $@"
EXECUTE [{dbName.Replace("]", "]]")}].sys.sp_executesql
    N'SELECT
         configuration_name = dsc.name,
         value = CONVERT(nvarchar(256), dsc.value),
         value_for_secondary = CONVERT(nvarchar(256), dsc.value_for_secondary)
     FROM sys.database_scoped_configurations AS dsc
     ORDER BY dsc.name
     OPTION(RECOMPILE);'";

                using var cmd = new SqlCommand(scopedQuery, sqlConnection);
                cmd.CommandTimeout = CommandTimeoutSeconds;

                using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    scopedRows.Add((
                        dbName,
                        reader.GetString(0),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
            }
            catch (SqlException ex)
            {
                _logger?.LogWarning("Failed to collect scoped config from [{Database}] on '{Server}': {Message}",
                    dbName, server.DisplayName, ex.Message);
            }
        }

        sqlSw.Stop();

        /* Write to DuckDB using appender */
        var duckSw = Stopwatch.StartNew();
        using var duckConnection = _duckDb.CreateConnection();
        await duckConnection.OpenAsync(cancellationToken);

        using var appender = duckConnection.CreateAppender("database_scoped_config");
        foreach (var (dbName, configName, value, valueForSecondary) in scopedRows)
        {
            var row = appender.CreateRow();
            row.AppendValue(GenerateCollectionId())
               .AppendValue(captureTime)
               .AppendValue(serverId)
               .AppendValue(GetServerNameForStorage(server))
               .AppendValue(dbName)
               .AppendValue(configName)
               .AppendValue(value)
               .AppendValue(valueForSecondary)
               .EndRow();
            totalRows++;
        }

        duckSw.Stop();
        _lastSqlMs = sqlSw.ElapsedMilliseconds;
        _lastDuckDbMs = duckSw.ElapsedMilliseconds;

        _logger?.LogDebug("Collected {RowCount} database scoped config rows across {DbCount} databases for server '{Server}'",
            totalRows, databases.Count, server.DisplayName);
        return totalRows;
    }
}
