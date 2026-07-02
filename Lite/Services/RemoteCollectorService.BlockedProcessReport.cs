/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using PerformanceMonitor.Collectors;
using PerformanceMonitorLite.Models;

namespace PerformanceMonitorLite.Services;

public partial class RemoteCollectorService
{
    /* The session name lives in the shared definition so the ring-buffer reader and this
       lifecycle code can never disagree on it. */
    private const string BlockedProcessXeSessionName = BlockedProcessReportCollector.XeSessionName;

    /// <summary>
    /// Ensures the blocked process XE session exists and is running.
    /// Creates a ring_buffer session for ALL platforms (on-prem, MI, Azure SQL DB).
    /// Uses server-scoped session for on-prem/MI, database-scoped for Azure SQL DB.
    /// </summary>
    public async Task EnsureBlockedProcessXeSessionAsync(ServerConnection server, int engineEdition = 0, CancellationToken cancellationToken = default)
    {
        /* Skip if the blocked_process_report collector is disabled */
        var schedule = _scheduleManager.GetScheduleForServer(server.Id, "blocked_process_report");
        if (schedule == null || !schedule.Enabled)
        {
            return;
        }

        bool isAzureSqlDb = engineEdition == 5;

        try
        {
            using var connection = await CreateConnectionAsync(server, cancellationToken);

            if (isAzureSqlDb)
            {
                /* Azure SQL DB: create database-scoped session with ring_buffer */
                await EnsureBlockedProcessXeSessionAzureSqlDbAsync(connection, cancellationToken);
            }
            else
            {
                /* On-prem and Azure MI: create server-scoped session with ring_buffer */
                await EnsureBlockedProcessXeSessionOnPremAsync(connection, server, cancellationToken);
            }
        }
        catch (SqlException ex) when (IsBenignXeSessionAlreadyPresent(ex))
        {
            /* The session is already present + running. On Azure SQL DB the XE existence catalogs are
               visibility-scoped per principal (sys.database_event_sessions needs VIEW DATABASE PERFORMANCE
               STATE, sys.dm_xe_database_sessions needs VIEW DATABASE STATE), so a pre-check can come back
               empty even when the session exists -- the CREATE/START path then reports "already exists"
               (25631) / "already started" (25705). That confirms the session is up; it is success, not a
               failure to surface as an unhealthy collector or log every cycle (#1251). */
            AppLogger.Info("XeSession", $"[{server.DisplayName}] Blocked process XE session already present (benign, #1251)");
        }
        catch (SqlException ex)
        {
            AppLogger.Error("XeSession", $"[{server.DisplayName}] Failed to ensure blocked process XE session: {ex.Message}");

            /* Propagate so RunCollectorAsync marks the collector unhealthy instead
               of letting a zero-row ring-buffer read record SUCCESS (#1086) */
            throw new XeSessionEnsureException("blocked process", ex);
        }
    }

    /// <summary>
    /// On-prem / Azure MI / AWS RDS: creates or ensures server-scoped XE session with ring_buffer target.
    /// Also ensures the blocked process threshold is configured (skipped on RDS where sp_configure is not available).
    /// </summary>
    private async Task EnsureBlockedProcessXeSessionOnPremAsync(SqlConnection connection, ServerConnection server, CancellationToken cancellationToken)
    {
        /* Check blocked process threshold and configure if needed.
           Wrapped in try/catch because sp_configure is not available on AWS RDS
           (threshold must be set via RDS parameter groups instead). */
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
            thresholdCmd.CommandTimeout = CommandTimeoutSeconds;
            var result = await thresholdCmd.ExecuteScalarAsync(cancellationToken);
            var threshold = result as int? ?? 0;

            if (threshold == 0)
            {
                AppLogger.Info("XeSession", $"[{server.DisplayName}] Configured blocked process threshold to 5 seconds");
            }
        }
        catch (SqlException ex)
        {
            /* sp_configure not available (e.g. AWS RDS) — threshold must be set via platform config */
            AppLogger.Info("XeSession", $"[{server.DisplayName}] Cannot set blocked process threshold via sp_configure (may require platform config): {ex.Message}");
        }

