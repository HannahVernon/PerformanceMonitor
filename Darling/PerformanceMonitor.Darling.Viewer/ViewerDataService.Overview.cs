/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Npgsql;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Overview server-cards read (W2a viewer copy-parity), copied from Lite's
/// <c>LocalDataService.Overview.cs</c> (GetServerSummaryAsync + ServerSummaryItem) and rewired to the
/// Darling Postgres store, then ENRICHED toward the Dashboard's richer <c>ServerHealthCard</c>
/// (Dashboard/Controls/ServerHealthCard.xaml + Dashboard/Models/ServerHealthStatus.cs). Alongside Lite's
/// original five per-server reads — latest CPU (SQL + other), latest total server memory, blocking count
/// in the last hour (XE blocked-process reports, falling back to the always-on DMV snapshot when the XE
/// count is zero), deadlock count in the last hour, and the last collection time — the card now also
/// surfaces the Dashboard's <b>Threads</b> row (worker-thread pressure from the latest
/// <c>cpu_scheduler_stats</c> snapshot), <b>Collectors</b> row (healthy / failing counts reusing the
/// viewer's own <see cref="CollectorHealthRow.HealthStatus"/> banding), a richer <b>Memory</b> signal
/// (resource-semaphore waiters / timeouts / forced grants from <c>memory_grant_stats</c>, not just total
/// MB), and a <b>Blocking</b> duration (max wait in the window). Every metric drives a per-row severity
/// band that mirrors <c>ServerHealthStatus</c>'s deterministic CASE logic. All reads run over the same
/// <c>v_*</c> passthrough views the other viewer tabs read; the SQL lives in public constants so tests can
/// pin the load-bearing clauses without a live Postgres.
///
/// <para><b>The viewer adaptations (#1262 headless plan).</b> (1) Lite's <c>IsOnline</c> comes from a live
/// per-server connection ping; the viewer has no live connection to the monitored servers, so it derives
/// the card's status from COLLECTION FRESHNESS instead — how old the newest <c>v_collection_log</c> row
/// is (see <see cref="ServerSummaryItem.ClassifyFreshness"/>): fresh → Online (green), stale (older than
/// twice the fastest collector's cadence) → Warning (amber), and no collection / long-dead → Offline
/// (the red overlay). (2) The Dashboard's health card live-queries currently-blocked sessions / current
/// scheduler state; the viewer reads the newest COLLECTED snapshot instead (the freshness band already
/// answers "is this server reporting"). The severity BANDS themselves are reproduced verbatim from
/// <c>ServerHealthStatus</c> so a viewer card colours a metric exactly as the Dashboard would.</para>
/// </summary>
public sealed partial class ViewerDataService
{
    /// <summary>Latest SQL + other-process CPU for one server (newest ring-buffer sample). $1 server_id.</summary>
    public const string ServerSummaryCpuSql = @"
SELECT sqlserver_cpu_utilization, other_process_cpu_utilization
FROM v_cpu_utilization_stats
WHERE server_id = $1
ORDER BY sample_time DESC
LIMIT 1";

    /// <summary>Latest total server memory + buffer pool (MB) for one server. $1 server_id.</summary>
    public const string ServerSummaryMemorySql = @"
SELECT
    CAST(total_server_memory_mb AS double precision),
    CAST(buffer_pool_mb AS double precision)
FROM v_memory_stats
WHERE server_id = $1
ORDER BY collection_time DESC
LIMIT 1";

    /// <summary>
    /// The latest resource-semaphore pressure for one server — the workspace-memory signal the Dashboard's
    /// <c>get_resource_semaphore</c> / <c>ServerHealthStatus.MemorySeverity</c> reads: grant waiters,
    /// timeout-error and forced-grant deltas, and total granted MB, summed across every pool at the newest
    /// collection instant. The Dashboard live-sums <c>sys.dm_exec_query_resource_semaphores.waiter_count</c>
    /// (WHERE max_target_memory_kb IS NOT NULL, which the <c>memory_grant_stats</c> collector already
    /// applies at capture); the viewer sums the collected per-pool rows at MAX(collection_time). $1 server_id.
    /// </summary>
    public const string ServerSummaryMemoryPressureSql = @"
SELECT
    CAST(COALESCE(SUM(waiter_count), 0) AS bigint),
    CAST(COALESCE(SUM(timeout_error_count_delta), 0) AS bigint),
    CAST(COALESCE(SUM(forced_grant_count_delta), 0) AS bigint),
    CAST(COALESCE(SUM(granted_memory_mb), 0) AS double precision)
FROM v_memory_grant_stats
WHERE server_id = $1
AND   collection_time = (SELECT MAX(collection_time) FROM v_memory_grant_stats WHERE server_id = $1)";

