using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using PerformanceMonitor.Analysis;
using PerformanceMonitor.PlanAnalysis;
using PerformanceMonitorDashboard.Helpers;

namespace PerformanceMonitorDashboard.Analysis;

public partial class SqlServerFactCollector
{
    /// <summary>
    /// Collects wait stats facts — one Fact per significant wait type.
    /// Value is wait_time_ms / period_duration_ms (fraction of examined period).
    /// </summary>
    private async Task CollectWaitStatsFactsAsync(AnalysisContext context, List<Fact> facts)
    {
        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    wait_type,
    SUM(waiting_tasks_count_delta) AS total_waiting_tasks,
    SUM(wait_time_ms_delta) AS total_wait_time_ms,
    SUM(signal_wait_time_ms_delta) AS total_signal_wait_time_ms
FROM collect.wait_stats
WHERE collection_time >= @startTime
AND   collection_time <= @endTime
AND   wait_time_ms_delta > 0
GROUP BY wait_type
ORDER BY SUM(wait_time_ms_delta) DESC";

            command.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
            command.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

            using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var waitType = reader.GetString(0);
                var waitingTasks = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1));
                var waitTimeMs = reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2));
                var signalWaitTimeMs = reader.IsDBNull(3) ? 0L : Convert.ToInt64(reader.GetValue(3));

                if (waitTimeMs <= 0) continue;

                var fractionOfPeriod = waitTimeMs / context.PeriodDurationMs;
                var avgMsPerWait = waitingTasks > 0 ? (double)waitTimeMs / waitingTasks : 0;

                facts.Add(new Fact
                {
                    Source = "waits",
                    Key = waitType,
                    Value = fractionOfPeriod,
                    ServerId = context.ServerId,
                    Metadata = new Dictionary<string, double>
                    {
                        ["wait_time_ms"] = waitTimeMs,
                        ["waiting_tasks_count"] = waitingTasks,
                        ["signal_wait_time_ms"] = signalWaitTimeMs,
                        ["resource_wait_time_ms"] = waitTimeMs - signalWaitTimeMs,
                        ["avg_ms_per_wait"] = avgMsPerWait,
                        ["period_duration_ms"] = context.PeriodDurationMs
                    }
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error("SqlServerFactCollector.CollectWaitStatsFactsAsync failed", ex);
        }
    }

    /// <summary>
    /// Collects blocking facts from blocking_BlockedProcessReport.
    /// Produces a single BLOCKING_EVENTS fact with event count, rate, and details.
    /// Value is events per hour for threshold comparison.
    /// </summary>
    private async Task CollectBlockingFactsAsync(AnalysisContext context, List<Fact> facts)
    {
        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT
    COUNT(*) AS event_count,
    AVG(CAST(wait_time_ms AS FLOAT)) AS avg_wait_time_ms,
    MAX(wait_time_ms) AS max_wait_time_ms,
    COUNT(DISTINCT spid) AS distinct_head_blockers,
    COUNT(CASE WHEN status = 'sleeping' THEN 1 END) AS sleeping_blocker_count
FROM collect.blocking_BlockedProcessReport
WHERE collection_time >= @startTime
AND   collection_time <= @endTime";

            command.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
            command.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return;

            var eventCount = reader.IsDBNull(0) ? 0L : Convert.ToInt64(reader.GetValue(0));
            if (eventCount <= 0) return;

            var avgWaitTimeMs = reader.IsDBNull(1) ? 0.0 : Convert.ToDouble(reader.GetValue(1));
            var maxWaitTimeMs = reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2));
            var distinctHeadBlockers = reader.IsDBNull(3) ? 0L : Convert.ToInt64(reader.GetValue(3));
            var sleepingBlockerCount = reader.IsDBNull(4) ? 0L : Convert.ToInt64(reader.GetValue(4));

            var periodHours = context.PeriodDurationMs / 3_600_000.0;
            var eventsPerHour = periodHours > 0 ? eventCount / periodHours : 0;

            facts.Add(new Fact
            {
                Source = "blocking",
                Key = "BLOCKING_EVENTS",
                Value = eventsPerHour,
                ServerId = context.ServerId,
                Metadata = new Dictionary<string, double>
                {
                    ["event_count"] = eventCount,
                    ["events_per_hour"] = eventsPerHour,
                    ["avg_wait_time_ms"] = avgWaitTimeMs,
                    ["max_wait_time_ms"] = maxWaitTimeMs,
                    ["distinct_head_blockers"] = distinctHeadBlockers,
                    ["sleeping_blocker_count"] = sleepingBlockerCount,
                    ["period_hours"] = periodHours
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error("SqlServerFactCollector.CollectBlockingFactsAsync failed", ex);
        }
    }

    /// <summary>
    /// Collects deadlock facts from the deadlocks table.
    /// Produces a single DEADLOCKS fact with count and rate.
    /// Value is deadlocks per hour for threshold comparison.
    /// </summary>
    private async Task CollectDeadlockFactsAsync(AnalysisContext context, List<Fact> facts)
    {
        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT COUNT(*) AS deadlock_count
FROM collect.deadlocks
WHERE collection_time >= @startTime
AND   collection_time <= @endTime";

            command.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
            command.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

            using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return;

            var deadlockCount = reader.IsDBNull(0) ? 0L : Convert.ToInt64(reader.GetValue(0));
            if (deadlockCount <= 0) return;

            var periodHours = context.PeriodDurationMs / 3_600_000.0;
            var deadlocksPerHour = periodHours > 0 ? deadlockCount / periodHours : 0;

            facts.Add(new Fact
            {
                Source = "blocking",
                Key = "DEADLOCKS",
                Value = deadlocksPerHour,
                ServerId = context.ServerId,
                Metadata = new Dictionary<string, double>
                {
                    ["deadlock_count"] = deadlockCount,
                    ["deadlocks_per_hour"] = deadlocksPerHour,
                    ["period_hours"] = periodHours
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error("SqlServerFactCollector.CollectDeadlockFactsAsync failed", ex);
        }
    }

    /// <summary>
    /// Reconstructs blocking chains from collect.blocking_BlockedProcessReport (one row
    /// per side of each blocking event, dedup'd to <c>activity = 'blocked'</c>) and emits
    /// one aggregate BLOCKING_CHAIN fact describing the worst chain — apex head blocker,
    /// depth, transitive victim count. Structure the BLOCKING_EVENTS rate is blind to.
    /// Reads typed blocker-side columns populated by collect.process_blocked_process_xml,
    /// so no XML re-parse on the analysis hot path.
    /// </summary>
    private async Task CollectBlockingChainFactsAsync(AnalysisContext context, List<Fact> facts)
    {
        const int maxPairs = 5000;
        const int maxDepth = 50;
        const int stepBudget = 100_000;

        try
        {
            using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            using var cmd = connection.CreateCommand();
            // ORDER BY collection_time DESC is a backward CIX scan (sort-free); event_time
            // is a residual predicate. activity='blocked' picks the canonical per-event side.
            // blocking_spid IS NOT NULL filters out rows whose source XML had an empty
            // <blocking-process><process/></blocking-process> (system task / torn-down session) —
            // those can't contribute to a reconstructed chain.
            cmd.CommandText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT TOP (5000)
    event_time,
    database_name,
    spid,
    last_transaction_started,
    blocking_spid,
    blocking_last_tran_started,
    wait_time_ms,
    lock_mode,
    blocking_status,
    blocked_sql_text,
    blocking_sql_text
FROM collect.blocking_BlockedProcessReport
WHERE collection_time >= @collectionWindow
AND   event_time >= @startTime
AND   event_time <= @endTime
AND   activity = 'blocked'
AND   blocking_spid IS NOT NULL
ORDER BY collection_time DESC";

            // Generous bound — analysis window plus an hour — to catch rows whose
            // event_time is inside the window but whose collection_time may lag slightly.
            cmd.Parameters.Add(new SqlParameter("@collectionWindow", context.TimeRangeStart.AddHours(-1)));
            cmd.Parameters.Add(new SqlParameter("@startTime", context.TimeRangeStart));
            cmd.Parameters.Add(new SqlParameter("@endTime", context.TimeRangeEnd));

            var rows = new List<BlockingPairRow>();
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    rows.Add(new BlockingPairRow
                    {
                        EventTime = reader.IsDBNull(0) ? default : reader.GetDateTime(0),
                        DatabaseName = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                        BlockedSpid = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2)),
                        BlockedTranStarted = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3),
                        BlockingSpid = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetValue(4)),
                        BlockingTranStarted = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5),
                        WaitTimeMs = reader.IsDBNull(6) ? 0L : Convert.ToInt64(reader.GetValue(6)),
                        LockMode = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                        BlockingStatus = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                        BlockedSqlText = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                        BlockingSqlText = reader.IsDBNull(10) ? string.Empty : reader.GetString(10)
                    });
                }
            }

            if (rows.Count == 0) return;

            var reconstruction = BlockingChainReconstructor.Reconstruct(rows, maxDepth, maxPairs, stepBudget);
            if (reconstruction.Chains.Count == 0) return;

            var worst = reconstruction.Chains[0];

            facts.Add(new Fact
            {
                Source = "blocking",
                Key = "BLOCKING_CHAIN",
                Value = worst.Depth,
                ServerId = context.ServerId,
                Metadata = new Dictionary<string, double>
                {
                    ["worst_chain_depth"] = worst.Depth,
                    ["worst_chain_victim_count"] = worst.VictimCount,
                    ["worst_apex_spid"] = worst.ApexSpid,
                    ["worst_apex_sleeping"] = worst.ApexSleeping ? 1 : 0,
                    ["worst_chain_max_wait_ms"] = worst.MaxWaitMs,
                    ["total_reconstructed_chains"] = reconstruction.Chains.Count,
                    ["deepest_chain_overall"] = reconstruction.Chains.Max(c => c.Depth),
                    ["max_victim_count_overall"] = reconstruction.Chains.Max(c => c.VictimCount),
                    ["depth_capped"] = reconstruction.DepthCapped ? 1 : 0,
                    ["traversal_truncated"] = reconstruction.TraversalTruncated ? 1 : 0,
                    ["cycle_detected"] = reconstruction.CycleDetected ? 1 : 0
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Error("SqlServerFactCollector.CollectBlockingChainFactsAsync failed", ex);
        }
    }
}
