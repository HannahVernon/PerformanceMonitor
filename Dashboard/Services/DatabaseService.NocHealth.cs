/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using PerformanceMonitorDashboard.Helpers;
using PerformanceMonitorDashboard.Models;

namespace PerformanceMonitorDashboard.Services
{
    public partial class DatabaseService
    {
        /// <summary>
        /// Fetches all NOC health metrics for the landing page.
        /// Returns a populated ServerHealthStatus object.
        /// </summary>
        public async Task<ServerHealthStatus> GetNocHealthStatusAsync(ServerConnection server, int engineEdition = 0)
        {
            var status = new ServerHealthStatus(server);

            try
            {
                await using var tc = await OpenThrottledConnectionAsync();
                var connection = tc.Connection;

                status.IsOnline = true;

                // Run all health queries in parallel for speed
                var cpuTask = GetCpuPercentAsync(connection, engineEdition);
                var memoryTask = GetMemoryStatusAsync(connection, status);
                var blockingTask = GetBlockingStatusAsync(connection, status);
                var threadsTask = GetThreadStatusAsync(connection, status);
                var deadlockTask = GetDeadlockCountAsync(connection);
                var collectorTask = GetCollectorStatusAsync(connection, status);
                var waitsTask = GetTopWaitAsync(connection, status);
                var lastBlockingTask = GetLastBlockingEventTimeAsync(connection, status);
                var lastDeadlockTask = GetLastDeadlockEventTimeAsync(connection, status);

                await Task.WhenAll(cpuTask, memoryTask, blockingTask, threadsTask, deadlockTask, collectorTask, waitsTask, lastBlockingTask, lastDeadlockTask);

                var cpuResult = await cpuTask;
                status.CpuPercent = cpuResult.SqlCpu;
                status.OtherCpuPercent = cpuResult.OtherCpu;
                status.DeadlockCount = await deadlockTask;

                status.LastUpdated = DateTime.Now;
                status.NotifyOverallSeverityChanged();
            }
            catch (Exception ex)
            {
                status.IsOnline = false;
                status.ErrorMessage = ex.Message;
                Logger.Warning($"Failed to get NOC health for {server.DisplayName}: {ex.Message}");
            }

            return status;
        }

        /// <summary>
        /// Updates an existing ServerHealthStatus with fresh data.
        /// </summary>
        public async Task RefreshNocHealthStatusAsync(ServerHealthStatus status, int engineEdition = 0)
        {
            status.IsLoading = true;
            Logger.Info($"RefreshNocHealthStatusAsync starting for {status.DisplayName}");

            try
            {
                await using var tc = await OpenThrottledConnectionAsync();
                var connection = tc.Connection;
                Logger.Info($"Connection opened for {status.DisplayName}");

                status.IsOnline = true;
                status.ErrorMessage = null;

                // Run all health queries in parallel
                var cpuTask = GetCpuPercentAsync(connection, engineEdition);
                var memoryTask = GetMemoryStatusAsync(connection, status);
                var blockingTask = GetBlockingStatusAsync(connection, status);
                var threadsTask = GetThreadStatusAsync(connection, status);
                var deadlockTask = GetDeadlockCountAsync(connection);
                var collectorTask = GetCollectorStatusAsync(connection, status);
                var waitsTask = GetTopWaitAsync(connection, status);
                var lastBlockingTask = GetLastBlockingEventTimeAsync(connection, status);
                var lastDeadlockTask = GetLastDeadlockEventTimeAsync(connection, status);

                await Task.WhenAll(cpuTask, memoryTask, blockingTask, threadsTask, deadlockTask, collectorTask, waitsTask, lastBlockingTask, lastDeadlockTask);
                Logger.Info($"All NOC queries completed for {status.DisplayName}");

                var cpuResult = await cpuTask;
                status.CpuPercent = cpuResult.SqlCpu;
                status.OtherCpuPercent = cpuResult.OtherCpu;
                status.DeadlockCount = await deadlockTask;

                Logger.Info($"NOC status for {status.DisplayName}: CPU={status.CpuPercent}%, Blocked={status.TotalBlocked}, LongestBlock={status.LongestBlockedSeconds}s");

                status.LastUpdated = DateTime.Now;
                status.NotifyOverallSeverityChanged();
            }
            catch (Exception ex)
            {
                status.IsOnline = false;
                status.ErrorMessage = ex.Message;
                Logger.Warning($"Failed to refresh NOC health for {status.DisplayName}: {ex.Message}");
            }
            finally
            {
                status.IsLoading = false;
            }
        }

