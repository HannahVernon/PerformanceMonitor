using System;
using System.Collections.Generic;

namespace PerformanceMonitor.Analysis;

/// <summary>
/// A single block of operator-facing advice for a fact-key.
/// <para>
/// <see cref="Headline"/> is the one-line summary used as a section heading.
/// <see cref="Investigation"/> tells the operator where to look first.
/// <see cref="Remediation"/> tells them what to consider doing.
/// <see cref="RemediationTsql"/> is populated by <see cref="FactRemediation"/>
/// when a finding's drill-down carries the IDs needed to generate a
/// copy-paste-ready statement.
/// </para>
/// </summary>
public sealed record AdviceBlock(
    string Headline,
    string Investigation,
    string Remediation,
    string? RemediationTsql = null);

/// <summary>
/// Static lookup mapping fact-keys (the same constants emitted by
/// <see cref="FactScorer"/>) to operator-facing advice prose. Both Lite and
/// Dashboard render the same content from this table so the user reads the
/// same diagnosis regardless of which app surfaced the finding.
///
/// <para>
/// Dynamic keys (BAD_ACTOR_{hash}, ANOMALY_WAIT_{type}) are resolved by
/// prefix in <see cref="GetForFactKey"/>. The wait-anomaly composer reuses
/// the inner wait's prose with an anomaly-framing prelude.
/// </para>
/// </summary>
public static class FactAdvice
{
    /// <summary>
    /// Looks up advice for a fact-key. Returns null if the key is unknown.
    /// </summary>
    public static AdviceBlock? GetForFactKey(string? factKey)
    {
        if (string.IsNullOrEmpty(factKey))
            return null;

        if (_byKey.TryGetValue(factKey, out var direct))
            return direct;

        if (factKey.StartsWith("BAD_ACTOR_", StringComparison.OrdinalIgnoreCase))
            return _byKey.GetValueOrDefault("BAD_ACTOR");

        if (factKey.StartsWith("ANOMALY_WAIT_", StringComparison.OrdinalIgnoreCase))
        {
            var inner = factKey.Substring("ANOMALY_WAIT_".Length);
            if (string.IsNullOrEmpty(inner))
                return null;
            return ComposeAnomalyWaitAdvice(inner);
        }

        return null;
    }

    /// <summary>
    /// Convenience: looks up the advice for a finding's root fact and composes
    /// it with any generated remediation T-SQL. Returns null if no advice
    /// matches the root fact key.
    /// </summary>
    public static AdviceBlock? GetForFinding(AnalysisFinding finding)
    {
        if (finding is null)
            return null;

        var advice = GetForFactKey(finding.RootFactKey);
        if (advice is null)
            return null;

        var tsql = FactRemediation.GenerateForFinding(finding);
        return tsql is null ? advice : advice with { RemediationTsql = tsql };
    }

    /// <summary>
    /// Wraps the inner wait-type's advice with an anomaly-framing prelude so
    /// an ANOMALY_WAIT_THREADPOOL finding reads as "this wait is anomalously
    /// elevated vs. baseline" rather than "this wait crossed a static
    /// threshold". Falls back to the bare anomaly framing if the inner wait
    /// has no first-class advice.
    /// </summary>
    private static AdviceBlock ComposeAnomalyWaitAdvice(string innerWaitType)
    {
        const string prelude =
            "This wait is anomalously elevated compared to its own baseline for this " +
            "time bucket — it is a deviation from normal, not necessarily a sustained " +
            "problem. Check whether the elevation coincides with a workload change, a " +
            "deploy, or an external event before treating it as a chronic issue.";

        if (_byKey.TryGetValue(innerWaitType, out var inner))
        {
            return new AdviceBlock(
                Headline: $"Anomalous spike in {innerWaitType} vs. baseline",
                Investigation: prelude + " " + inner.Investigation,
                Remediation: inner.Remediation);
        }

        return new AdviceBlock(
            Headline: $"Anomalous spike in {innerWaitType} vs. baseline",
            Investigation:
                prelude + " Pull the per-collection wait totals from the analysis window " +
                "and compare them against the same time-of-day buckets over the previous " +
                "two weeks to see how isolated this spike is.",
            Remediation:
                "Resolve the underlying wait the way you would normally — the anomaly " +
                "framing only tells you that the wait is unusual right now, not what to " +
                "do about it. If the wait persists across multiple analysis windows, treat " +
                "it as a sustained problem instead of an anomaly.");
    }

    private static readonly Dictionary<string, AdviceBlock> _byKey = BuildAdviceTable();