    /// <summary>
    /// The latest worker-thread pressure for one server — the Dashboard's Threads row inputs
    /// (<c>ServerHealthStatus.ThreadsSeverity</c>) from the newest <c>cpu_scheduler_stats</c> snapshot:
    /// total worker ceiling (<c>max_workers_count</c>), workers in use (<c>total_current_workers_count</c> —
    /// available = ceiling − in-use, the same figure the collector's own worker-thread-exhaustion warning
    /// bands on at 90%), runnable tasks waiting for CPU (<c>total_runnable_tasks_count</c>), and requests
    /// starved of a worker (<c>total_work_queue_count</c>). A point-in-time snapshot collector, so the
    /// newest row is the current state. NULL/absent on Azure SQL DB (the collector does not apply there),
    /// which the card renders as "--". $1 server_id.
    /// </summary>
    public const string ServerSummaryThreadsSql = @"
SELECT
    max_workers_count,
    total_current_workers_count,
    total_runnable_tasks_count,
    total_work_queue_count
FROM v_cpu_scheduler_stats
WHERE server_id = $1
ORDER BY collection_time DESC
LIMIT 1";

    /// <summary>
    /// Blocking in the window with its worst wait, plus the newest blocking event ever — XE blocked-process
    /// reports preferred, the always-on DMV blocking snapshot as fallback (AWS RDS has no XE), keeping
    /// Lite's XE→DMV fallback but returning the COUNT and the MAX wait (ms) from the SAME source so the
    /// card's count and its "max: Ns" duration can never come from different feeds. The last two columns are
    /// each source's newest event_time (unbounded — the same <c>MAX(event_time) WHERE server_id</c> read the
    /// collector runs every cycle for its watermark), for the Dashboard's "Last: N ago" detail when the
    /// window is clear. The caller applies the fallback in C# (identical to Lite's
    /// <c>COALESCE(NULLIF(xe,0), dmv)</c>: use XE when it has any row, else DMV). $1 server_id, $2 window
    /// start (naive UTC).
    /// </summary>
    public const string ServerSummaryBlockingSql = @"
SELECT
    (SELECT COUNT(*)          FROM v_blocked_process_reports WHERE server_id = $1 AND event_time >= $2),
    (SELECT MAX(wait_time_ms) FROM v_blocked_process_reports WHERE server_id = $1 AND event_time >= $2),
    (SELECT COUNT(*)          FROM v_dmv_blocking_snapshots   WHERE server_id = $1 AND event_time >= $2),
    (SELECT MAX(wait_time_ms) FROM v_dmv_blocking_snapshots   WHERE server_id = $1 AND event_time >= $2),
    (SELECT MAX(event_time)   FROM v_blocked_process_reports WHERE server_id = $1),
    (SELECT MAX(event_time)   FROM v_dmv_blocking_snapshots   WHERE server_id = $1)";

    /// <summary>
    /// Deadlock count in the window plus the newest deadlock ever — the windowed count for the card value,
    /// and the unbounded MAX(deadlock_time) for the Dashboard's "Last: N ago" detail. $1 server_id, $2
    /// window start (naive UTC).
    /// </summary>
    public const string ServerSummaryDeadlockSql = @"
SELECT
    (SELECT COUNT(*)           FROM v_deadlocks WHERE server_id = $1 AND deadlock_time >= $2),
    (SELECT MAX(deadlock_time) FROM v_deadlocks WHERE server_id = $1)";

    /// <summary>Newest collection time across all collectors for one server. $1 server_id.</summary>
    public const string ServerSummaryLastCollectionSql = @"
SELECT MAX(collection_time)
FROM v_collection_log
WHERE server_id = $1";

    /// <summary>
    /// One server's Overview-card summary — Lite's <c>GetServerSummaryAsync</c> ported to Postgres and
    /// enriched toward the Dashboard's <c>ServerHealthCard</c>. The caller sets
    /// <see cref="ServerSummaryItem.ServerName"/> and applies the freshness-derived status
    /// (<see cref="ServerSummaryItem.ApplyFreshness"/>) after the read, exactly where Lite set IsOnline
    /// from the live ping. Blocking / deadlock counts use a one-hour window (Lite's window); the Threads /
    /// Memory-pressure reads take the newest snapshot; the Collectors row REUSES the viewer's 7-day
    /// <see cref="GetCollectionHealthAsync"/> banding.
    /// </summary>
    public async Task<ServerSummaryItem> GetServerSummaryAsync(int serverId, string displayName, CancellationToken cancellationToken = default)
    {
        var nowUtc = DateTime.UtcNow;
        var windowStart = DateTime.SpecifyKind(nowUtc.AddHours(-1), DateTimeKind.Unspecified);

        double? cpuPercent = null;
        double? otherProcessCpuPercent = null;
        double? memoryMb = null;
        double? bufferPoolMb = null;
        var blockingCount = 0;
        long maxBlockingWaitMs = 0;
        int? lastBlockingMinutesAgo = null;
        var deadlockCount = 0;
        int? lastDeadlockMinutesAgo = null;
        DateTime? lastCollection = null;

        int? totalThreads = null;
        int? currentWorkers = null;
        var threadsWaitingForCpu = 0;
        long requestsWaitingForThreads = 0;

        long memoryWaiterCount = 0;
        long memoryTimeoutCount = 0;
        long memoryForcedCount = 0;
        double? grantedMemoryMb = null;

        /* Latest CPU — SQL and other-process, so the card can show total non-idle CPU with the SQL-only
           number alongside (Lite's headline). */
        await using (var command = _dataSource.CreateCommand(ServerSummaryCpuSql))
        {
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                cpuPercent = reader.IsDBNull(0) ? null : Convert.ToDouble(reader.GetValue(0));
                otherProcessCpuPercent = reader.IsDBNull(1) ? null : Convert.ToDouble(reader.GetValue(1));
            }
        }