        /// <summary>
        /// Lightweight alert-only health check. Runs 3 queries instead of 9.
        /// Used by MainWindow's independent alert timer.
        /// </summary>
        public async Task<AlertHealthResult> GetAlertHealthAsync(int engineEdition = 0)
        {
            var result = new AlertHealthResult();

            try
            {
                await using var tc = await OpenThrottledConnectionAsync();
                var connection = tc.Connection;

                result.IsOnline = true;

                var cpuTask = GetCpuPercentAsync(connection, engineEdition);
                var blockingTask = GetBlockingValuesAsync(connection);
                var deadlockTask = GetDeadlockCountAsync(connection);

                await Task.WhenAll(cpuTask, blockingTask, deadlockTask);

                var cpuResult = await cpuTask;
                result.CpuPercent = cpuResult.SqlCpu;
                result.OtherCpuPercent = cpuResult.OtherCpu;

                var blockingResult = await blockingTask;
                result.TotalBlocked = blockingResult.TotalBlocked;
                result.LongestBlockedSeconds = blockingResult.LongestBlockedSeconds;

                result.DeadlockCount = await deadlockTask;
            }
            catch (Exception ex)
            {
                result.IsOnline = false;
                Logger.Warning($"Failed to get alert health: {ex.Message}");
            }

            return result;
        }

        /// <summary>
        /// Returns blocking values directly (without writing to a ServerHealthStatus).
        /// Used by GetAlertHealthAsync for lightweight alert checks.
        /// </summary>
        private async Task<(long TotalBlocked, decimal LongestBlockedSeconds)> GetBlockingValuesAsync(SqlConnection connection)
        {
            const string query = @"SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

                SELECT
                    total_blocked = COUNT_BIG(*),
                    longest_blocked_seconds = ISNULL(MAX(s.waittime), 0) / 1000.0
                FROM sys.sysprocesses AS s
                WHERE s.blocked <> 0
                AND   s.lastwaittype LIKE N'LCK%'
                OPTION(MAXDOP 1, RECOMPILE);";

            try
            {
                using var cmd = new SqlCommand(query, connection);
                cmd.CommandTimeout = 10;
                using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    var totalBlockedValue = reader.GetValue(0);
                    var longestSecondsValue = reader.GetValue(1);

                    var totalBlocked = Convert.ToInt64(totalBlockedValue, System.Globalization.CultureInfo.InvariantCulture);
                    var longestSeconds = Convert.ToDecimal(longestSecondsValue, System.Globalization.CultureInfo.InvariantCulture);

                    return (totalBlocked, longestSeconds);
                }
                return (0, 0);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to get blocking values: {ex.Message}");
                return (0, 0);
            }
        }