    private static Dictionary<string, AdviceBlock> BuildAdviceTable()
    {
        var t = new Dictionary<string, AdviceBlock>(StringComparer.OrdinalIgnoreCase);

        // ─────────────────────────────────────────────────────────────────
        // CPU pressure
        // ─────────────────────────────────────────────────────────────────

        t["SOS_SCHEDULER_YIELD"] = new AdviceBlock(
            Headline:
                "Scheduler yields are dominating waits — workers are giving up the CPU because there is more demand than capacity",
            Investigation:
                "Check `sys.dm_os_schedulers` for runnable_tasks_count per scheduler — sustained values above 1-2 mean queries are queueing for CPU. Cross-reference with CPU_SQL_PERCENT to confirm SQL is the consumer (not another process on the box). If CXPACKET is also elevated, parallelism is eating schedulers; if THREADPOOL is climbing, you are at the edge of thread exhaustion.",
            Remediation:
                "This is a CPU capacity or query-cost problem — fix the workload before adding cores. Find the most expensive queries by total CPU time in `sys.dm_exec_query_stats` and address them (missing indexes, scans on huge tables, runaway parameter sniffing). If CXPACKET is co-elevated, raise `cost threshold for parallelism` from the default 5 to 50 and set `MAXDOP` to a sane value (half of cores up to 8 is a common starting point).");

        t["THREADPOOL"] = new AdviceBlock(
            Headline:
                "THREADPOOL waits — SQL Server has run out of worker threads and new sessions are queuing for one",
            Investigation:
                "This is severe — any wait time on THREADPOOL means queries cannot start until a worker frees up. Check `sys.dm_os_workers` and `sys.dm_os_threads` for total worker count, and look at `max worker threads` in `sys.configurations` (a setting of 0 lets SQL calculate it from cores). The most common cause is long blocking chains pinning workers; check BLOCKING_CHAIN drill-down for sleeping apex blockers and stuck transactions.",
            Remediation:
                "Resolve the underlying contention before touching `max worker threads`. Kill abandoned head-blocker sessions with open transactions, fix the queries causing the blocking, and reduce parallelism if CXPACKET is co-elevated (each parallel query consumes one worker per scheduler it touches). Raising `max worker threads` without fixing the root cause masks the symptom and trades thread exhaustion for memory pressure.");

        t["CXPACKET"] = new AdviceBlock(
            Headline:
                "Parallelism waits — queries are spending real time waiting for their parallel workers to synchronize",
            Investigation:
                "Check `cost threshold for parallelism` and `max degree of parallelism` in `sys.configurations` — the defaults (5 and 0) are wrong for most modern workloads. The QUERY_HIGH_DOP amplifier signals queries running at DOP > 8, which usually overwhelms the scheduler. Look for parallel queries doing scans of large tables (missing indexes), or skewed parallelism (one branch doing all the work while the others wait). `sp_BlitzCache @sort_order = 'cpu'` will rank the parallel offenders.",
            Remediation:
                "Set `cost threshold for parallelism` to 50 as a first cut — most OLTP queries should not go parallel at all. Set `MAXDOP` to half the cores in a NUMA node, capped at 8. CXCONSUMER (grouped into CXPACKET by the collector) is benign on its own; the problem is usually the underlying expensive parallel plan, not parallelism per se. Fix the plan with an index or a rewrite and the wait goes away.");

        t["CPU_SQL_PERCENT"] = new AdviceBlock(
            Headline:
                "SQL Server process CPU is sustained above the threshold — the workload is eating real CPU, not waiting on something else",
            Investigation:
                "Confirm SQL is the consumer (not antivirus, not a runaway agent job, not another process on the same VM) — Task Manager or `sys.dm_os_ring_buffers` for the resource monitor entries will show SQL CPU vs. total. If SOS_SCHEDULER_YIELD is co-elevated, schedulers are saturated. The CPU_SPIKE corroborator signals bursty load (max far above average) — different problem than steady saturation. Pull the top CPU queries with `sp_BlitzCache @sort_order = 'cpu'`.",
            Remediation:
                "Tune the top CPU queries — that is almost always cheaper than buying cores. Look for missing indexes, scans of large tables, and parameter sniffing producing inefficient plans (PARAMETER_SENSITIVITY findings will surface those). If a recent plan change caused the spike (PLAN_REGRESSION will surface it), forcing the previous plan is a fast fix while you investigate the root cause.");

        t["CPU_SPIKE"] = new AdviceBlock(
            Headline:
                "CPU spike detected — peak usage is well above the period average, not sustained saturation",
            Investigation:
                "Spikes are different from steady saturation: the box has CPU headroom most of the time but something briefly burns it all. The drill-down `queries_at_spike` lists what was running near the peak. Common causes: a recently-cached bad plan (PLAN_REGRESSION amplifier), a parameter-sensitive query hit by a worst-case parameter value (PARAMETER_SENSITIVITY amplifier), or a parallel report query (CXPACKET amplifier).",
            Remediation:
                "If a plan regression is co-flagged, force the better plan — the Query Store EXEC is in the remediation snippet below. If parameter sensitivity is co-flagged, do NOT force a plan (it locks in the wrong one for some inputs) — recompile or add `OPTION(RECOMPILE)` to the affected statement instead. For ad-hoc reporting spikes, consider Resource Governor or scheduling the report off-peak.");

        // ─────────────────────────────────────────────────────────────────
        // Memory pressure
        // ─────────────────────────────────────────────────────────────────

        t["PAGEIOLATCH_SH"] = new AdviceBlock(
            Headline:
                "PAGEIOLATCH_SH waits — SQL is reading data pages from disk into the buffer pool faster than it can serve them",
            Investigation:
                "This is the classic buffer-pool-too-small / scan-doing-too-much-IO signal. Pull PERFMON_PLE — if Page Life Expectancy is low, the buffer pool is being churned. Cross-reference with IO_READ_LATENCY_MS to separate buffer pool pressure from underlying disk latency: high PAGEIOLATCH_SH with low read latency means the workload is reading too much, not that disk is slow.",
            Remediation:
                "Look at the top read-IO queries with `sp_BlitzCache @sort_order = 'reads'` and the missing-index drill-down. The single most common fix is a missing nonclustered index — adding one can eliminate gigabytes of scans per execution. If indexes are right and the wait persists, the buffer pool is too small for the data set; increase `max server memory` if the OS has room, or add RAM.");

        t["PAGEIOLATCH_EX"] = new AdviceBlock(
            Headline:
                "PAGEIOLATCH_EX waits — SQL is waiting to write data pages, usually under heavy modification workload",
            Investigation:
                "PAGEIOLATCH_EX shows up under heavy bulk inserts, large updates, or tempdb workspace writes. Check IO_WRITE_LATENCY_MS to separate workload-driven pressure from underlying disk pressure. If TEMPDB_USAGE is also flagged, the writes are tempdb workspace (spills, hash table spills, sort spills) — the QUERY_SPILLS drill-down identifies the offenders.",
            Remediation:
                "For tempdb-driven PAGEIOLATCH_EX, fix the spilling queries (give them better memory grants by updating statistics, or rewrite to avoid the operator that spills). For modification-workload pressure, batch large operations into smaller chunks so the buffer pool can flush between batches. If disk write latency is genuinely high (IO_WRITE_LATENCY_MS critical), the storage is the bottleneck and no amount of tuning will help.");

        t["RESOURCE_SEMAPHORE"] = new AdviceBlock(
            Headline:
                "RESOURCE_SEMAPHORE waits — queries are queueing for memory grants because the workspace pool is exhausted",
            Investigation:
                "Memory grants are reserved up front for sorts, hashes, and parallel operators. When the grant pool fills, large queries queue and small ones starve. Pull MEMORY_GRANT_PENDING for the live waiter count, and look at `sys.dm_exec_query_memory_grants` for who is holding the grants. The PAGEIOLATCH_SH/EX cascade is common — large grants shrink the buffer pool, which causes I/O pressure.",
            Remediation:
                "The top contributors are over-granted queries — find them with `sp_BlitzCache @sort_order = 'memory grant'` and look for queries with `granted_memory_kb` far above their `used_memory_kb` (the grant was much larger than needed, usually due to stale statistics or a bad cardinality estimate). Updating statistics and adding the right indexes shrinks the grants. As a last resort, lower `MAX server memory's` query workspace cap or use Resource Governor to cap individual workload groups.");

        t["RESOURCE_SEMAPHORE_QUERY_COMPILE"] = new AdviceBlock(
            Headline:
                "Query compile gateway waits — too many concurrent compilations of expensive queries",
            Investigation:
                "SQL Server gates expensive plan compilations through a semaphore (the big-plan gateway). When too many big plans need compiling at once, sessions wait. This is almost always a parameterisation problem — ad-hoc queries with literal values forcing recompiles, or RPC calls without proper parameter typing inflating the compile cost. Pull `sys.dm_exec_query_stats` ordered by total `compile_cpu_ms` to find the offenders.",
            Remediation:
                "Force parameterisation on the database (`ALTER DATABASE ... SET PARAMETERIZATION FORCED`) only after checking the workload is safe for it. Better: fix the app to parameterise calls, or add a plan guide for the worst ad-hoc patterns. Enabling `optimize for ad hoc workloads` at the instance level reduces plan cache bloat from one-off queries but does not reduce the compile cost of any individual large plan.");

        t["MEMORY_GRANT_PENDING"] = new AdviceBlock(
            Headline:
                "Queries are queued waiting for memory grants — the workspace pool is full",
            Investigation:
                "MEMORY_GRANT_PENDING is the live waiter count from `sys.dm_exec_query_resource_semaphores`. A single sustained waiter is concerning, five is critical. Look at `sys.dm_exec_query_memory_grants` for who is holding the grants and how much they are over-requesting (`granted_memory_kb` vs `used_memory_kb`). Co-elevated RESOURCE_SEMAPHORE waits confirm the pressure, and QUERY_SPILLS confirms queries are running with insufficient grants and falling back to disk.",
            Remediation:
                "Same playbook as RESOURCE_SEMAPHORE: fix the over-granted queries. Run `sp_BlitzCache @sort_order = 'memory grant'` to find them. Stale statistics produce over-estimates that demand over-large grants — update statistics on the heavy hitters. As a stopgap, `OPTION (MAX_GRANT_PERCENT = X)` on the worst query limits its grant without affecting others.");

        t["PERFMON_PLE"] = new AdviceBlock(
            Headline:
                "Page Life Expectancy is low — the buffer pool is being churned faster than data can stay resident",
            Investigation:
                "The old `PLE > 300` rule of thumb is outdated for large memory systems — what matters is the trend. A PLE that crashes from steady state to single digits indicates active eviction, usually from a query scanning a table larger than the buffer pool. Cross-reference PAGEIOLATCH_SH (data being read from disk to replace what was evicted) and look at the top-reads queries with `sp_BlitzCache @sort_order = 'reads'`.",
            Remediation:
                "Find and fix the scan: usually a missing index, a query rewritten to do a seek, or a partition strategy for the largest tables. Adding memory only helps if the working set genuinely exceeds RAM — most low-PLE problems are caused by one or two scan-heavy queries, not by the data being too big. Confirm `max server memory` is set (not the default 2147483647 MB) so SQL is not fighting the OS for RAM.");

        // ─────────────────────────────────────────────────────────────────
        // Blocking / lock contention
        // ─────────────────────────────────────────────────────────────────

        t["BLOCKING_EVENTS"] = new AdviceBlock(
            Headline:
                "Blocked process reports are firing above the per-hour threshold — sessions are waiting on locks held by other sessions",
            Investigation:
                "The blocked-process-report drill-down identifies the head blockers and victims. A `sleeping_blocker_count > 0` means the apex session is sleeping with an open transaction — the abandoned-transaction pattern, almost always an application bug (BEGIN TRAN without a matching COMMIT/ROLLBACK on the error path). Run `sp_WhoIsActive @get_locks = 1` against the live server for the current chain.",
            Remediation:
                "Kill abandoned sleeping head-blocker sessions to free the chain — the underlying transaction will roll back. For non-abandoned blocking, the cause is usually long-running write transactions or unindexed reads taking shared locks. Add the right indexes to reduce reader-writer contention, and consider enabling READ_COMMITTED_SNAPSHOT on the database (the DB_CONFIG amplifier flags this) to eliminate reader/writer blocking entirely.");

        t["DEADLOCKS"] = new AdviceBlock(
            Headline:
                "Deadlocks are firing above the per-hour threshold — at least one transaction is being killed every few minutes",
            Investigation:
                "Pull the deadlock XML graphs from the system_health Extended Events session — they show the resources, lock modes, and statements involved. Look at the deadlock victims' statements: most deadlocks are caused by inconsistent access order between two procedures (proc A locks table X then Y; proc B locks Y then X). Reader/writer deadlocks (LCK_M_S waits in the graph) are eliminated by READ_COMMITTED_SNAPSHOT and flagged by the DB_CONFIG amplifier when RCSI is off.",
            Remediation:
                "Fix the deadlock pattern: enforce consistent object access order in the application, shorten transactions (move work that does not need to be transactional outside the BEGIN TRAN), or enable READ_COMMITTED_SNAPSHOT to eliminate reader/writer deadlocks. Raising deadlock priority on the loser does not prevent the deadlock — it only changes who wins. SQL Server's deadlock detector runs every 5 seconds, so any sustained rate means active contention, not transient.");

        t["BLOCKING_CHAIN"] = new AdviceBlock(
            Headline:
                "Multi-level blocking chain reconstructed — a session is blocking blockers, fanning out into a pile-up",
            Investigation:
                "The drill-down emits depth (how many levels) and victim count (total transitively blocked sessions). `worst_apex_sleeping = 1` means the head blocker is sleeping with an open transaction — the classic abandoned-transaction signature. THREADPOOL co-elevation means every blocked victim is also holding a worker thread, and you are at risk of total thread exhaustion. Run `sp_WhoIsActive @get_locks = 1, @get_plans = 1` to see the chain live.",
            Remediation:
                "If the apex is sleeping, kill it — that single action collapses the entire chain. For non-sleeping apex chains, the cause is usually one slow operation everyone is queued behind: a missing index causing a long scan under a lock, a poorly-targeted UPDATE statement, or an unindexed foreign key holding an Sch-S lock during a parent-row update. Address that one query; the chain dissolves.");

        t["LCK"] = new AdviceBlock(
            Headline:
                "General lock contention is significant in wait stats — writer/writer or update/exclusive lock conflicts dominating",
            Investigation:
                "LCK groups X (exclusive), U (update), IX (intent exclusive), SIX, and BU lock waits — the writer/writer side of lock contention. Unlike LCK_M_S/LCK_M_IS (readers blocked by writers, fixable with RCSI), these are real serialization points: two writers genuinely cannot proceed at the same time. Pull `sp_BlitzFirst @expert_mode = 1` for the active wait breakdown and `sp_WhoIsActive` for live offenders.",
            Remediation:
                "RCSI does NOT help here — these are writer/writer conflicts. Shorten transactions, fix the queries causing long lock holds (missing indexes turning a seek into a scan extends every lock the scan needs), and consider partitioning hot tables so different parts of the workload do not collide. For frequent UPDATE-with-no-WHERE-clause-index, that one query is usually the entire problem.");

        t["LCK_M_S"] = new AdviceBlock(
            Headline:
                "LCK_M_S waits — readers are being blocked by writers, classic shared/exclusive lock contention",
            Investigation:
                "LCK_M_S waits mean SELECT queries are queueing behind UPDATE/INSERT/DELETE transactions on the same rows or pages. Check the DB_CONFIG drill-down: if RCSI (READ_COMMITTED_SNAPSHOT) is off on the database, enabling it eliminates this entire category of wait. Reader/writer deadlocks are also driven by this pattern (the DEADLOCKS finding will show them in the graph).",
            Remediation:
                "Enable READ_COMMITTED_SNAPSHOT: `ALTER DATABASE <db> SET READ_COMMITTED_SNAPSHOT ON;` — readers stop taking shared locks and instead read the row version. This must be done at a quiet moment because it requires an exclusive lock on the database. Test on a non-production copy first if the application uses lock hints (NOLOCK, READUNCOMMITTED) or relies on the default isolation in surprising ways.");

        t["LCK_M_IS"] = new AdviceBlock(
            Headline:
                "LCK_M_IS waits — intent-shared locks are being blocked, usually shared locks waiting for exclusive operations to finish",
            Investigation:
                "Intent-shared locks are the page/table-level locks SELECT takes to declare 'I will be reading rows on this page'. Waiting on IS means the page or table is held in an incompatible mode by a writer. Same RCSI-eligible pattern as LCK_M_S: readers blocked by writers. Pull the DB_CONFIG drill-down to confirm RCSI is off on the affected databases.",
            Remediation:
                "Same as LCK_M_S: enable READ_COMMITTED_SNAPSHOT on the database. This eliminates reader/writer blocking for all isolation levels at or below READ COMMITTED — the most common case. If the application relies on READ UNCOMMITTED with NOLOCK hints to avoid these waits, RCSI is a strictly better mechanism for the same goal because it returns consistent (committed) data instead of dirty reads.");

        t["SCH_M"] = new AdviceBlock(
            Headline:
                "Schema modification lock waits — DDL or index operations are blocking everything else on the affected object",
            Investigation:
                "SCH-M locks are taken by `ALTER TABLE`, `CREATE/DROP INDEX`, partition operations, and statistics updates. They are incompatible with everything, including SELECT, so a query holding an SCH-M lock on a hot table blocks the entire workload against it. Check `sys.dm_exec_requests` for what is running — usually a scheduled job (RUNNING_JOBS may also be flagged) doing an index rebuild during the wrong window.",
            Remediation:
                "Reschedule the offending DDL operation to a real maintenance window. For ongoing index maintenance on hot tables, use `ONLINE = ON` index rebuilds where possible — they take a much briefer SCH-M lock at the start and end instead of holding it for the whole rebuild. For Enterprise Edition, also consider `WAIT_AT_LOW_PRIORITY` to let the rebuild yield to user queries.");

        t["LCK_RANGE"] = new AdviceBlock(
            Headline:
                "Range-lock waits — SERIALIZABLE isolation is escalating to row-range locks, blocking other readers and writers",
            Investigation:
                "Range locks (LCK_M_RS_*, LCK_M_RIn_*, LCK_M_RX_*) only appear under SERIALIZABLE or REPEATABLE READ isolation — they prevent phantom reads by locking ranges of an index, not just the rows being read. Either the application is explicitly using SERIALIZABLE, or a `BEGIN TRANSACTION` somewhere defaults to it (System.Transactions / TransactionScope in .NET defaults to SERIALIZABLE — a notorious source of this pattern).",
            Remediation:
                "Check the application's transaction handling: if it is .NET, `TransactionScope` defaults to SERIALIZABLE — pass `TransactionOptions { IsolationLevel = IsolationLevel.ReadCommitted }`. If SERIALIZABLE is required (rare), the only mitigation is shorter transactions and tighter range predicates so fewer rows are locked. Enabling READ_COMMITTED_SNAPSHOT does not help SERIALIZABLE — you would need SNAPSHOT isolation as well, and the application has to opt into it explicitly.");

        // The range-lock family all share one advice block — every LCK_M_R* key
        // in FactScorer.GetWaitThresholds maps to the same LCK_RANGE entry.
        var rangeLock = t["LCK_RANGE"];
        t["LCK_M_RS_S"]  = rangeLock;
        t["LCK_M_RS_U"]  = rangeLock;
        t["LCK_M_RIn_NL"] = rangeLock;
        t["LCK_M_RIn_S"] = rangeLock;
        t["LCK_M_RIn_U"] = rangeLock;
        t["LCK_M_RIn_X"] = rangeLock;
        t["LCK_M_RX_S"]  = rangeLock;
        t["LCK_M_RX_U"]  = rangeLock;
        t["LCK_M_RX_X"]  = rangeLock;

        // ─────────────────────────────────────────────────────────────────
        // I/O
        // ─────────────────────────────────────────────────────────────────

        t["IO_READ_LATENCY_MS"] = new AdviceBlock(
            Headline:
                "Average read latency is above the storage-health threshold — disk reads are slow",
            Investigation:
                "Use `sys.dm_io_virtual_file_stats` (via `sp_BlitzFirst @expert_mode = 1`) to identify which files are slow — data files? log files? tempdb? Latency above 20ms is concerning, 50ms is critical for OLTP storage. If PAGEIOLATCH_SH is co-elevated, the workload is read-heavy enough to be exposing the latency; if PAGEIOLATCH waits are low, the storage is slow but the workload is not hitting it hard.",
            Remediation:
                "First, check whether the workload is the cause: a missing index forcing scans drives massive read I/O that exposes any storage as slow. Fix the indexing and the latency often disappears without storage changes. If indexing is right and latency persists, the storage layer is the bottleneck — talk to the storage team about IOPS, queue depth, and whether the data files are on appropriate tier storage. Watch for shared SAN contention; latency that varies by time of day usually means another workload is contending.");

        t["IO_WRITE_LATENCY_MS"] = new AdviceBlock(
            Headline:
                "Average write latency is above the storage-health threshold — disk writes are slow",
            Investigation:
                "Pull `sys.dm_io_virtual_file_stats` and look at write latency per file — the log file is the usual suspect because transactions cannot commit until WRITELOG completes. Latency above 10ms is concerning for OLTP write workloads, 30ms is critical. If WRITELOG is co-elevated, the bottleneck is specifically the transaction log; if PAGEIOLATCH_EX is high, data file writes are slow under modification load.",
            Remediation:
                "The transaction log demands sequential, low-latency storage — separate spindle/LUN from the data files where possible, and never put it on storage shared with non-database workloads. For data file write pressure, look at the modification workload (bulk inserts, large updates) and consider batching them. As with reads, missing indexes drive over-writing (every update touches every nonclustered index on the table); right-size the index set.");

        t["WRITELOG"] = new AdviceBlock(
            Headline:
                "WRITELOG waits — transactions are waiting for the log to flush before they can commit",
            Investigation:
                "WRITELOG is the wait at COMMIT — every transaction has to wait for its log records to be durably written to disk before it returns. Pull `sys.dm_io_virtual_file_stats` for the log file specifically; if its write latency is high, the storage is the bottleneck. If write latency is low but WRITELOG is still high, the workload is committing too frequently — usually thousands of tiny transactions per second from chatty client code.",
            Remediation:
                "Storage fix: put the log file on its own low-latency device (NVMe, log-tier SAN); make sure it is not contending with data file or tempdb writes. Workload fix: batch many small writes into fewer larger transactions where the consistency model allows. Delayed durability (`ALTER DATABASE ... SET DELAYED_DURABILITY = FORCED`) trades durability for latency and is appropriate for workloads where the loss of the last few committed transactions on a crash is acceptable.");

        // ─────────────────────────────────────────────────────────────────
        // Latch contention
        // ─────────────────────────────────────────────────────────────────

        t["LATCH_EX"] = new AdviceBlock(
            Headline:
                "Exclusive page-latch contention — in-memory contention on hot pages, often tempdb allocation or last-page insert hotspots",
            Investigation:
                "Page latches are short-term synchronization on individual buffer pool pages. EX latch waits usually mean parallel insert into a heap or narrow index (every session contending for the same allocation page), tempdb allocation contention on GAM/SGAM/PFS pages (`2:1:1`-style waits), or insert hotspots on tables with monotonically increasing keys. TempDB allocation contention shows up alongside TEMPDB_USAGE elevation.",
            Remediation:
                "For tempdb allocation contention, enable `MIXED_PAGE_ALLOCATION = OFF` (default since 2016) and ensure at least 4 tempdb data files of equal size — eight if cores >= 8. For insert hotspots, partition the table, use a non-sequential clustered key, or move the workload to in-memory OLTP if the table fits. For heap inserts, add a clustered index or accept that parallel inserts to heaps will contend.");

        t["LATCH_SH"] = new AdviceBlock(
            Headline:
                "Shared page-latch contention — concurrent readers contending for the same hot pages",
            Investigation:
                "Shared latch waits are less common than EX, but show up when many parallel readers hit the same small set of pages — root pages of busy indexes, or single-page tables that everyone reads. Pull `sys.dm_os_latch_stats` for the breakdown by latch class. PAGE class is the buffer pool; ACCESS_METHODS_HOBT_VIRTUAL_ROOT is a famously hot root-page latch on heavily-read indexes.",
            Remediation:
                "If a single hot page is being thrashed by many small lookups (root-page latch contention), consider partitioning the index, denormalizing the lookup data into multiple pages, or caching at the application layer. Latch waits are usually a symptom of an architectural hotspot, not a SQL Server configuration problem — fixing them usually means changing the schema or workload, not a setting.");

        // ─────────────────────────────────────────────────────────────────
        // TempDB
        // ─────────────────────────────────────────────────────────────────

        t["TEMPDB_USAGE"] = new AdviceBlock(
            Headline:
                "TempDB usage is above the configured threshold — workspace allocation or version store growth is heavy",
            Investigation:
                "Pull `sys.dm_db_file_space_usage` and `sys.dm_db_session_space_usage` to see who is consuming tempdb. Three common drivers: (1) heavy spills from queries with insufficient memory grants (QUERY_SPILLS may be co-elevated), (2) very heavy use of temp tables or table variables in application code, (3) RCSI/SI version store on a write-heavy database growing the version store. Each has a different fix.",
            Remediation:
                "For spill-driven tempdb use, fix the spilling queries (update statistics, add indexes, rewrite to avoid the operator that spills). For temp-object-driven use, look at the worst-offender procedures and consider whether all those temp tables are necessary. For version-store growth, look for long-running transactions that prevent old row versions from being cleaned up — those keep the store growing forever. Confirm tempdb has enough data files (one per core up to 8) sized identically and not auto-growing.");

        // ─────────────────────────────────────────────────────────────────
        // Query-level
        // ─────────────────────────────────────────────────────────────────

        t["QUERY_SPILLS"] = new AdviceBlock(
            Headline:
                "Query spills — operators are running out of granted memory and spilling intermediate results to tempdb",
            Investigation:
                "Spills happen when a hash join, sort, or hash aggregate gets a memory grant smaller than it actually needed and falls back to writing to tempdb. The root cause is almost always a bad cardinality estimate: SQL guessed the operator would handle 1000 rows, granted memory for that, and the operator actually got 1,000,000 rows. Pull `sp_BlitzCache @sort_order = 'spills'` and look for queries with stale statistics or skewed parameter values.",
            Remediation:
                "Update statistics with `FULLSCAN` on the offending tables — out-of-date statistics are the most common cause of bad grant estimates. Add filtered indexes if a subset of the data is the actual hot spot. As a per-query stopgap, `OPTION (RECOMPILE)` lets SQL re-estimate with the current statistics at each execution; `OPTION (MIN_GRANT_PERCENT = X)` forces a larger grant. If multiple queries spill consistently, the workload may simply need more workspace memory available.");

        t["QUERY_HIGH_DOP"] = new AdviceBlock(
            Headline:
                "Queries are running with high degree of parallelism — DOP above 8 is unusual outside of warehousing workloads",
            Investigation:
                "Queries running at DOP > 8 consume disproportionate scheduler and thread-pool resources for what is usually a small per-query benefit. Pull `sp_BlitzCache @sort_order = 'cpu'` for the parallel offenders and look at the per-execution CPU vs. duration ratio — if duration is roughly CPU/DOP, parallelism is helping; if duration is closer to CPU, parallelism is mostly contending with itself (CXPACKET amplifier corroborates).",
            Remediation:
                "Set `MAXDOP` at the instance level to a sane value (half a NUMA node's cores, capped at 8 is a common starting point), and raise `cost threshold for parallelism` from the default 5 to 50 — most OLTP queries should not parallelise at all. For specific queries that benefit from higher DOP, use `OPTION (MAXDOP X)` per-query rather than raising the instance default. Watch the CXPACKET wait afterward; if it drops without a CPU regression, the change is the right call.");

        t["PARAMETER_SENSITIVITY"] = new AdviceBlock(
            Headline:
                "Parameter-sensitive plan — one cached plan is wildly different in cost across different parameter values",
            Investigation:
                "The drill-down emits worker-time ratio (max/min per-execution CPU for the same plan), grant divergence (different grants for the same plan), and spill divergence. A worker-time ratio above 10x means the plan is catastrophic for some parameter values — usually a plan compiled for a small selective value being reused with a large unselective one, or vice versa. Pull the offending query from `sys.dm_exec_query_stats` and run it with the extreme parameter values to see the plan shapes.",
            Remediation:
                "**Do not force a plan.** Forcing locks in a plan that is bad for some parameter values — the entire point of detecting parameter sensitivity is that no single plan works for all inputs. The right remediations are: `OPTION (RECOMPILE)` to get a fresh plan per execution (best when parameter values vary widely and the query is not run thousands of times per minute), `OPTION (OPTIMIZE FOR ...)` to compile for a specific value, plan guides for the parameter branches, or splitting the query into two branches handled by separate procedures. On SQL Server 2022, Parameter Sensitive Plan optimization (PSP) can handle this automatically for some plan shapes.");

        t["PLAN_REGRESSION"] = new AdviceBlock(
            Headline:
                "Plan regression — a query is running a worse plan than one it has performed well with in the past",
            Investigation:
                "The drill-down shows the regression factor (latest plan cost vs. best historical plan cost, per execution), the plan hashes, and the integer plan_ids needed for `sp_query_store_force_plan`. If `latest_is_forced > 0` and `force_failure_count > 0` (PlanRegression amplifier), SQL is already trying to force a plan that is failing to apply — examine `sys.query_store_plan` for the force_failure_reason; another forcing attempt will not help. CPU_SPIKE and CPU_SQL_PERCENT amplifiers indicate the regressed plan is driving the CPU.",
            Remediation:
                "Forcing the historical best plan is the fast fix while you investigate. **Confirm the better plan is actually better against current data before forcing** — run both plans against representative parameter values and compare execution stats. The generated EXEC below has the IDs filled in. Forcing is reversible via `sp_query_store_unforce_plan` (the back-out statement is commented in the snippet). For a permanent fix, address what caused the bad plan to be chosen: stale statistics, parameter sniffing, or recent index changes are the usual culprits.");

        // ─────────────────────────────────────────────────────────────────
        // DB config
        // ─────────────────────────────────────────────────────────────────

        t["DB_CONFIG"] = new AdviceBlock(
            Headline:
                "Database-level configuration issues detected — one or more databases have settings that cause measurable harm",
            Investigation:
                "The drill-down breaks down the count per category: `auto_shrink_on_count` (databases with AUTO_SHRINK on — never appropriate, causes catastrophic fragmentation), `auto_close_on_count` (AUTO_CLOSE on — adds connection-time overhead on every first query), `page_verify_not_checksum_count` (page verify not at CHECKSUM — torn-page detection is much weaker), and `rcsi_off_count` (READ_COMMITTED_SNAPSHOT off — amplified when LCK_M_S/LCK_M_IS waits are present, meaning RCSI would help).",
            Remediation:
                "`ALTER DATABASE <db> SET AUTO_SHRINK OFF` — always. `ALTER DATABASE <db> SET AUTO_CLOSE OFF` — always for any database that gets real workload. `ALTER DATABASE <db> SET PAGE_VERIFY CHECKSUM` — for torn-page detection on modern storage. RCSI is the more nuanced call: enabling it eliminates reader/writer blocking but adds version store overhead to writes — test on a copy first if the application uses lock hints or relies on default isolation semantics in surprising ways.");

        // ─────────────────────────────────────────────────────────────────
        // Jobs / disk / bad actors
        // ─────────────────────────────────────────────────────────────────

        t["RUNNING_JOBS"] = new AdviceBlock(
            Headline:
                "Long-running SQL Agent jobs detected — one or more jobs have been running past their normal duration",
            Investigation:
                "Pull `msdb.dbo.sysjobs` and `sysjobhistory` to see which jobs are running long and what they normally take. Common patterns: a maintenance job (CHECKDB, index rebuild) that overlapped into business hours, a job stuck on a blocked spid (BLOCKING_EVENTS may be co-flagged), or a job whose data volume grew beyond what its design could handle. SCH_M waits sometimes accompany long index-maintenance jobs that are now blocking the workload.",
            Remediation:
                "Decide whether to kill the running job or let it finish — killing a job mid-CHECKDB or mid-rebuild typically leaves nothing harmful behind but the work needs to redo. For chronically-long jobs, look at whether the workload they handle has outgrown the implementation: a CHECKDB on a 10TB database is not a job, it's a project. Consider partitioning, incremental statistics, or `WITH PHYSICAL_ONLY` for CHECKDB on enormous databases as a partial substitute.");

        t["DISK_SPACE"] = new AdviceBlock(
            Headline:
                "Disk free space is below the healthy-headroom threshold — risk of database growth being denied",
            Investigation:
                "Below 10% free is concerning, below 5% is critical. Check which database files are growing and at what rate — `sys.dm_io_virtual_file_stats` plus `sys.master_files` will show file sizes and recent growth. Common patterns: tempdb growth from a runaway spilling query (QUERY_SPILLS may be co-flagged), a log file that has not been backed up (FULL recovery mode without log backups), or a data file that auto-grew aggressively.",
            Remediation:
                "Free space first, fix root cause second: take log backups for FULL recovery databases (logs cannot truncate without backups), shrink tempdb if it grew from a one-time spill (this is one of the few legitimate uses of DBCC SHRINKFILE), or expand the volume. For data file growth, set sane auto-growth values (fixed MB, not percentage; ideally pre-size based on workload growth) and ensure Instant File Initialization is enabled so growths don't block writes.");

        t["BAD_ACTOR"] = new AdviceBlock(
            Headline:
                "One query is dominating workload cost — its frequency × per-execution impact rank it as the top offender",
            Investigation:
                "The fact key carries the query hash; the drill-down has the per-execution CPU and reads, plus the execution count. A query running 100,000 times at 5ms CPU is a very different problem from one running once at 30 seconds, even though both can score high — the first is a parameterisation or caching opportunity, the second is a tuning project. Pull the plan with `sys.dm_exec_query_plan` for the hash and look at the worst operators.",
            Remediation:
                "For high-frequency lightweight queries, look at index opportunities (does the WHERE clause cover the right columns?), at the call pattern (can the application cache results?), and at the parameterisation (one cached plan vs. thousands of compile-once executions). For low-frequency heavyweight queries, this is a single-query tuning exercise: missing indexes, rewriting subqueries to joins or vice versa, or adding the right statistics. `sp_BlitzCache` against the specific hash will dig into the plan.");

        // ─────────────────────────────────────────────────────────────────
        // Anomaly facts (first-class)
        // ─────────────────────────────────────────────────────────────────

        t["ANOMALY_CPU_SPIKE"] = new AdviceBlock(
            Headline:
                "CPU is anomalously elevated compared to its baseline for this time bucket",
            Investigation:
                "The anomaly detector compares current CPU to the same hour-of-week historical baseline — sigma above 2.0 is flagged. A 4-sigma deviation (the critical level) means CPU is far above what is normal for this time-of-day. Pull `queries_at_spike` to see what was running near the peak. Common anomaly drivers: a recently-cached bad plan (check PLAN_REGRESSION), an unusual workload pattern (a marketing campaign, a backfill job), or another process on the box.",
            Remediation:
                "Confirm whether the elevation is a one-time event (ad-hoc reporting, a deploy) or a new sustained state. If transient, no action needed beyond noting it. If sustained, the threshold-based CPU_SQL_PERCENT finding will fire on the next analysis window and the standard CPU-pressure playbook applies. The anomaly framing means 'this is unusual', not 'this is automatically bad' — context determines severity.");

        t["ANOMALY_BLOCKING_SPIKE"] = new AdviceBlock(
            Headline:
                "Blocking events are anomalously elevated compared to baseline — a ratio above 3x normal rate",
            Investigation:
                "The detector uses ratio-based scoring (3x baseline = 0.5 score, 10x = 1.0), so this finding means blocking rate is well above the workload's normal pattern. Pull the BLOCKING_EVENTS drill-down for the head blockers, and check whether a specific blocker is responsible for most of the elevation (`sleeping_blocker_count` flags abandoned transactions). A sudden ratio spike often points to a deploy or schema change that introduced a new contention pattern.",
            Remediation:
                "Identify what changed: a recent deploy, a new query pattern, a schema migration, or a job running outside its normal window. If a specific abandoned head blocker is at the apex, kill it. For systemic increases, the BLOCKING_EVENTS standard playbook applies — RCSI for reader/writer waits, shorter transactions for writer/writer, index tuning to reduce lock-hold duration.");

        t["ANOMALY_DEADLOCK_SPIKE"] = new AdviceBlock(
            Headline:
                "Deadlock rate is anomalously elevated — a sudden jump in deadlocks compared to normal",
            Investigation:
                "Deadlocks usually run at a low steady-state rate determined by the workload's transaction patterns; a sudden ratio spike means a new deadlock-prone interaction was just introduced. Pull the deadlock graphs from system_health Extended Events for the period — the new pattern will dominate. Common causes: a recent stored procedure change that altered object access order, or a new high-frequency call path hitting an existing-but-rare deadlock window.",
            Remediation:
                "Find the new deadlock pattern in the graphs and fix the offending interaction — usually by enforcing consistent object access order between the two procedures involved, or by shortening the transaction so the window for deadlock is smaller. If a deploy correlates with the spike, the new code is the suspect — review the latest changes to the procedures named in the deadlock graphs.");

        t["ANOMALY_READ_LATENCY"] = new AdviceBlock(
            Headline:
                "Read latency is anomalously elevated compared to its baseline — storage is slower than normal right now",
            Investigation:
                "The baseline is hour-of-week historical, so this finding means reads are slower than they usually are at this time. Pull `sys.dm_io_virtual_file_stats` to see which files are affected. Storage-tier anomalies are often shared-infrastructure contention (another VM, another workload on the SAN) and resolve on their own. Workload-tier anomalies (a new scan-heavy query, a missing index regression) persist until the workload is fixed.",
            Remediation:
                "If `queries_at_spike` shows a new query doing massive reads, fix the index or the query and the storage will catch up. If the queries look normal but latency is up, the storage layer is the issue — check with the storage team about other tenants or scheduled jobs (backups, replication) on the same infrastructure. Temporary storage-side anomalies need no action beyond monitoring; sustained ones become an IO_READ_LATENCY_MS finding on the next window.");

        t["ANOMALY_WRITE_LATENCY"] = new AdviceBlock(
            Headline:
                "Write latency is anomalously elevated compared to its baseline — disk writes are slower than normal right now",
            Investigation:
                "Same diagnostic shape as the read-latency anomaly but for writes. Log file write latency tends to be the most consequential — every commit waits for it. Pull `sys.dm_io_virtual_file_stats` for the log file specifically. If WRITELOG waits are co-flagged, the workload is committing through the slow log right now. Recent storage-side events (a SAN snapshot, a failover, a backup) often correlate with anomalous write latency.",
            Remediation:
                "If a specific event correlates (a backup that overlapped, a SAN maintenance window), no action — let it pass. If the elevation is sustained without an external explanation, the storage layer needs investigation. Workload-side: a sudden burst of large writes (bulk insert, big update) can drive write latency anomalies — the WRITELOG playbook applies if it persists.");

        t["ANOMALY_BATCH_REQUESTS"] = new AdviceBlock(
            Headline:
                "Batch requests per second are anomalously elevated — the server is fielding far more queries than usual",
            Investigation:
                "Batch rate is the rawest measure of workload — a 4-sigma spike means traffic surged. Pull `queries_at_spike` to see what is dominating, and `sp_BlitzCache @sort_order = 'executions'` for what is running most often. Common drivers: an application loop that broke (chatty retries, or a poll that should have been event-driven), a new caller hitting the database directly, or a legitimate burst from a marketing event.",
            Remediation:
                "Confirm the burst is intentional. If the application is misbehaving (uncached lookups, retry storms, polling), fix it at the source — no amount of SQL tuning will fix a server hit with 100,000 unnecessary requests per second. If the burst is real and expected, scale the workload (caching at the app layer, connection pooling, splitting reads to a replica). If load is steady-state going forward, this will appear as a new BAD_ACTOR or CPU finding on the next window.");

        t["ANOMALY_SESSION_SPIKE"] = new AdviceBlock(
            Headline:
                "Active session count is anomalously elevated — far more connections than normal for this time bucket",
            Investigation:
                "A session-count spike usually means clients are holding connections open longer than expected — typical causes are a long-blocked workload (BLOCKING_EVENTS, BLOCKING_CHAIN co-elevation), a connection-pool exhaustion incident in the app, or a runaway client opening connections without closing them. Pull `sys.dm_exec_sessions` and group by `program_name` and `host_name` to see who is holding the connections.",
            Remediation:
                "If the count climbed because workers are blocked, the blocking is the real problem — sessions free themselves as blocking resolves. If a specific application is accumulating connections without releasing, the connection-pool configuration there is wrong (pool size, idle timeout, leaks). Watch for sustained session counts approaching `max user connections` or the worker-thread limit, where the spike becomes a real availability problem.");

        t["ANOMALY_QUERY_DURATION"] = new AdviceBlock(
            Headline:
                "Query duration is anomalously elevated — the median or P95 query is slower than baseline for this time bucket",
            Investigation:
                "Duration anomalies are workload-shape signals: the same queries running slower, OR a different query mix running than usual. Pull `queries_at_spike` for what was active, and compare to `sp_BlitzCache @sort_order = 'avg duration'` for which queries dominate elapsed time. If a specific query's duration jumped, look at PLAN_REGRESSION and PARAMETER_SENSITIVITY for plan-stability findings — those will surface the root cause cleanly.",
            Remediation:
                "Track down the slow query and treat it like any other slow-query issue (plan analysis, statistics, indexes). If multiple queries got slower at once, the box itself is slower — co-elevated CPU, I/O, or memory pressure should pinpoint which resource. Pure duration anomalies without other resource pressure usually mean blocking — check BLOCKING_EVENTS even if it didn't score above its own threshold.");

        t["ANOMALY_MEMORY_PRESSURE"] = new AdviceBlock(
            Headline:
                "Memory pressure metrics are anomalously elevated — PLE, memory-grant queue, or working-set size out of baseline range",
            Investigation:
                "Pull PERFMON_PLE and MEMORY_GRANT_PENDING — those are the two finest indicators of buffer-pool and grant-pool pressure. A sudden PLE drop means a new query is scanning a large object; a grant-queue anomaly means a new large query (or many small over-granted ones) is consuming the workspace pool. The QUERY_SPILLS amplifier corroborates grant-pool exhaustion.",
            Remediation:
                "If the anomaly is buffer-pool-driven (PLE crash), find the scan and add the missing index — the PERFMON_PLE playbook applies. If it is grant-driven, the offender is in `sys.dm_exec_query_memory_grants` right now; update statistics or add an index to fix its grant estimate. Memory anomalies that resolve on their own are typically one-time reporting queries; sustained ones become standard PERFMON_PLE or RESOURCE_SEMAPHORE findings.");

        return t;
    }
}