        /* Latest total server memory + buffer pool. */
        await using (var command = _dataSource.CreateCommand(ServerSummaryMemorySql))
        {
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                memoryMb = reader.IsDBNull(0) ? null : Convert.ToDouble(reader.GetValue(0));
                bufferPoolMb = reader.IsDBNull(1) ? null : Convert.ToDouble(reader.GetValue(1));
            }
        }

        /* Latest resource-semaphore pressure (grant waiters / timeouts / forced grants + granted MB). */
        await using (var command = _dataSource.CreateCommand(ServerSummaryMemoryPressureSql))
        {
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                memoryWaiterCount = reader.IsDBNull(0) ? 0 : Convert.ToInt64(reader.GetValue(0));
                memoryTimeoutCount = reader.IsDBNull(1) ? 0 : Convert.ToInt64(reader.GetValue(1));
                memoryForcedCount = reader.IsDBNull(2) ? 0 : Convert.ToInt64(reader.GetValue(2));
                grantedMemoryMb = reader.IsDBNull(3) ? null : Convert.ToDouble(reader.GetValue(3));
            }
        }

        /* Latest worker-thread pressure (max / in-use / runnable-waiting / work-queue). Absent on Azure. */
        await using (var command = _dataSource.CreateCommand(ServerSummaryThreadsSql))
        {
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                totalThreads = reader.IsDBNull(0) ? null : Convert.ToInt32(reader.GetValue(0));
                currentWorkers = reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1));
                threadsWaitingForCpu = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
                requestsWaitingForThreads = reader.IsDBNull(3) ? 0 : Convert.ToInt64(reader.GetValue(3));
            }
        }

        /* Blocking count + worst wait in the last hour (XE preferred, DMV fallback — same source for both). */
        await using (var command = _dataSource.CreateCommand(ServerSummaryBlockingSql))
        {
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
            command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = windowStart });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var xeCount = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                var xeMaxWait = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1));
                var dmvCount = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));
                var dmvMaxWait = reader.IsDBNull(3) ? 0L : Convert.ToInt64(reader.GetValue(3));

                /* Lite's fallback: use XE when it has any row this window, else the DMV snapshot. Both the
                   count and the max wait come from whichever source wins, so they never disagree. */
                if (xeCount > 0)
                {
                    blockingCount = xeCount;
                    maxBlockingWaitMs = xeMaxWait;
                }
                else
                {
                    blockingCount = dmvCount;
                    maxBlockingWaitMs = dmvMaxWait;
                }

                /* Newest blocking event across both sources (unbounded) → "Last: N ago" when the window is
                   clear. Stored times are naive UTC; the tick subtraction against UtcNow is a true elapsed. */
                DateTime? lastBlocking = reader.IsDBNull(4) ? null : reader.GetDateTime(4);
                var dmvLast = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);
                if (dmvLast.HasValue && (!lastBlocking.HasValue || dmvLast.Value > lastBlocking.Value))
                {
                    lastBlocking = dmvLast;
                }
                lastBlockingMinutesAgo = MinutesAgo(lastBlocking, nowUtc);
            }
        }

        /* Deadlock count in the last hour + the newest deadlock ever (for "Last: N ago"). */
        await using (var command = _dataSource.CreateCommand(ServerSummaryDeadlockSql))
        {
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
            command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = windowStart });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                deadlockCount = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
                lastDeadlockMinutesAgo = MinutesAgo(reader.IsDBNull(1) ? null : reader.GetDateTime(1), nowUtc);
            }
        }

        /* Newest collection time across all collectors — drives the freshness status. */
        await using (var command = _dataSource.CreateCommand(ServerSummaryLastCollectionSql))
        {
            command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
            var result = await command.ExecuteScalarAsync(cancellationToken);
            if (result is not null && result != DBNull.Value)
            {
                lastCollection = Convert.ToDateTime(result);
            }
        }

        /* Collectors row — REUSE the viewer's own 7-day per-collector health banding (the same STALE /
           FAILING / NEVER_RUN / HEALTHY logic the Collection Health tab renders), mirroring the Dashboard's
           SUM(CASE health_status = 'HEALTHY' / 'FAILING') over report.collection_health. */
        var (healthyCollectors, failingCollectors) = await GetCollectorHealthCountsAsync(serverId, cancellationToken);

        return new ServerSummaryItem
        {
            DisplayName = displayName,
            ServerId = serverId,
            CpuPercent = cpuPercent,
            OtherProcessCpuPercent = otherProcessCpuPercent,
            MemoryMb = memoryMb,
            BufferPoolMb = bufferPoolMb,
            GrantedMemoryMb = grantedMemoryMb,
            MemoryWaiterCount = memoryWaiterCount,
            MemoryTimeoutCount = memoryTimeoutCount,
            MemoryForcedCount = memoryForcedCount,
            BlockingCount = blockingCount,
            MaxBlockingWaitMs = maxBlockingWaitMs,
            LastBlockingMinutesAgo = lastBlockingMinutesAgo,
            DeadlockCount = deadlockCount,
            LastDeadlockMinutesAgo = lastDeadlockMinutesAgo,
            TotalThreads = totalThreads,
            CurrentWorkers = currentWorkers,
            ThreadsWaitingForCpu = threadsWaitingForCpu,
            RequestsWaitingForThreads = requestsWaitingForThreads,
            HealthyCollectorCount = healthyCollectors,
            FailedCollectorCount = failingCollectors,
            LastCollectionTime = lastCollection,
        };
    }

    /// <summary>
    /// Healthy / failing collector counts for one server, derived from the viewer's own 7-day collection
    /// health banding (<see cref="GetCollectionHealthAsync"/> → <see cref="CollectorHealthRow.HealthStatus"/>).
    /// Mirrors the Dashboard's <c>GetCollectorStatusAsync</c> (SUM of HEALTHY / FAILING over
    /// <c>report.collection_health</c>) — HEALTHY and FAILING are the two bands the card surfaces; the
    /// STALE / WARNING / NO_PERMISSIONS / NEVER_RUN rows count as neither (a failing collector is one the
    /// banding calls FAILING: no success in over 24h).
    /// </summary>
    private async Task<(int Healthy, int Failing)> GetCollectorHealthCountsAsync(int serverId, CancellationToken cancellationToken)
    {
        var rows = await GetCollectionHealthAsync(serverId, cancellationToken);
        var healthy = rows.Count(r => r.HealthStatus == "HEALTHY");
        var failing = rows.Count(r => r.HealthStatus == "FAILING");
        return (healthy, failing);
    }

    /// <summary>Whole minutes elapsed from a stored naive-UTC instant to now (UTC), floored at 0, or null
    /// when there is no instant. Both sides are UTC instants, so the tick subtraction is a true elapsed
    /// regardless of Kind (the same reasoning the freshness classification documents).</summary>
    private static int? MinutesAgo(DateTime? instantUtc, DateTime nowUtc) =>
        instantUtc.HasValue ? Math.Max(0, (int)(nowUtc - instantUtc.Value).TotalMinutes) : null;
}

