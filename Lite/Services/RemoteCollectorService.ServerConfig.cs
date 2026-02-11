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
using PerformanceMonitorLite.Models;

namespace PerformanceMonitorLite.Services;

public partial class RemoteCollectorService
{
    /// <summary>
    /// Collects server configuration from sys.configurations. On-load only, not scheduled.
    /// </summary>
    private async Task<int> CollectServerConfigAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        const string query = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    configuration_name = c.name,
    value_configured = CONVERT(bigint, c.value),
    value_in_use = CONVERT(bigint, c.value_in_use),
    is_dynamic = c.is_dynamic,
    is_advanced = c.is_advanced
FROM sys.configurations AS c
ORDER BY c.name
OPTION(RECOMPILE);";

        var serverId = GetServerId(server);
        var captureTime = DateTime.UtcNow;
        var rowsCollected = 0;
        _lastSqlMs = 0;
        _lastDuckDbMs = 0;

        /* Read all rows from SQL Server first to avoid holding appender open during SQL reads */
        var rows = new List<(string Name, long ValueConfigured, long ValueInUse, bool IsDynamic, bool IsAdvanced)>();

        var sqlSw = Stopwatch.StartNew();
        using var sqlConnection = await CreateConnectionAsync(server, cancellationToken);
        using var command = new SqlCommand(query, sqlConnection);
        command.CommandTimeout = CommandTimeoutSeconds;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4)));
        }
        sqlSw.Stop();

        /* Write to DuckDB using appender */
        var duckSw = Stopwatch.StartNew();
        using var duckConnection = _duckDb.CreateConnection();
        await duckConnection.OpenAsync(cancellationToken);

        using var appender = duckConnection.CreateAppender("server_config");
        foreach (var r in rows)
        {
            var row = appender.CreateRow();
            row.AppendValue(GenerateCollectionId())
               .AppendValue(captureTime)
               .AppendValue(serverId)
               .AppendValue(server.ServerName)
               .AppendValue(r.Name)
               .AppendValue(r.ValueConfigured)
               .AppendValue(r.ValueInUse)
               .AppendValue(r.IsDynamic)
               .AppendValue(r.IsAdvanced)
               .EndRow();
            rowsCollected++;
        }

        duckSw.Stop();
        _lastSqlMs = sqlSw.ElapsedMilliseconds;
        _lastDuckDbMs = duckSw.ElapsedMilliseconds;

        _logger?.LogDebug("Collected {RowCount} server config rows for server '{Server}'", rowsCollected, server.DisplayName);
        return rowsCollected;
    }

    /// <summary>
    /// Collects database configuration from sys.databases. On-load only, not scheduled.
    /// </summary>
    private async Task<int> CollectDatabaseConfigAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        const string query = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    database_name = d.name,
    compatibility_level = d.compatibility_level,
    recovery_model = d.recovery_model_desc,
    is_auto_close_on = d.is_auto_close_on,
    is_auto_shrink_on = d.is_auto_shrink_on,
    is_query_store_on = d.is_query_store_on,
    page_verify_option = d.page_verify_option_desc,
    target_recovery_time_seconds = d.target_recovery_time_in_seconds,
    delayed_durability = d.delayed_durability_desc