        private async Task<(int? SqlCpu, int? OtherCpu)> GetCpuPercentAsync(SqlConnection connection, int engineEdition = 0)
        {
            /* Azure SQL DB (edition 5) doesn't have dm_os_ring_buffers.
               Use sys.dm_db_resource_stats instead (reports avg_cpu_percent over 15-second intervals). */
            bool isAzureSqlDb = engineEdition == 5;

            string query = isAzureSqlDb
                ? @"SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

                SELECT TOP (1)
                    sql_cpu_percent = CONVERT(integer, avg_cpu_percent),
                    other_cpu_percent = CONVERT(integer, 0)
                FROM sys.dm_db_resource_stats
                ORDER BY
                    end_time DESC
                OPTION(MAXDOP 1);"
                : @"SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

                SELECT TOP (1)
                    sql_cpu_percent =
                        x.rb.value
                        (
                            '(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]',
                            'integer'
                        ),
                    other_cpu_percent =
                        100
                        - x.rb.value
                        (
                            '(./Record/SchedulerMonitorEvent/SystemHealth/SystemIdle)[1]',
                            'integer'
                        )
                        - x.rb.value
                        (
                            '(./Record/SchedulerMonitorEvent/SystemHealth/ProcessUtilization)[1]',
                            'integer'
                        )
                FROM
                (
                    SELECT
                        rb.timestamp,
                        rb = TRY_CAST(rb.record AS XML)
                    FROM sys.dm_os_ring_buffers AS rb
                    WHERE rb.ring_buffer_type = N'RING_BUFFER_SCHEDULER_MONITOR'
                ) AS x
                ORDER BY
                    x.timestamp DESC
                OPTION(MAXDOP 1, RECOMPILE);";

            try
            {
                using var cmd = new SqlCommand(query, connection);
                cmd.CommandTimeout = 10;
                using var reader = await cmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    var sqlCpu = reader.IsDBNull(0) ? null : (int?)reader.GetInt32(0);
                    var otherCpu = reader.IsDBNull(1) ? null : (int?)reader.GetInt32(1);
                    return (sqlCpu, otherCpu);
                }
                return (null, null);
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to get CPU percent: {ex.Message}");
                return (null, null);
            }
        }

        private async Task GetMemoryStatusAsync(SqlConnection connection, ServerHealthStatus status)
        {
            const string query = @"SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

                SELECT
                    buffer_pool_gb =
                    (
                        SELECT
                            SUM(domc.pages_kb) / 1024.0 / 1024.0
                        FROM sys.dm_os_memory_clerks AS domc
                        WHERE domc.type = N'MEMORYCLERK_SQLBUFFERPOOL'
                        AND   domc.memory_node_id < 64
                    ),
                    total_granted_memory_gb =
                        SUM(deqrs.granted_memory_kb) / 1024.0 / 1024.0,
                    total_used_memory_gb =
                        SUM(deqrs.used_memory_kb) / 1024.0 / 1024.0,
                    requests_waiting_for_memory =
                        SUM(deqrs.waiter_count)
                FROM sys.dm_exec_query_resource_semaphores AS deqrs
                WHERE deqrs.max_target_memory_kb IS NOT NULL
                OPTION(MAXDOP 1, RECOMPILE);";

            try
            {
                using var cmd = new SqlCommand(query, connection);
                cmd.CommandTimeout = 10;
                using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    status.BufferPoolGb = reader.IsDBNull(0) ? null : reader.GetDecimal(0);
                    status.GrantedMemoryGb = reader.IsDBNull(1) ? null : reader.GetDecimal(1);
                    status.UsedMemoryGb = reader.IsDBNull(2) ? null : reader.GetDecimal(2);
                    status.RequestsWaitingForMemory = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to get memory status: {ex.Message}");
            }
        }

        private async Task GetBlockingStatusAsync(SqlConnection connection, ServerHealthStatus status)
        {
            const string query = @"SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

                SELECT
                    total_blocked = COUNT_BIG(*),
                    longest_blocked_seconds = ISNULL(MAX(s.waittime), 0) / 1000.0
                FROM sys.sysprocesses AS s
                WHERE s.blocked <> 0
                AND   s.lastwaittype LIKE N'LCK%'
                OPTION(MAXDOP 1, RECOMPILE);";

            try
            {
                using var cmd = new SqlCommand(query, connection);
                cmd.CommandTimeout = 10;
                using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    // Use GetValue + Convert for safety with varying SQL types
                    var totalBlockedValue = reader.GetValue(0);
                    var longestSecondsValue = reader.GetValue(1);
                    
                    var totalBlocked = Convert.ToInt64(totalBlockedValue, System.Globalization.CultureInfo.InvariantCulture);
                    var longestSeconds = Convert.ToDecimal(longestSecondsValue, System.Globalization.CultureInfo.InvariantCulture);
                    
                    status.TotalBlocked = totalBlocked;
                    status.LongestBlockedSeconds = longestSeconds;
                    
                    Logger.Info($"Blocking status: {totalBlocked} blocked, longest {longestSeconds}s");
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to get blocking status: {ex.Message}");
            }
        }

        private async Task GetThreadStatusAsync(SqlConnection connection, ServerHealthStatus status)
        {
            const string query = @"SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

                SELECT
                    total_threads =
                        MAX(osi.max_workers_count),
                    available_threads =
                        MAX(osi.max_workers_count) - SUM(dos.active_workers_count),
                    threads_waiting_for_cpu =
                        SUM(dos.runnable_tasks_count),
                    requests_waiting_for_threads =
                        SUM(dos.work_queue_count)
                FROM sys.dm_os_schedulers AS dos
                CROSS JOIN sys.dm_os_sys_info AS osi
                WHERE dos.status = N'VISIBLE ONLINE'
                OPTION(MAXDOP 1, RECOMPILE);";

            try
            {
                using var cmd = new SqlCommand(query, connection);
                cmd.CommandTimeout = 10;
                using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    /*
                    Use Convert.ToInt32 to handle both int and bigint return types
                    (SQL Server SUM/MAX on int columns may return bigint on some versions)
                    */
                    status.TotalThreads = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                    status.AvailableThreads = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
                    status.ThreadsWaitingForCpu = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
                    status.RequestsWaitingForThreads = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3));
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to get thread status: {ex.Message}");
            }
        }

        private async Task<long> GetDeadlockCountAsync(SqlConnection connection)
        {
            const string query = @"SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

                SELECT
                    deadlock_count = SUM(pc.cntr_value)
                FROM sys.dm_os_performance_counters AS pc
                WHERE pc.counter_name LIKE N'Number of Deadlocks/sec%'
                OPTION(MAXDOP 1, RECOMPILE);";

            try
            {
                using var cmd = new SqlCommand(query, connection);
                cmd.CommandTimeout = 10;
                var result = await cmd.ExecuteScalarAsync();
                return result is long l ? l : 0;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to get deadlock count: {ex.Message}");
                return 0;
            }
        }

        private async Task GetCollectorStatusAsync(SqlConnection connection, ServerHealthStatus status)
        {
            const string query = @"SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

                SELECT
                    healthy_collector_count =
                        SUM(CASE WHEN ch.health_status = N'HEALTHY' THEN 1 ELSE 0 END),
                    failed_collector_count =
                        SUM(CASE WHEN ch.health_status = N'FAILING' THEN 1 ELSE 0 END)
                FROM report.collection_health AS ch
                OPTION(MAXDOP 1, RECOMPILE);";

            try
            {
                using var cmd = new SqlCommand(query, connection);
                cmd.CommandTimeout = 10;
                using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    status.HealthyCollectorCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                    status.FailedCollectorCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to get collector status: {ex.Message}");
            }
        }

        private async Task GetTopWaitAsync(SqlConnection connection, ServerHealthStatus status)
        {
            const string query = @"SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

                SELECT TOP (1)
                    dowt.wait_type,
                    wait_duration_seconds =
                        SUM(dowt.wait_duration_ms) / 1000.0
                FROM sys.dm_os_waiting_tasks AS dowt
                WHERE dowt.session_id > 50
                AND   NOT EXISTS
                      (
                          SELECT
                              1/0
                          FROM config.ignored_wait_types AS iwt
                          WHERE iwt.wait_type = dowt.wait_type
                      )
                GROUP BY
                    dowt.wait_type
                ORDER BY
                    SUM(dowt.wait_duration_ms) DESC
                OPTION(MAXDOP 1, RECOMPILE);";

            try
            {
                using var cmd = new SqlCommand(query, connection);
                cmd.CommandTimeout = 10;
                using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    status.TopWaitType = reader.IsDBNull(0) ? null : reader.GetString(0);
                    status.TopWaitDurationSeconds = reader.IsDBNull(1) ? 0 : reader.GetDecimal(1);
                }
                else
                {
                    status.TopWaitType = null;
                    status.TopWaitDurationSeconds = 0;
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to get top wait: {ex.Message}");
            }
        }

        private async Task GetLastBlockingEventTimeAsync(SqlConnection connection, ServerHealthStatus status)
        {
            const string query = @"SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

                SELECT TOP (1)
                    minutes_ago =
                        DATEDIFF(MINUTE, bpx.event_time, SYSDATETIME())
                FROM collect.blocked_process_xml AS bpx
                ORDER BY
                    bpx.id DESC
                OPTION(MAXDOP 1);";

            try
            {
                using var cmd = new SqlCommand(query, connection);
                cmd.CommandTimeout = 10;
                var result = await cmd.ExecuteScalarAsync();
                status.LastBlockingMinutesAgo = result as int?;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to get last blocking event time: {ex.Message}");
            }
        }

        private async Task GetLastDeadlockEventTimeAsync(SqlConnection connection, ServerHealthStatus status)
        {
            const string query = @"SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

                SELECT TOP (1)
                    minutes_ago =
                        DATEDIFF(MINUTE, dx.event_time, SYSDATETIME())
                FROM collect.deadlock_xml AS dx
                ORDER BY
                    dx.id DESC
                OPTION(MAXDOP 1);";

            try
            {
                using var cmd = new SqlCommand(query, connection);
                cmd.CommandTimeout = 10;
                var result = await cmd.ExecuteScalarAsync();
                status.LastDeadlockMinutesAgo = result as int?;
            }
            catch (Exception ex)
            {
                Logger.Warning($"Failed to get last deadlock event time: {ex.Message}");
            }
        }
    }
}