/// <summary>The three collection-freshness bands the viewer derives a card's status from.</summary>
public enum ServerFreshness
{
    /// <summary>The newest collection is within twice the fastest collector's cadence — Online (green).</summary>
    Fresh,

    /// <summary>Collection has lagged past twice the cadence but the server isn't long-dead — Warning (amber).</summary>
    Stale,

    /// <summary>No collection at all, or the newest is long-dead — the Offline overlay (red).</summary>
    Offline,
}

/// <summary>
/// Per-metric health bands for the Overview card's severity dots, a verbatim mirror of the Dashboard's
/// <c>PerformanceMonitorDashboard.Models.HealthSeverity</c>. <see cref="Unknown"/> is a metric with no
/// collected data (e.g. Threads on Azure SQL DB) — a grey dot, and it never escalates the card's overall
/// band.
/// </summary>
public enum HealthSeverity
{
    Unknown,
    Healthy,
    Warning,
    Critical,
}

/// <summary>
/// One Overview server card's view-model — copied from Lite's <c>ServerSummaryItem</c>
/// (Lite/Services/LocalDataService.Overview.cs) and enriched toward the Dashboard's
/// <c>ServerHealthStatus</c> (Threads / Collectors rows, the resource-semaphore Memory signal, blocking
/// duration, and a per-metric severity band for each row's dot). Two viewer adaptations carry over from
/// the headless plan (#1262): <see cref="CpuPercentForAlert"/> is always total non-idle CPU (the viewer
/// has no per-app <c>CpuAlertMode</c> preference — total is what the alert engine evaluates by default),
/// and the connection status is derived from collection freshness rather than a live ping (see
/// <see cref="ClassifyFreshness"/> / <see cref="ApplyFreshness"/>). Every severity BAND reproduces
/// <c>ServerHealthStatus</c>'s deterministic CASE logic so a viewer card colours a metric exactly as the
/// Dashboard would; every display / brush is otherwise a pure format of the stored values.
/// </summary>
public sealed class ServerSummaryItem
{
    /// <summary>
    /// The fastest scheduled collector's cadence (wait_stats / cpu_utilization / memory_stats etc. all
    /// run every minute — see <c>CollectorScheduleDefaults</c>), so MAX(collection_time) tracks a
    /// one-minute rhythm on a healthy server. Freshness bands are multiples of this.
    /// </summary>
    private static readonly TimeSpan s_collectorCadence = TimeSpan.FromMinutes(1);

