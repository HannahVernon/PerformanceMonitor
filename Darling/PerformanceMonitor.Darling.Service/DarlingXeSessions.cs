/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PerformanceMonitor.Collectors;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// Creates and starts the app-managed XE ring-buffer sessions the deadlock and blocked-process
/// collectors read — ported from Lite's Ensure* lifecycle (session names come from the shared
/// definitions so the reader and this lifecycle can never disagree). Server-scoped sessions on
/// on-prem/MI/RDS, database-scoped on Azure SQL DB; the blocked-process threshold sp_configure
/// bootstrap self-guards for platforms without sp_configure (AWS RDS). Ensure failures are
/// logged and tolerated — the collectors read zero rows until the session exists (#1251 benign
/// "already present" errors are success).
/// </summary>
public static class DarlingXeSessions
{
    public static async Task EnsureAllAsync(ServerRuntime server, ILogger? logger, CancellationToken cancellationToken)
    {
        try
        {
            using var connection = new SqlConnection(server.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            if (server.Target.IsAzureSqlDb)
            {
                await EnsureDeadlockAzureAsync(connection, server, logger, cancellationToken);
                await EnsureBlockedProcessAzureAsync(connection, server, logger, cancellationToken);
            }
            else
            {
                await EnsureDeadlockOnPremAsync(connection, server, logger, cancellationToken);
                await EnsureBlockedProcessOnPremAsync(connection, server, logger, cancellationToken);
            }
        }
        catch (SqlException ex) when (IsBenignXeSessionAlreadyPresent(ex))
        {
            logger?.LogInformation("[{Server}] XE sessions already present (benign, #1251)", server.Config.DisplayName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.LogError("[{Server}] Failed to ensure XE sessions: {Message} — deadlock/blocked-process collection will read zero rows until resolved",
                server.Config.DisplayName, ex.Message);
        }
    }

    private static async Task EnsureDeadlockOnPremAsync(SqlConnection connection, ServerRuntime server, ILogger? logger, CancellationToken cancellationToken)
    {
        using (var cmd = new SqlCommand(@"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT /* PerformanceMonitorDarling */
    is_running = CASE WHEN dxs.name IS NOT NULL THEN 1 ELSE 0 END
FROM sys.server_event_sessions AS ses
LEFT JOIN sys.dm_xe_sessions AS dxs
  ON dxs.name = ses.name
WHERE ses.name = @session_name;", connection))
        {
            cmd.CommandTimeout = 60;
            cmd.Parameters.Add(new SqlParameter("@session_name", SqlDbType.NVarChar, 128) { Value = DeadlocksCollector.XeSessionName });
            var result = await cmd.ExecuteScalarAsync(cancellationToken);

            if (result != null)
            {
                if (result is int isRunning && isRunning == 0)
                {
                    using var startCmd = new SqlCommand(
                        $"ALTER EVENT SESSION [{DeadlocksCollector.XeSessionName}] ON SERVER STATE = START;", connection);
                    startCmd.CommandTimeout = 60;
                    await startCmd.ExecuteNonQueryAsync(cancellationToken);
                    logger?.LogInformation("[{Server}] Started deadlock XE session", server.Config.DisplayName);
                }
                return;
            }
        }

        /* MEMORY_PARTITION_MODE = NONE for AWS RDS compatibility (mirrors Lite). */
        using var createCmd = new SqlCommand($@"
CREATE EVENT SESSION [{DeadlocksCollector.XeSessionName}]
ON SERVER
ADD EVENT sqlserver.xml_deadlock_report
ADD TARGET package0.ring_buffer
(
    SET max_memory = 4096
)
WITH
(
    MAX_DISPATCH_LATENCY = 5 SECONDS,
    EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS,
    MEMORY_PARTITION_MODE = NONE,
    STARTUP_STATE = ON
);

ALTER EVENT SESSION [{DeadlocksCollector.XeSessionName}] ON SERVER STATE = START;", connection);
        createCmd.CommandTimeout = 60;
        await createCmd.ExecuteNonQueryAsync(cancellationToken);
        logger?.LogInformation("[{Server}] Created and started deadlock XE session", server.Config.DisplayName);
    }

    private static async Task EnsureDeadlockAzureAsync(SqlConnection connection, ServerRuntime server, ILogger? logger, CancellationToken cancellationToken)
    {
        using (var cmd = new SqlCommand(@"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT /* PerformanceMonitorDarling */
    has_correct_event = CASE
        WHEN EXISTS
        (
            SELECT 1/0
            FROM sys.database_event_session_events AS dese
            JOIN sys.database_event_sessions AS des
              ON des.event_session_id = dese.event_session_id
            WHERE des.name = @session_name
            AND   dese.name = N'database_xml_deadlock_report'
        )
        THEN 1
        WHEN EXISTS
        (
            SELECT 1/0
            FROM sys.database_event_sessions AS des
            WHERE des.name = @session_name
        )
        THEN 0
        ELSE NULL
    END;", connection))
        {
            cmd.CommandTimeout = 60;
            cmd.Parameters.Add(new SqlParameter("@session_name", SqlDbType.NVarChar, 128) { Value = DeadlocksCollector.XeSessionName });
            var result = await cmd.ExecuteScalarAsync(cancellationToken);

            if (result is int hasCorrectEvent)
            {
                if (hasCorrectEvent == 0)
                {
                    /* Wrong event — drop and recreate (mirrors Lite). */
                    try
                    {
                        using var dropCmd = new SqlCommand(
                            $"DROP EVENT SESSION [{DeadlocksCollector.XeSessionName}] ON DATABASE;", connection);
                        dropCmd.CommandTimeout = 60;
                        await dropCmd.ExecuteNonQueryAsync(cancellationToken);
                    }
                    catch (SqlException ex)
                    {
                        logger?.LogError("[{Server}] Failed to drop old deadlock XE session: {Message}", server.Config.DisplayName, ex.Message);
                    }
                }
                else
                {
                    using var startCmd = new SqlCommand($@"
IF NOT EXISTS
(
    SELECT
        1/0
    FROM sys.dm_xe_database_sessions AS xes
    WHERE xes.name = N'{DeadlocksCollector.XeSessionName}'
)
BEGIN
    ALTER EVENT SESSION [{DeadlocksCollector.XeSessionName}] ON DATABASE STATE = START;
END;", connection);
                    startCmd.CommandTimeout = 60;
                    await startCmd.ExecuteNonQueryAsync(cancellationToken);
                    return;
                }
            }
        }

        using (var cmd = new SqlCommand($@"
CREATE EVENT SESSION [{DeadlocksCollector.XeSessionName}]
ON DATABASE
ADD EVENT sqlserver.database_xml_deadlock_report
ADD TARGET package0.ring_buffer
(
    SET max_memory = 4096
)
WITH
(
    MAX_DISPATCH_LATENCY = 5 SECONDS,
    EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS,
    STARTUP_STATE = ON
);

ALTER EVENT SESSION [{DeadlocksCollector.XeSessionName}] ON DATABASE STATE = START;", connection))
        {
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        logger?.LogInformation("[{Server}] Created and started deadlock XE session (database-scoped)", server.Config.DisplayName);
    }

    private static async Task EnsureBlockedProcessOnPremAsync(SqlConnection connection, ServerRuntime server, ILogger? logger, CancellationToken cancellationToken)
    {
        /* Blocked process threshold bootstrap — sp_configure is unavailable on AWS RDS
           (parameter groups there), hence the tolerant catch (mirrors Lite). */
        try
        {
            using var thresholdCmd = new SqlCommand(@"
DECLARE
    @threshold integer;

SELECT
    @threshold = CONVERT(integer, c.value_in_use)
FROM sys.configurations AS c
WHERE c.name = N'blocked process threshold (s)';

IF @threshold = 0
BEGIN
    EXECUTE sys.sp_configure
        N'show advanced options',
        1;

    RECONFIGURE;

    EXECUTE sys.sp_configure
        N'blocked process threshold (s)',
        5;

    RECONFIGURE;
END;

SELECT @threshold;", connection);
            thresholdCmd.CommandTimeout = 60;
            var result = await thresholdCmd.ExecuteScalarAsync(cancellationToken);
            if ((result as int? ?? 0) == 0)
            {
                logger?.LogInformation("[{Server}] Configured blocked process threshold to 5 seconds", server.Config.DisplayName);
            }
        }
        catch (SqlException ex)
        {
            logger?.LogInformation("[{Server}] Cannot set blocked process threshold via sp_configure (may require platform config): {Message}",
                server.Config.DisplayName, ex.Message);
        }

        using (var cmd = new SqlCommand(@"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT /* PerformanceMonitorDarling */
    is_running = CASE WHEN dxs.name IS NOT NULL THEN 1 ELSE 0 END
FROM sys.server_event_sessions AS ses
LEFT JOIN sys.dm_xe_sessions AS dxs
  ON dxs.name = ses.name
WHERE ses.name = @session_name;", connection))
        {
            cmd.CommandTimeout = 60;
            cmd.Parameters.Add(new SqlParameter("@session_name", SqlDbType.NVarChar, 128) { Value = BlockedProcessReportCollector.XeSessionName });
            var result = await cmd.ExecuteScalarAsync(cancellationToken);

            if (result != null)
            {
                if (result is int isRunning && isRunning == 0)
                {
                    using var startCmd = new SqlCommand(
                        $"ALTER EVENT SESSION [{BlockedProcessReportCollector.XeSessionName}] ON SERVER STATE = START;", connection);
                    startCmd.CommandTimeout = 60;
                    await startCmd.ExecuteNonQueryAsync(cancellationToken);
                    logger?.LogInformation("[{Server}] Started blocked process XE session", server.Config.DisplayName);
                }
                return;
            }
        }

        using var createCmd = new SqlCommand($@"
CREATE EVENT SESSION [{BlockedProcessReportCollector.XeSessionName}]
ON SERVER
ADD EVENT sqlserver.blocked_process_report
ADD TARGET package0.ring_buffer
(
    SET max_memory = 4096
)
WITH
(
    MAX_DISPATCH_LATENCY = 5 SECONDS,
    STARTUP_STATE = ON
);

ALTER EVENT SESSION [{BlockedProcessReportCollector.XeSessionName}] ON SERVER STATE = START;", connection);
        createCmd.CommandTimeout = 60;
        await createCmd.ExecuteNonQueryAsync(cancellationToken);
        logger?.LogInformation("[{Server}] Created and started blocked process XE session", server.Config.DisplayName);
    }

    private static async Task EnsureBlockedProcessAzureAsync(SqlConnection connection, ServerRuntime server, ILogger? logger, CancellationToken cancellationToken)
    {
        using (var cmd = new SqlCommand(@"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT /* PerformanceMonitorDarling */
    session_state = des.name
FROM sys.database_event_sessions AS des
WHERE des.name = @session_name;", connection))
        {
            cmd.CommandTimeout = 60;
            cmd.Parameters.Add(new SqlParameter("@session_name", SqlDbType.NVarChar, 128) { Value = BlockedProcessReportCollector.XeSessionName });
            var result = await cmd.ExecuteScalarAsync(cancellationToken);

            if (result != null)
            {
                using var startCmd = new SqlCommand($@"
IF NOT EXISTS
(
    SELECT
        1/0
    FROM sys.dm_xe_database_sessions AS xes
    WHERE xes.name = N'{BlockedProcessReportCollector.XeSessionName}'
)
BEGIN
    ALTER EVENT SESSION [{BlockedProcessReportCollector.XeSessionName}] ON DATABASE STATE = START;
END;", connection);
                startCmd.CommandTimeout = 60;
                await startCmd.ExecuteNonQueryAsync(cancellationToken);
                return;
            }
        }

        using (var cmd = new SqlCommand($@"
CREATE EVENT SESSION [{BlockedProcessReportCollector.XeSessionName}]
ON DATABASE
ADD EVENT sqlserver.blocked_process_report
ADD TARGET package0.ring_buffer
(
    SET max_memory = 4096
)
WITH
(
    MAX_DISPATCH_LATENCY = 5 SECONDS,
    STARTUP_STATE = ON
);

ALTER EVENT SESSION [{BlockedProcessReportCollector.XeSessionName}] ON DATABASE STATE = START;", connection))
        {
            cmd.CommandTimeout = 60;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        logger?.LogInformation("[{Server}] Created and started blocked process XE session (database-scoped)", server.Config.DisplayName);
    }

    /// <summary>
    /// True when every error is a benign "the session is already there" extended-events error:
    /// 25631 (already exists) or 25705 (already started) — on Azure SQL DB the XE existence
    /// catalogs are visibility-scoped per principal, so CREATE/START can report these even when
    /// the pre-check came back empty; they confirm the session is up (#1251, verbatim from Lite).
    /// </summary>
    internal static bool IsBenignXeSessionAlreadyPresent(SqlException ex)
    {
        if (ex.Errors.Count == 0)
        {
            return false;
        }

        foreach (SqlError error in ex.Errors)
        {
            if (error.Number != 25631 && error.Number != 25705)
            {
                return false;
            }
        }

        return true;
    }
}