        /* Check if our XE session already exists */
        using (var cmd = new SqlCommand(@"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT /* PerformanceMonitorLite */
    is_running = CASE WHEN dxs.name IS NOT NULL THEN 1 ELSE 0 END
FROM sys.server_event_sessions AS ses
LEFT JOIN sys.dm_xe_sessions AS dxs
  ON dxs.name = ses.name
WHERE ses.name = @session_name;", connection))
        {
            cmd.CommandTimeout = CommandTimeoutSeconds;
            cmd.Parameters.Add(new SqlParameter("@session_name", SqlDbType.NVarChar, 128) { Value = BlockedProcessXeSessionName });
            var result = await cmd.ExecuteScalarAsync(cancellationToken);

            if (result != null)
            {
                if (result is int isRunning && isRunning == 0)
                {
                    /* Session exists but is stopped - start it */
                    try
                    {
                        using var startCmd = new SqlCommand(
                            $"ALTER EVENT SESSION [{BlockedProcessXeSessionName}] ON SERVER STATE = START;", connection);
                        startCmd.CommandTimeout = CommandTimeoutSeconds;
                        await startCmd.ExecuteNonQueryAsync(cancellationToken);
                        AppLogger.Info("XeSession", $"[{server.DisplayName}] Started blocked process XE session");
                    }
                    catch (SqlException ex)
                    {
                        AppLogger.Error("XeSession", $"[{server.DisplayName}] Failed to start blocked process XE session: {ex.Message}");
                        throw;
                    }
                }
                else
                {
                    AppLogger.Debug("XeSession", $"Blocked process XE session is running on '{server.DisplayName}'");
                }
                return;
            }
        }

        /* Create and start server-scoped session with ring_buffer */
        try
        {
            using var createCmd = new SqlCommand($@"
CREATE EVENT SESSION [{BlockedProcessXeSessionName}]
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

ALTER EVENT SESSION [{BlockedProcessXeSessionName}] ON SERVER STATE = START;", connection);
            createCmd.CommandTimeout = CommandTimeoutSeconds;
            await createCmd.ExecuteNonQueryAsync(cancellationToken);
            AppLogger.Info("XeSession", $"[{server.DisplayName}] Created and started blocked process XE session");
        }
        catch (SqlException ex)
        {
            AppLogger.Error("XeSession", $"[{server.DisplayName}] Failed to create blocked process XE session: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Azure SQL DB: creates database-scoped XE session with ring_buffer target.
    /// File targets are not supported in Azure SQL DB.
    /// </summary>
    private async Task EnsureBlockedProcessXeSessionAzureSqlDbAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        /* Check if database-scoped session already exists */
        using (var cmd = new SqlCommand(@"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT /* PerformanceMonitorLite */
    session_state = des.name
FROM sys.database_event_sessions AS des
WHERE des.name = @session_name;", connection))
        {
            cmd.CommandTimeout = CommandTimeoutSeconds;
            cmd.Parameters.Add(new SqlParameter("@session_name", SqlDbType.NVarChar, 128) { Value = BlockedProcessXeSessionName });
            var result = await cmd.ExecuteScalarAsync(cancellationToken);

            if (result != null)
            {
                /* Session exists - ensure it's started (database-scoped sessions can stop on reconnect) */
                using var startCmd = new SqlCommand($@"
IF NOT EXISTS
(
    SELECT
        1/0
    FROM sys.dm_xe_database_sessions AS xes
    WHERE xes.name = N'{BlockedProcessXeSessionName}'
)
BEGIN
    ALTER EVENT SESSION [{BlockedProcessXeSessionName}] ON DATABASE STATE = START;
END;", connection);
                startCmd.CommandTimeout = CommandTimeoutSeconds;
                await startCmd.ExecuteNonQueryAsync(cancellationToken);

                AppLogger.Info("XeSession", $"[Azure SQL DB] Blocked process XE session verified (database-scoped)");
                return;
            }
        }

        /* Create and start database-scoped session */
        using (var cmd = new SqlCommand($@"
CREATE EVENT SESSION [{BlockedProcessXeSessionName}]
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

ALTER EVENT SESSION [{BlockedProcessXeSessionName}] ON DATABASE STATE = START;", connection))
        {
            cmd.CommandTimeout = CommandTimeoutSeconds;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        AppLogger.Info("XeSession", $"[Azure SQL DB] Created and started blocked process XE session (database-scoped)");
    }

    /// <summary>
    /// True when every error is a benign "the session is already there" extended-events error:
    /// 25631 (event session already exists) or 25705 (already started). On Azure SQL DB the XE
    /// existence catalogs (sys.database_event_sessions / sys.dm_xe_database_sessions) are visibility-
    /// scoped per principal and can come back empty even when the session is present + running, so the
    /// CREATE/START path reports these -- they confirm the session is up, not a failure to surface (#1251).
    /// Shared by the blocked-process and deadlock ensure paths (same partial class).
    /// </summary>
    private static bool IsBenignXeSessionAlreadyPresent(SqlException ex)
    {
        if (ex.Errors.Count == 0)
        {
            return false;
        }

        foreach (Microsoft.Data.SqlClient.SqlError error in ex.Errors)
        {
            /* 25631 = event session already exists; 25705 = already started. */
            if (error.Number != 25631 && error.Number != 25705)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Collects blocked process reports via the shared <see cref="BlockedProcessReportCollector"/>
    /// definition (the server- vs database-scoped ring-buffer reads, the wait_resource →
    /// contentious-object resolution, the event_time watermark, and the report-XML parse live
    /// there — the cross-SKU parity contract). The XE session lifecycle stays here; a
    /// missing/inaccessible session is tolerated as zero rows, exactly as before.
    /// </summary>
    private async Task<int> CollectBlockedProcessReportsAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        try
        {
            return await RunCollectorDefinitionAsync(BlockedProcessReportCollector.Instance, server, cancellationToken);
        }
        catch (SqlException ex) when (ex.Number == 297 || ex.Number == 15151 || ex.Message.Contains("XE session"))
        {
            /* XE session not found or not accessible */
            AppLogger.Info("XeSession", $"[{server.DisplayName}] Blocked process XE session not available: {ex.Message}");
            return 0;
        }
    }
}