    /// <summary>Older than twice the cadence = the collection has visibly lagged (Warning).</summary>
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromTicks(s_collectorCadence.Ticks * 2);

    /// <summary>Older than this (or no collection at all) = the server is treated as Offline.</summary>
    public static readonly TimeSpan OfflineThreshold = TimeSpan.FromMinutes(15);

    /* Per-severity dot / value brushes, frozen once (the viewer's dark-theme palette). */
    private static readonly SolidColorBrush s_criticalBrush = MakeBrush("#E57373");
    private static readonly SolidColorBrush s_warningBrush = MakeBrush("#FFD54F");
    private static readonly SolidColorBrush s_healthyBrush = MakeBrush("#81C784");
    private static readonly SolidColorBrush s_unknownBrush = MakeBrush("#888888");

    public string DisplayName { get; set; } = "";
    public string ServerName { get; set; } = "";
    public int ServerId { get; set; }
    public bool? IsOnline { get; set; }

    /// <summary>Warning (amber) state — in the viewer this means the collection has gone stale.</summary>
    public bool HasCollectorErrors { get; set; }

    /// <summary>SQL Server scheduler ProcessUtilization from sys.dm_os_ring_buffers. NULL on Azure SQL DB.</summary>
    public double? CpuPercent { get; set; }

    /// <summary>Non-SQL-Server CPU on the host (100 - SystemIdle - ProcessUtilization). NULL on Azure SQL DB.</summary>
    public double? OtherProcessCpuPercent { get; set; }

    /// <summary>Total non-idle CPU on the host = sql_server + other_process. Tracks OS user+system counters.</summary>
    public double? TotalCpuPercent =>
        CpuPercent.HasValue ? CpuPercent.Value + (OtherProcessCpuPercent ?? 0) : null;

    /// <summary>
    /// The CPU value the headline display / colour band uses. The viewer has no per-app CpuAlertMode, so
    /// it always uses total non-idle CPU (falling back to SQL-only when other-process is unavailable) —
    /// what the alert engine evaluates by default.
    /// </summary>
    public double? CpuPercentForAlert => TotalCpuPercent ?? CpuPercent;

    public double? MemoryMb { get; set; }

    /// <summary>Latest buffer-pool MB (v_memory_stats.buffer_pool_mb) — the "BP" figure in the Memory detail.</summary>
    public double? BufferPoolMb { get; set; }

    /// <summary>Total granted query-memory MB across all pools at the newest grant snapshot — the "QMG" figure.</summary>
    public double? GrantedMemoryMb { get; set; }

    /// <summary>Grant waiters at the newest snapshot — the primary resource-semaphore pressure signal.</summary>
    public long MemoryWaiterCount { get; set; }

    /// <summary>Grant timeout-error delta at the newest snapshot (a query gave up waiting for memory).</summary>
    public long MemoryTimeoutCount { get; set; }

    /// <summary>Forced-grant delta at the newest snapshot (a grant was forced through under pressure).</summary>
    public long MemoryForcedCount { get; set; }

    public int BlockingCount { get; set; }

    /// <summary>The worst blocking wait (ms) observed in the window — the "max: Ns" detail + Critical band input.</summary>
    public long MaxBlockingWaitMs { get; set; }

    /// <summary>Minutes since the most recent blocking event ever — the "Last: N ago" detail when the window is clear.</summary>
    public int? LastBlockingMinutesAgo { get; set; }

    public int DeadlockCount { get; set; }

    /// <summary>Minutes since the most recent deadlock ever — the "Last: N ago" deadlock detail.</summary>
    public int? LastDeadlockMinutesAgo { get; set; }

    /// <summary>Worker-thread ceiling (max_workers_count). NULL = no scheduler snapshot (e.g. Azure SQL DB).</summary>
    public int? TotalThreads { get; set; }

    /// <summary>Workers in use (total_current_workers_count); available = ceiling − in-use.</summary>
    public int? CurrentWorkers { get; set; }

    /// <summary>Runnable tasks waiting for a CPU (total_runnable_tasks_count).</summary>
    public int ThreadsWaitingForCpu { get; set; }

    /// <summary>Requests starved of a worker thread (total_work_queue_count) — thread-pool starvation.</summary>
    public long RequestsWaitingForThreads { get; set; }

