/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
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
    /// Always-on DMV blocking snapshot. Captures current blocking (blocker -> blocked edges) from
    /// sys.dm_os_waiting_tasks + sys.dm_exec_*, the BPR-independent fallback for when the
    /// blocked_process_report XE captured nothing (blocked process threshold unset, e.g. AWS RDS).
    /// Same blocker/blocked pair-row shape the blocking-chain reconstruction consumes; monitor_loop is
    /// synthesized NEGATIVE so a DMV episode can never collide with a real (non-negative) BPR monitorLoop.
    /// </summary>
    private async Task<int> CollectDmvBlockingSnapshotAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        string query = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT /* PerformanceMonitorLite */
    database_name = DB_NAME(der_b.database_id),
    blocked_spid = CONVERT(integer, wt.session_id),
    blocked_ecid = CONVERT(integer, wt.exec_context_id),
    blocked_last_tran_started = tat_b.transaction_begin_time,
    blocking_spid = CONVERT(integer, wt.blocking_session_id),
    blocking_ecid = CONVERT(integer, ISNULL(wt.blocking_exec_context_id, 0)),
    blocking_last_tran_started = tat_k.transaction_begin_time,
    wait_time_ms = wt.wait_duration_ms,
    lock_mode = REPLACE(REPLACE(REPLACE(wt.wait_type, N'LCK_M_', N''), N'PAGEIOLATCH_', N''), N'PAGELATCH_', N''),
    blocking_status = ISNULL(der_k.status, ses_k.status),
    contentious_object =
        CASE
            WHEN objparse.object_id IS NOT NULL
            THEN ISNULL(QUOTENAME(OBJECT_SCHEMA_NAME(objparse.object_id, objparse.database_id)) + N'.' + QUOTENAME(OBJECT_NAME(objparse.object_id, objparse.database_id)), der_b.wait_resource)
            ELSE der_b.wait_resource
        END,
    blocked_sql_text = dest_b.text,
    blocking_sql_text = dest_k.text,
    blocked_login_name = ses_b.login_name,
    blocked_host_name = ses_b.host_name,
    blocked_client_app = ses_b.program_name,
    blocking_login_name = ses_k.login_name,
    blocking_host_name = ses_k.host_name,
    blocking_client_app = ses_k.program_name
FROM sys.dm_os_waiting_tasks AS wt
JOIN sys.dm_exec_sessions AS ses_b
  ON ses_b.session_id = wt.session_id
LEFT JOIN sys.dm_exec_requests AS der_b
  ON der_b.session_id = wt.session_id
LEFT JOIN sys.dm_exec_sessions AS ses_k
  ON ses_k.session_id = wt.blocking_session_id
LEFT JOIN sys.dm_exec_requests AS der_k
  ON der_k.session_id = wt.blocking_session_id
LEFT JOIN sys.dm_exec_connections AS con_k
  ON con_k.session_id = wt.blocking_session_id
OUTER APPLY sys.dm_exec_sql_text(der_b.sql_handle) AS dest_b
OUTER APPLY sys.dm_exec_sql_text(COALESCE(der_k.sql_handle, con_k.most_recent_sql_handle)) AS dest_k
OUTER APPLY
(
    SELECT TOP (1) tat.transaction_begin_time
    FROM sys.dm_tran_session_transactions AS stx
    JOIN sys.dm_tran_active_transactions AS tat
      ON tat.transaction_id = stx.transaction_id
    WHERE stx.session_id = wt.session_id
    ORDER BY tat.transaction_begin_time ASC
) AS tat_b
OUTER APPLY
(
    SELECT TOP (1) tat.transaction_begin_time
    FROM sys.dm_tran_session_transactions AS stx
    JOIN sys.dm_tran_active_transactions AS tat
      ON tat.transaction_id = stx.transaction_id
    WHERE stx.session_id = wt.blocking_session_id
    ORDER BY tat.transaction_begin_time ASC
) AS tat_k
OUTER APPLY
(
    SELECT
        database_id = TRY_CONVERT(integer, PARSENAME(REPLACE(SUBSTRING(der_b.wait_resource, 9, 200), N':', N'.'), 3)),
        object_id = TRY_CONVERT(integer, PARSENAME(REPLACE(SUBSTRING(der_b.wait_resource, 9, 200), N':', N'.'), 2))
    WHERE der_b.wait_resource LIKE N'OBJECT: %'
) AS objparse
WHERE wt.blocking_session_id IS NOT NULL
AND   wt.blocking_session_id <> 0
AND   wt.blocking_session_id <> wt.session_id
AND   wt.session_id > 50
AND   wt.session_id <> @@SPID
AND   (
          wt.wait_type LIKE N'LCK[_]%'
          OR wt.wait_type LIKE N'PAGELATCH[_]%'
          OR wt.wait_type LIKE N'PAGEIOLATCH[_]%'
          OR wt.wait_type LIKE N'RESOURCE_SEMAPHORE%'
      )