FROM sys.databases AS d
WHERE (d.database_id > 4 OR d.database_id = 2)
AND   d.database_id < 32761
AND   d.name <> N'PerformanceMonitor'
ORDER BY d.name
OPTION(RECOMPILE);";

        var serverId = GetServerId(server);
        var captureTime = DateTime.UtcNow;
        var rowsCollected = 0;
        _lastSqlMs = 0;
        _lastDuckDbMs = 0;

        var rows = new List<(string DbName, int CompatLevel, string? RecoveryModel, bool AutoClose, bool AutoShrink, bool QueryStore, string? PageVerify, int TargetRecovery, string? DelayedDurability)>();

        var sqlSw = Stopwatch.StartNew();
        using var sqlConnection = await CreateConnectionAsync(server, cancellationToken);
        using var command = new SqlCommand(query, sqlConnection);
        command.CommandTimeout = CommandTimeoutSeconds;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((
                reader.GetString(0),
                Convert.ToInt32(reader.GetValue(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                Convert.ToInt32(reader.GetValue(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        sqlSw.Stop();

        var duckSw = Stopwatch.StartNew();
        using var duckConnection = _duckDb.CreateConnection();
        await duckConnection.OpenAsync(cancellationToken);

        using var appender = duckConnection.CreateAppender("database_config");
        foreach (var r in rows)
        {
            var row = appender.CreateRow();
            row.AppendValue(GenerateCollectionId())
               .AppendValue(captureTime)
               .AppendValue(serverId)
               .AppendValue(server.ServerName)
               .AppendValue(r.DbName)
               .AppendValue(r.CompatLevel)
               .AppendValue(r.RecoveryModel)
               .AppendValue(r.AutoClose)
               .AppendValue(r.AutoShrink)
               .AppendValue(r.QueryStore)
               .AppendValue(r.PageVerify)
               .AppendValue(r.TargetRecovery)
               .AppendValue(r.DelayedDurability)
               .EndRow();
            rowsCollected++;
        }

        duckSw.Stop();
        _lastSqlMs = sqlSw.ElapsedMilliseconds;
        _lastDuckDbMs = duckSw.ElapsedMilliseconds;

        _logger?.LogDebug("Collected {RowCount} database config rows for server '{Server}'", rowsCollected, server.DisplayName);
        return rowsCollected;
    }

    /// <summary>
    /// Collects database-scoped configurations from sys.database_scoped_configurations
    /// for each online user database. On-load only, not scheduled.
    /// </summary>
    private async Task<int> CollectDatabaseScopedConfigAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        const string dbQuery = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    d.name
FROM sys.databases AS d
WHERE (d.database_id > 4 OR d.database_id = 2)
AND   d.database_id < 32761
AND   d.name <> N'PerformanceMonitor'
AND   d.state_desc = N'ONLINE'
ORDER BY d.name
OPTION(RECOMPILE);";

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
               .AppendValue(server.ServerName)
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

    /// <summary>
    /// Collects active trace flags via DBCC TRACESTATUS(-1). On-load only, not scheduled.
    /// Wrapped in try/catch — fails gracefully if caller lacks DBCC permissions.
    /// </summary>
    private async Task<int> CollectTraceFlagsAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        const string query = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

CREATE TABLE
    #trace_flags
(
    trace_flag integer NOT NULL,
    status bit NOT NULL,
    is_global bit NOT NULL,
    is_session bit NOT NULL
);

INSERT
    #trace_flags
(
    trace_flag,
    status,
    is_global,
    is_session
)
EXECUTE(N'DBCC TRACESTATUS(-1) WITH NO_INFOMSGS;');

SELECT
    tf.trace_flag,
    tf.status,
    tf.is_global,
    tf.is_session
FROM #trace_flags AS tf
ORDER BY tf.trace_flag
OPTION(RECOMPILE);";

        var serverId = GetServerId(server);
        var captureTime = DateTime.UtcNow;
        var rowsCollected = 0;
        _lastSqlMs = 0;
        _lastDuckDbMs = 0;

        try
        {
            var rows = new List<(int TraceFlag, bool Status, bool IsGlobal, bool IsSession)>();

            var sqlSw = Stopwatch.StartNew();
            using var sqlConnection = await CreateConnectionAsync(server, cancellationToken);
            using var command = new SqlCommand(query, sqlConnection);
            command.CommandTimeout = CommandTimeoutSeconds;

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetInt32(0),
                    reader.GetBoolean(1),
                    reader.GetBoolean(2),
                    reader.GetBoolean(3)));
            }
            sqlSw.Stop();

            var duckSw = Stopwatch.StartNew();
            using var duckConnection = _duckDb.CreateConnection();
            await duckConnection.OpenAsync(cancellationToken);

            using var appender = duckConnection.CreateAppender("trace_flags");
            foreach (var r in rows)
            {
                var row = appender.CreateRow();
                row.AppendValue(GenerateCollectionId())
                   .AppendValue(captureTime)
                   .AppendValue(serverId)
                   .AppendValue(server.ServerName)
                   .AppendValue(r.TraceFlag)
                   .AppendValue(r.Status)
                   .AppendValue(r.IsGlobal)
                   .AppendValue(r.IsSession)
                   .EndRow();
                rowsCollected++;
            }

            duckSw.Stop();
            _lastSqlMs = sqlSw.ElapsedMilliseconds;
            _lastDuckDbMs = duckSw.ElapsedMilliseconds;

            _logger?.LogDebug("Collected {RowCount} trace flag rows for server '{Server}'", rowsCollected, server.DisplayName);
        }
        catch (SqlException ex)
        {
            _logger?.LogWarning("Failed to collect trace flags on '{Server}' (may lack DBCC permissions): {Message}",
                server.DisplayName, ex.Message);
        }

        return rowsCollected;
    }
}