    /// <summary>Available worker threads = ceiling − in-use, or NULL when there is no scheduler snapshot.</summary>
    public int? AvailableThreads =>
        TotalThreads.HasValue ? TotalThreads.Value - (CurrentWorkers ?? 0) : null;

    /// <summary>Collectors whose 7-day band is HEALTHY (mirrors report.collection_health).</summary>
    public int HealthyCollectorCount { get; set; }

    /// <summary>Collectors whose 7-day band is FAILING (no success in over 24h).</summary>
    public int FailedCollectorCount { get; set; }

    public DateTime? LastCollectionTime { get; set; }

    /// <summary>
    /// Headline CPU display: total non-idle CPU prominently with the SQL-only number alongside, e.g.
    /// "64% (SQL 60%)". Falls back to a single number when only one value is available.
    /// </summary>
    public string CpuDisplay
    {
        get
        {
            if (!CpuPercent.HasValue) return "--";
            if (!OtherProcessCpuPercent.HasValue) return $"{CpuPercent:F0}%";
            return $"{TotalCpuPercent:F0}% (SQL {CpuPercent:F0}%)";
        }
    }

    /// <summary>The non-SQL host CPU alongside the headline (the Dashboard's CPU detail), when known.</summary>
    public string CpuDetail => OtherProcessCpuPercent.HasValue ? $"Other: {OtherProcessCpuPercent:F0}%" : "";

    public string MemoryDisplay => MemoryMb.HasValue ? $"{MemoryMb / 1024.0:F1} GB" : "--";

    /// <summary>
    /// The Memory detail: under resource-semaphore pressure it names the pressure (grant waiters, then
    /// timeouts / forced grants when present) — mirroring the Dashboard's "N waiting"; when calm it shows
    /// the buffer-pool and query-memory-grant sizes (the Dashboard's "BP: x, QMG: y").
    /// </summary>
    public string MemoryDetail
    {
        get
        {
            if (HasMemoryPressure)
            {
                var parts = new List<string>();
                if (MemoryWaiterCount > 0) parts.Add($"{MemoryWaiterCount} grant waiter{(MemoryWaiterCount == 1 ? "" : "s")}");
                if (MemoryTimeoutCount > 0) parts.Add($"{MemoryTimeoutCount} timeout{(MemoryTimeoutCount == 1 ? "" : "s")}");
                if (MemoryForcedCount > 0) parts.Add($"{MemoryForcedCount} forced");
                return string.Join(", ", parts);
            }

            var sizes = new List<string>();
            if (BufferPoolMb.HasValue) sizes.Add($"BP {BufferPoolMb.Value / 1024.0:F1}");
            if (GrantedMemoryMb is > 0) sizes.Add($"QMG {GrantedMemoryMb.Value / 1024.0:F1}");
            return sizes.Count > 0 ? string.Join(", ", sizes) + " GB" : "";
        }
    }

    public string BlockingDisplay => BlockingCount > 0 ? BlockingCount.ToString() : "0";

    /// <summary>
    /// The blocking detail (Dashboard's BlockingDetailText): the worst wait while blocking is present in
    /// the window ("max: 42s"), else how long since the last blocking event ever ("Last: 3h ago"), else blank.
    /// </summary>
    public string BlockingDetail
    {
        get
        {
            if (BlockingCount > 0) return $"max: {MaxBlockedSeconds:F0}s";
            if (LastBlockingMinutesAgo.HasValue) return $"Last: {FormatMinutesAgo(LastBlockingMinutesAgo.Value)}";
            return "";
        }
    }

    /// <summary>The worst blocking wait in the window, in seconds.</summary>
    public double MaxBlockedSeconds => MaxBlockingWaitMs / 1000.0;

    public string DeadlockDisplay => DeadlockCount > 0 ? DeadlockCount.ToString() : "0";

    /// <summary>The deadlock detail — how long since the last deadlock ever ("Last: N ago"), else blank.</summary>
    public string DeadlockDetail =>
        LastDeadlockMinutesAgo.HasValue ? $"Last: {FormatMinutesAgo(LastDeadlockMinutesAgo.Value)}" : "";

    /// <summary>Threads value — the pressure headline (Dashboard's ThreadsDisplayText), or "--" with no snapshot.</summary>
    public string ThreadsDisplay
    {
        get
        {
            if (!TotalThreads.HasValue) return "--";
            if (RequestsWaitingForThreads > 0) return $"{RequestsWaitingForThreads} starved";
            if (ThreadsWaitingForCpu >= 20) return $"{ThreadsWaitingForCpu} runnable";
            if (TotalThreads.Value > 0 && AvailableThreads < TotalThreads.Value * 0.10) return "Low";
            return "OK";
        }
    }

    /// <summary>Threads detail — "Available: in/ceiling" (Dashboard's ThreadsDetailText); blank with no snapshot.</summary>
    public string ThreadsDetail =>
        TotalThreads is > 0 ? $"Available: {AvailableThreads}/{TotalThreads}" : "";