/* Layered minimum-wait floor (kept in lockstep with install/56): locks must persist to matter (2s); page
   latches churn faster (PAGELATCH 0.5s, PAGEIOLATCH 1s); memory-grant / compile-gate waits run long, so 5s. */
AND   wt.wait_duration_ms >=
      CASE
          WHEN wt.wait_type LIKE N'LCK[_]%'             THEN 2000
          WHEN wt.wait_type LIKE N'PAGELATCH[_]%'       THEN 500
          WHEN wt.wait_type LIKE N'PAGEIOLATCH[_]%'     THEN 1000
          WHEN wt.wait_type LIKE N'RESOURCE_SEMAPHORE%' THEN 5000
          ELSE 1000
      END
OPTION(RECOMPILE);";

        var serverId = GetServerId(server);
        var collectionTime = DateTime.UtcNow;
        /* Synthetic monitor_loop, NEGATIVE so it can never collide with a real (non-negative) BPR
           monitorLoop. One value per cycle, distinct per second (collection cadence is >= 1 minute). */
        var monitorLoop = -(int)(collectionTime - new DateTime(2020, 1, 1)).TotalSeconds;
        var rowsCollected = 0;
        _lastSqlMs = 0;
        _lastDuckDbMs = 0;

        var sqlSw = Stopwatch.StartNew();
        using var sqlConnection = await CreateConnectionAsync(server, cancellationToken);
        using var command = new SqlCommand(query, sqlConnection);
        command.CommandTimeout = CommandTimeoutSeconds;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        sqlSw.Stop();
        _lastSqlMs = sqlSw.ElapsedMilliseconds;

        var duckSw = Stopwatch.StartNew();

        using (var duckConnection = _duckDb.CreateConnection())
        {
            await duckConnection.OpenAsync(cancellationToken);

            using (var appender = duckConnection.CreateAppender("dmv_blocking_snapshots"))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    var databaseName = reader.IsDBNull(0) ? null : reader.GetString(0);
                    var blockedSpid = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                    var blockedEcid = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                    var blockedLastTran = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3);
                    var blockingSpid = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
                    var blockingEcid = reader.IsDBNull(5) ? 0 : reader.GetInt32(5);
                    var blockingLastTran = reader.IsDBNull(6) ? (DateTime?)null : reader.GetDateTime(6);
                    var waitTimeMs = reader.IsDBNull(7) ? 0L : reader.GetInt64(7);
                    var lockMode = reader.IsDBNull(8) ? null : reader.GetString(8);
                    var blockingStatus = reader.IsDBNull(9) ? null : reader.GetString(9);
                    var contentiousObject = reader.IsDBNull(10) ? null : reader.GetString(10);
                    var blockedSql = reader.IsDBNull(11) ? null : reader.GetString(11);
                    var blockingSql = reader.IsDBNull(12) ? null : reader.GetString(12);
                    var blockedLogin = reader.IsDBNull(13) ? null : reader.GetString(13);
                    var blockedHost = reader.IsDBNull(14) ? null : reader.GetString(14);
                    var blockedApp = reader.IsDBNull(15) ? null : reader.GetString(15);
                    var blockingLogin = reader.IsDBNull(16) ? null : reader.GetString(16);
                    var blockingHost = reader.IsDBNull(17) ? null : reader.GetString(17);
                    var blockingApp = reader.IsDBNull(18) ? null : reader.GetString(18);

                    var row = appender.CreateRow();
                    row.AppendValue(GenerateCollectionId())
                       .AppendValue(collectionTime)
                       .AppendValue(serverId)
                       .AppendValue(GetServerNameForStorage(server))
                       .AppendValue(monitorLoop)
                       .AppendValue(collectionTime) /* event_time */
                       .AppendValue(databaseName)
                       .AppendValue(blockedSpid)
                       .AppendValue(blockedEcid)
                       .AppendValue(blockedLastTran)
                       .AppendValue(blockingSpid)
                       .AppendValue(blockingEcid)
                       .AppendValue(blockingLastTran)
                       .AppendValue(waitTimeMs)
                       .AppendValue(lockMode)
                       .AppendValue(blockingStatus)
                       .AppendValue(contentiousObject)
                       .AppendValue(blockedSql)
                       .AppendValue(blockingSql)
                       .AppendValue(blockedLogin)
                       .AppendValue(blockedHost)
                       .AppendValue(blockedApp)
                       .AppendValue(blockingLogin)
                       .AppendValue(blockingHost)
                       .AppendValue(blockingApp)
                       .EndRow();

                    rowsCollected++;
                }
            }
        }

        duckSw.Stop();
        _lastDuckDbMs = duckSw.ElapsedMilliseconds;

        _logger?.LogDebug("Collected {RowCount} DMV blocking snapshot rows for server '{Server}'", rowsCollected, server.DisplayName);
        return rowsCollected;
    }
}