    /// <summary>Collectors value — "N failed" or "OK" (Dashboard's CollectorDisplayText).</summary>
    public string CollectorDisplay => FailedCollectorCount > 0 ? $"{FailedCollectorCount} failed" : "OK";

    /// <summary>Collectors detail — "Healthy: N, Failing: M" (Dashboard's CollectorDetailText).</summary>
    public string CollectorDetail => $"Healthy: {HealthyCollectorCount}, Failing: {FailedCollectorCount}";

    /// <summary>
    /// The stored collection_time is naive UTC; the viewer shows it in the viewer machine's local time
    /// (the viewer convention — Lite used its per-server offset helper instead).
    /// </summary>
    public string LastCollectionDisplay => LastCollectionTime.HasValue
        ? ViewerTimeHelper.ForDisplay(LastCollectionTime.Value).ToString("HH:mm:ss")
        : "Never";

    /* Connection status — verbatim from Lite; in the viewer the inputs come from ApplyFreshness. */
    public string StatusDisplay => IsOnline switch
    {
        true when HasCollectorErrors => "Warning",
        true => "Online",
        false => "Offline",
        _ => "Unknown"
    };

    public SolidColorBrush StatusBrush => MakeBrush(IsOnline switch
    {
        true when HasCollectorErrors => "#FFD54F",  // amber — stale collection
        true => "#81C784",
        false => "#E57373",
        _ => "#888888"
    });

    public bool IsOffline => IsOnline == false;

    // ── Per-metric severity bands (verbatim mirrors of ServerHealthStatus's CASE logic) ──────────────

    /// <summary>CPU band — total non-idle CPU: ≥95% Critical, ≥80% Warning (ServerHealthStatus.CpuSeverity).</summary>
    public HealthSeverity CpuSeverity
    {
        get
        {
            var total = CpuPercentForAlert;
            if (!total.HasValue) return HealthSeverity.Unknown;
            if (total >= 95) return HealthSeverity.Critical;
            if (total >= 80) return HealthSeverity.Warning;
            return HealthSeverity.Healthy;
        }
    }

    /// <summary>True when the resource semaphore shows grant waiters, timeouts, or forced grants.</summary>
    public bool HasMemoryPressure => MemoryWaiterCount > 0 || MemoryTimeoutCount > 0 || MemoryForcedCount > 0;

    /// <summary>
    /// Memory band — Critical on resource-semaphore pressure, else Healthy. The Dashboard's
    /// <c>MemorySeverity</c> flags Critical on <c>waiter_count &gt; 0</c>; the viewer additionally treats
    /// recent grant timeouts / forced grants (collected as deltas) as the same workspace-memory pressure.
    /// </summary>
    public HealthSeverity MemorySeverity =>
        HasMemoryPressure ? HealthSeverity.Critical : HealthSeverity.Healthy;

    /// <summary>
    /// Blocking band — mirrors <c>ServerHealthStatus.BlockingSeverity</c>: ≥60s max wait or ≥5 events
    /// Critical; ≥10s max wait, ≥2 events, or any blocking at all Warning.
    /// </summary>
    public HealthSeverity BlockingSeverity
    {
        get
        {
            if (MaxBlockedSeconds >= 60) return HealthSeverity.Critical;
            if (BlockingCount >= 5) return HealthSeverity.Critical;
            if (MaxBlockedSeconds >= 10) return HealthSeverity.Warning;
            if (BlockingCount >= 2) return HealthSeverity.Warning;
            if (BlockingCount > 0) return HealthSeverity.Warning;
            return HealthSeverity.Healthy;
        }
    }

    /// <summary>
    /// Deadlock band — any deadlock in the window is Critical. The Dashboard bands on a cumulative-counter
    /// delta + minutes-since; the viewer has a windowed count (one-hour window), so a deadlock this window
    /// is the viewer's Critical, matching Lite's red-when-&gt;0 emphasis.
    /// </summary>
    public HealthSeverity DeadlockSeverity =>
        DeadlockCount > 0 ? HealthSeverity.Critical : HealthSeverity.Healthy;

    /// <summary>
    /// Threads band — mirrors <c>ServerHealthStatus.ThreadsSeverity</c>: work-queue starvation Critical;
    /// ≥20 runnable-waiting or under 10% workers available Warning. Unknown when there is no snapshot.
    /// </summary>
    public HealthSeverity ThreadsSeverity
    {
        get
        {
            if (!TotalThreads.HasValue) return HealthSeverity.Unknown;
            if (RequestsWaitingForThreads > 0) return HealthSeverity.Critical;
            if (ThreadsWaitingForCpu >= 20) return HealthSeverity.Warning;
            if (TotalThreads.Value > 0 && AvailableThreads < TotalThreads.Value * 0.10) return HealthSeverity.Warning;
            return HealthSeverity.Healthy;
        }
    }

    /// <summary>Collectors band — any FAILING collector is Warning (ServerHealthStatus.CollectorSeverity).</summary>
    public HealthSeverity CollectorSeverity =>
        FailedCollectorCount > 0 ? HealthSeverity.Warning : HealthSeverity.Healthy;

    /// <summary>
    /// The card's worst metric band (offline handled separately by the border / overlay). Unknown and
    /// Healthy never escalate — matching <c>ServerHealthStatus.OverallSeverity</c>'s reduce.
    /// </summary>
    public HealthSeverity OverallMetricSeverity
    {
        get
        {
            var worst = HealthSeverity.Healthy;
            foreach (var s in new[] { CpuSeverity, MemorySeverity, BlockingSeverity, ThreadsSeverity, DeadlockSeverity, CollectorSeverity })
            {
                if (s == HealthSeverity.Critical) return HealthSeverity.Critical;
                if (s == HealthSeverity.Warning) worst = HealthSeverity.Warning;
            }
            return worst;
        }
    }

    // ── Per-metric dot / value brushes ───────────────────────────────────────────────────────────────

    public SolidColorBrush CpuSeverityBrush => SeverityBrush(CpuSeverity);
    public SolidColorBrush MemorySeverityBrush => SeverityBrush(MemorySeverity);
    public SolidColorBrush BlockingSeverityBrush => SeverityBrush(BlockingSeverity);
    public SolidColorBrush DeadlockSeverityBrush => SeverityBrush(DeadlockSeverity);
    public SolidColorBrush ThreadsSeverityBrush => SeverityBrush(ThreadsSeverity);
    public SolidColorBrush CollectorSeverityBrush => SeverityBrush(CollectorSeverity);

    public bool HasAlerts => BlockingCount > 0 || DeadlockCount > 0;

    /// <summary>
    /// The card border reflects the worst signal: offline (red) &gt; a Critical metric (red) &gt; a Warning
    /// metric (amber-orange) &gt; a stale collection (amber) &gt; calm (dark). Enriches Lite's border (which
    /// only knew CPU / blocking / deadlock) with the added Threads / Memory / Collectors bands via
    /// <see cref="OverallMetricSeverity"/>.
    /// </summary>
    public SolidColorBrush CardBorderBrush
    {
        get
        {
            if (IsOnline == false) return s_criticalBrush;
            return OverallMetricSeverity switch
            {
                HealthSeverity.Critical => s_criticalBrush,
                HealthSeverity.Warning => s_warningBrush,
                _ => HasCollectorErrors ? MakeBrush("#FFD54F") : MakeBrush("#2a2d35"),
            };
        }
    }

    /// <summary>
    /// The viewer's status derivation (#1262): classify how fresh the newest collection is. Pure over
    /// (last-collection, now) so it can be pinned without a store. Both instants are UTC (the store is
    /// naive UTC; <paramref name="nowUtc"/> is <see cref="DateTime.UtcNow"/>), so the subtraction is a
    /// true elapsed-time regardless of Kind.
    /// </summary>
    public static ServerFreshness ClassifyFreshness(DateTime? lastCollectionUtc, DateTime nowUtc)
    {
        if (!lastCollectionUtc.HasValue) return ServerFreshness.Offline;

        var age = nowUtc - lastCollectionUtc.Value;
        if (age > OfflineThreshold) return ServerFreshness.Offline;
        if (age > StaleThreshold) return ServerFreshness.Stale;
        return ServerFreshness.Fresh;
    }

    /// <summary>
    /// Maps the freshness band onto Lite's card inputs, taking the live-ping's place: Fresh → Online,
    /// Stale → the amber Warning state, Offline → the red Offline overlay.
    /// </summary>
    public void ApplyFreshness(DateTime nowUtc)
    {
        var freshness = ClassifyFreshness(LastCollectionTime, nowUtc);
        IsOnline = freshness != ServerFreshness.Offline;
        HasCollectorErrors = freshness == ServerFreshness.Stale;
    }

    private static SolidColorBrush SeverityBrush(HealthSeverity severity) => severity switch
    {
        HealthSeverity.Critical => s_criticalBrush,
        HealthSeverity.Warning => s_warningBrush,
        HealthSeverity.Healthy => s_healthyBrush,
        _ => s_unknownBrush,
    };

    /// <summary>Human "N ago" rendering of an elapsed minutes count — verbatim from ServerHealthStatus.</summary>
    private static string FormatMinutesAgo(int minutes)
    {
        if (minutes < 1) return "just now";
        if (minutes < 60) return $"{minutes}m ago";
        if (minutes < 1440) return $"{minutes / 60}h ago";   // 24 hours
        if (minutes < 10080) return $"{minutes / 1440}d ago"; // 7 days
        return $"{minutes / 10080}w ago";
    }

    private static SolidColorBrush MakeBrush(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
