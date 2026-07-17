/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// Query Store runtime stats from every database with Query Store actually enabled. Extracted
/// verbatim from Lite's RemoteCollectorService.QueryStore.cs: the enumeration cursor probes each
/// database's sys.database_query_store_options.actual_state (NOT sys.databases.is_query_store_on,
/// which can be out of sync on Azure SQL DB; on-prem additionally filters to non-AG or
/// primary-replica databases). actual_state is matched against the explicit usable set
/// IN (1, 2, 4) — READ_ONLY, READ_WRITE, READ_CAPTURE_SECONDARY — rather than the looser
/// "> 0", which also admits 3 = ERROR: an errored Query Store is not readable and must not
/// pass the "is QS usable" gate (0 = OFF). The per-item [db].sys.sp_executesql query is incremental on the
/// last_execution_time watermark (fallback: 60 minutes back), and the 2017+/2022+ column gates
/// are decided by a live PRODUCTVERSION probe each cycle (default 13 when the probe fails) —
/// deliberately probed rather than trusting cached connection status, which can be
/// version-unknown.
/// </summary>
public sealed class QueryStoreCollector : CollectorDefinitionBase<QueryStoreCollector.Row>
{
    public static QueryStoreCollector Instance { get; } = new();

    private QueryStoreCollector()
    {
    }

    public sealed class Row
    {
        public string DatabaseName { get; set; } = "";
        public long QueryId { get; set; }
        public long PlanId { get; set; }
        public string? ExecutionTypeDesc { get; set; }
        public DateTime? FirstExecutionTime { get; set; }
        public DateTime? LastExecutionTime { get; set; }
        public string? ModuleName { get; set; }
        public string? QueryText { get; set; }
        public string? QueryHash { get; set; }
        public long ExecutionCount { get; set; }
        public long AvgDurationUs { get; set; }
        public long MinDurationUs { get; set; }
        public long MaxDurationUs { get; set; }
        public long AvgCpuTimeUs { get; set; }
        public long MinCpuTimeUs { get; set; }
        public long MaxCpuTimeUs { get; set; }
        public long AvgLogicalIoReads { get; set; }
        public long MinLogicalIoReads { get; set; }
        public long MaxLogicalIoReads { get; set; }
        public long AvgLogicalIoWrites { get; set; }
        public long MinLogicalIoWrites { get; set; }
        public long MaxLogicalIoWrites { get; set; }
        public long AvgPhysicalIoReads { get; set; }
        public long MinPhysicalIoReads { get; set; }
        public long MaxPhysicalIoReads { get; set; }
        public long AvgClrTimeUs { get; set; }
        public long MinClrTimeUs { get; set; }
        public long MaxClrTimeUs { get; set; }
        public long MinDop { get; set; }
        public long MaxDop { get; set; }
        public long AvgQueryMaxUsedMemory { get; set; }
        public long MinQueryMaxUsedMemory { get; set; }
        public long MaxQueryMaxUsedMemory { get; set; }
        public long AvgRowcount { get; set; }
        public long MinRowcount { get; set; }
        public long MaxRowcount { get; set; }
        public long AvgNumPhysicalIoReads { get; set; }
        public long MinNumPhysicalIoReads { get; set; }
        public long MaxNumPhysicalIoReads { get; set; }
        public long AvgLogBytesUsed { get; set; }
        public long MinLogBytesUsed { get; set; }
        public long MaxLogBytesUsed { get; set; }
        public long AvgTempdbSpaceUsed { get; set; }
        public long MinTempdbSpaceUsed { get; set; }
        public long MaxTempdbSpaceUsed { get; set; }
        public string? PlanType { get; set; }
        public string? PlanForcingType { get; set; }
        public bool IsForcedPlan { get; set; }
        public long ForceFailureCount { get; set; }
        public string? LastForceFailureReason { get; set; }
        public int CompatibilityLevel { get; set; }
        public string? QueryPlanText { get; set; }
        public string? QueryPlanHash { get; set; }

        /// <summary>
        /// The replica role sys.query_store_replicas attributed this runtime-stats row to ('Primary',
        /// 'Secondary', 'Geo Secondary', 'Geo HA Secondary'). NULL = the server did not attribute it
        /// (pre-2022, or a 2022 standalone whose sys.query_store_replicas is empty) — deliberately not
        /// coalesced. See hasReplicaAttribution in BuildPerItemQuery.
        /// </summary>
        public string? ReplicaRole { get; set; }
    }

    private const string OnPremDatabaseListQueryText = @"
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE
    @result TABLE (name sysname);

DECLARE
    @db sysname,
    @sql NVARCHAR(500),
    @exec_sp nvarchar(256);

DECLARE db_check CURSOR LOCAL FAST_FORWARD FOR
    SELECT /* PerformanceMonitorLite */
        d.name
    FROM sys.databases AS d
    LEFT JOIN sys.dm_hadr_database_replica_states AS drs
        ON d.database_id = drs.database_id
        AND drs.is_local = 1
    WHERE d.database_id > 4
    AND   d.database_id < 32761
    AND   d.state_desc = N'ONLINE'
    AND   d.name <> N'PerformanceMonitor'
    AND
    (
        drs.database_id IS NULL          /*not in any AG*/
        OR drs.is_primary_replica = 1    /*primary replica*/
    )
    /*EXCLUSION_FILTER*/
    OPTION(RECOMPILE);

OPEN db_check;

FETCH NEXT
FROM db_check
INTO @db;

WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        SET @sql = N'
            SELECT ' + QUOTENAME(@db, '''') + N'
            WHERE EXISTS
            (
                SELECT
                    1
                FROM sys.database_query_store_options
                WHERE actual_state IN (1, 2, 4)
            );';

        SET @exec_sp = QUOTENAME(@db) + N'.sys.sp_executesql';

        INSERT @result (name)
        EXECUTE @exec_sp @sql;
    END TRY
    BEGIN CATCH
    END CATCH;

    FETCH NEXT
    FROM db_check
    INTO @db;
END;

CLOSE db_check;
DEALLOCATE db_check;

SELECT
    name
FROM @result
ORDER BY
    name;";

    private const string AzureDatabaseListQueryText = @"
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

DECLARE
    @result TABLE (name sysname);

DECLARE
    @db sysname,
    @sql NVARCHAR(500),
    @exec_sp nvarchar(256);

DECLARE db_check CURSOR LOCAL FAST_FORWARD FOR
    SELECT /* PerformanceMonitorLite */
        d.name
    FROM sys.databases AS d
    WHERE d.database_id > 4
    AND   d.database_id < 32761
    AND   d.state_desc = N'ONLINE'
    AND   d.name <> N'PerformanceMonitor'
    /*EXCLUSION_FILTER*/
    OPTION(RECOMPILE);

OPEN db_check;

FETCH NEXT
FROM db_check
INTO @db;

WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        SET @sql = N'
            SELECT ' + QUOTENAME(@db, '''') + N'
            WHERE EXISTS
            (
                SELECT
                    1
                FROM sys.database_query_store_options
                WHERE actual_state IN (1, 2, 4)
            );';

        SET @exec_sp = QUOTENAME(@db) + N'.sys.sp_executesql';

        INSERT @result (name)
        EXECUTE @exec_sp @sql;
    END TRY
    BEGIN CATCH
    END CATCH;

    FETCH NEXT
    FROM db_check
    INTO @db;
END;

CLOSE db_check;
DEALLOCATE db_check;

SELECT
    name
FROM @result
ORDER BY
    name;";

    /// <summary>The live version probe deciding the 2017+/2022+ column gates (see class remarks).</summary>
    public const string ProductVersionProbeText =
        "SELECT CONVERT(integer, PARSENAME(CONVERT(sysname, SERVERPROPERTY('PRODUCTVERSION')), 4))";

    /// <summary>PRODUCTVERSION assumed when the probe fails or returns NULL (SQL Server 2016).</summary>
    public const int DefaultProductVersion = 13;

    /// <summary>
    /// Server-side per-database row backstop (#1556): a coarse ceiling — ~10× a healthy per-cycle
    /// volume — spliced as <c>TOP</c> so the SQL engine bounds its own work and the wire transfer before
    /// the client byte budget ever engages. Deliberately NOT the siblings' curation caps (query_stats
    /// TOP 200, procedure_stats TOP 150): those curate the "top N"; this only trims a pathological cycle.
    /// It bounds COUNT, not BYTES — <see cref="MaxTextBytesPerDatabase"/> is the primary memory bound —
    /// and is the same const the host warns on (<see cref="PerItemRowCountWarnThreshold"/>).
    /// </summary>
    public const int MaxRowsPerDatabase = 50_000;

    /// <summary>
    /// The PRIMARY memory bound (#1556): the cumulative per-database TEXT byte budget the client stops
    /// reading at, enforced in <see cref="ReadItemAsync"/>. 256 MB per database composes with the #1553
    /// 4-wide sweep to a bounded peak of ≈ 4 × 256 MB ≈ 1 GB transient — the field incident (0→13GB) was
    /// exactly this un-bounded: 50k rows × ~40KB plan XML is ~2GB for ONE database, ×4 = 8GB, with the
    /// row cap firing no defense (it caps rows, not bytes). Kept alongside the SQL <c>TOP</c> backstop.
    /// </summary>
    public const int MaxTextBytesPerDatabase = 256 * 1024 * 1024;

    public override string Name => "query_store";

    public override string TargetTable => "query_store_stats";

    /// <summary>
    /// Query Store first shipped in SQL Server 2016 (v13), so on-prem/RDS require v13+; a pre-2016 box has no
    /// Query Store at all. Azure SQL DB / Managed Instance report a low ProductMajorVersion yet ship Query
    /// Store, so they are never version-gated, and an unknown version (0) is assumed newest — the exact
    /// condition Lite used in IsCollectorSupported. Gated here in the shared AppliesTo so Lite and Darling
    /// skip identically on a pre-2016 target. (The per-cycle PRODUCTVERSION probe still refines which
    /// version-gated columns are selected; this gate decides whether the collector runs at all.)
    /// </summary>
    public override bool AppliesTo(CollectorTargetInfo target) =>
        target.SqlMajorVersion == 0 || target.SqlMajorVersion >= 13 || target.IsAzureSqlDb || target.IsAzureManagedInstance;

    /// <summary>Incremental: only intervals with newer last_execution_time are fetched per cycle.</summary>
    public override string? WatermarkColumn => "last_execution_time";

    /// <summary>
    /// Per-database watermark (#1556): query_store enumerates databases and now FLUSHES each database's
    /// rows before reading the next, so its cutoff must be per-database — otherwise any mid-run abort
    /// (shutdown, OOM, a store-write failure) would strand the un-flushed databases' intervals behind the
    /// committed databases' advanced server-wide watermark. Keyed on the already-collected
    /// <c>database_name</c> column, the same precedent the deadlocks / blocked_process_report XE
    /// collectors set. With this, each database's commit advances only its own watermark and an abort
    /// loses nothing. This also relocates query_store's cutoff computation into the per-item loop, which
    /// is exactly where the 24h catch-up clamp (<see cref="WatermarkPolicy"/>) must be applied.
    /// </summary>
    public override string? PerDatabaseWatermarkColumn => "database_name";

    /// <summary>Host warns when a per-database read hits the row backstop (see <see cref="MaxRowsPerDatabase"/>).</summary>
    public override int? PerItemRowCountWarnThreshold => MaxRowsPerDatabase;

    /// <summary>The client-side per-database text byte budget enforced in <see cref="ReadItemAsync"/> (see <see cref="MaxTextBytesPerDatabase"/>).</summary>
    public override int? PerItemTextByteBudget => MaxTextBytesPerDatabase;

    /// <summary>Enumerating collector — the primary query is never used.</summary>
    public override CollectorQuery BuildQuery(CollectorContext context)
        => throw new NotSupportedException("query_store enumerates databases; BuildEnumerationQuery drives the cycle.");

    public override CollectorQuery? BuildEnumerationQuery(CollectorContext context)
    {
        var (exclusionClause, exclusionParameters) = DatabaseExclusionFilter.Build(context.ExcludedDatabases, "d.name");
        var text = (context.Target.IsAzureSqlDb ? AzureDatabaseListQueryText : OnPremDatabaseListQueryText)
            .Replace("/*EXCLUSION_FILTER*/", exclusionClause, StringComparison.Ordinal);

        return new CollectorQuery(text, exclusionParameters);
    }

    public override CollectorQuery? BuildEnumerationProbe(CollectorContext context)
        => new(ProductVersionProbeText);

    public override CollectorQuery BuildPerItemQuery(string item, CollectorContext context)
    {
        /* Detect server version for version-gated columns.
           isNew = true for SQL Server 2017+ (product version > 13) or Azure SQL DB/MI.
           Controls: avg_num_physical_io_reads, avg_log_bytes_used, avg_tempdb_space_used, plan_forcing_type_desc.
           hasPlanType = true for SQL Server 2022+ (product version >= 16).
           Controls: plan_type_desc. */
        var productVersion = context.EnumerationProbeResult is null
            ? DefaultProductVersion
            : Convert.ToInt32(context.EnumerationProbeResult, CultureInfo.InvariantCulture);
        bool isNew = productVersion > 13 || context.Target.IsAzureSqlDb || context.Target.IsAzureManagedInstance;
        bool hasPlanType = productVersion >= 16;

        /* Replica attribution — SQL Server 2022+ (product version >= 16). Controls: replica_role.

           "Query Store for secondary replicas" (2022+) gives an AG ONE shared Query Store that lives
           on the PRIMARY: secondary-replica workload is streamed to the primary and persisted in the
           primary's QS tables, distinguishable only by replica_group_id. We collect from primaries
           only (is_primary_replica = 1, in the enumeration cursor) — which is correct and stays — but
           without this attribution the primary's "Top Queries by CPU" silently BLENDS secondary
           workload into the primary's own numbers, with no way for a reader to tell. (Microsoft's own
           Query Performance Insight has this exact bug.)

           Gated on >= 16, NOT the docs' claimed 2025+: sys.query_store_replicas and
           sys.query_store_runtime_stats.replica_group_id both verified present on SQL 2022
           (16.0.4255.1) and SQL 2025 (17.0.4045.5).

           LEFT JOIN, deliberately: on a 2022 standalone (non-AG) server sys.query_store_replicas has
           ZERO rows, yet real runtime-stats rows still carry replica_group_id = 1. An INNER JOIN — or
           a WHERE replica_name = 'Primary' filter — would match nothing and silently delete ALL Query
           Store collection on every 2022 standalone server. Do not "tighten" this.

           replica_name is read DIRECTLY rather than mapped from role_type: contrary to the docs, it IS
           populated on box SQL Server (observed: Primary, Secondary, Geo Secondary, Geo HA Secondary),
           and the docs' sample CASEs replica_group_id as though it were role_type — it is not, it is a
           replica SET number that accumulates per role across failovers.

           Resulting replica_role: NULL on a 2022 standalone, 'Primary' on a 2025 standalone, the actual
           role on an AG with the feature enabled. NULL honestly means "the server did not attribute
           this row" and is deliberately NOT coalesced to an invented value. */
        bool hasReplicaAttribution = productVersion >= 16;

        /* Build version-conditional column fragments for the Query Store query.
           These are injected into the sp_executesql parameter string — no single quotes needed. */
        string numPhysIoReadsCols = isNew
            ? "qsrs.avg_num_physical_io_reads, qsrs.min_num_physical_io_reads, qsrs.max_num_physical_io_reads,"
            : "avg_num_physical_io_reads = NULL, min_num_physical_io_reads = NULL, max_num_physical_io_reads = NULL,";

        string logBytesCols = isNew
            ? "avg_log_bytes_used = qsrs.avg_log_bytes_used, min_log_bytes_used = qsrs.min_log_bytes_used, max_log_bytes_used = qsrs.max_log_bytes_used,"
            : "avg_log_bytes_used = NULL, min_log_bytes_used = NULL, max_log_bytes_used = NULL,";

        string tempdbCols = isNew
            ? "avg_tempdb_space_used = qsrs.avg_tempdb_space_used, min_tempdb_space_used = qsrs.min_tempdb_space_used, max_tempdb_space_used = qsrs.max_tempdb_space_used,"
            : "avg_tempdb_space_used = NULL, min_tempdb_space_used = NULL, max_tempdb_space_used = NULL,";

        string planForcingCol = isNew
            ? "plan_forcing_type = qsp.plan_forcing_type_desc,"
            : "plan_forcing_type = NULL,";

        string planTypeCol = hasPlanType
            ? "plan_type = qsp.plan_type_desc,"
            : "plan_type = NULL,";

        /* Execution-plan capture — mirrors the full Dashboard's @collect_plan path in
           install/09_collect_query_store.sql: CONVERT(nvarchar(max), qsp.query_plan) from
           sys.query_store_plan, no size guard. On only when the host sets CapturePlanXml (Darling);
           off = the nvarchar(1) NULL placeholder (Lite), byte-identical to the no-plan form. No
           single quotes, so it splices straight into the sp_executesql body.

           #1556 plan-text dedupe (ON branch only): a plan is landed ONCE per plan_id per cycle — on its
           newest runtime-stats interval (rn = 1) — and NULL on the older intervals, instead of repeating
           the full plan XML on every interval row of the same plan. Composes with the TOP backstop below
           because both sort by last_execution_time DESC: a plan that survives TOP always keeps its rn = 1
           row (its newest interval is among the newest overall). The consumers tolerate the per-row NULL —
           Lite selects NULL for the grid and fetches plans live, and Darling's stored-plan readers all
           guard `query_plan_text IS NOT NULL`. Not mirrored into the Dashboard proc: its "Download Plan"
           reads by exact collection_id, where per-row NULLs would break a real reader. */
        string planTextCol = context.CapturePlanXml
            ? "query_plan_text = CASE WHEN ROW_NUMBER() OVER (PARTITION BY qsp.plan_id ORDER BY qsrs.last_execution_time DESC) = 1 THEN CONVERT(nvarchar(max), qsp.query_plan) ELSE CONVERT(nvarchar(max), NULL) END,"
            : "query_plan_text = CONVERT(nvarchar(1), NULL),";

        /* The replica-attribution column + its join (see hasReplicaAttribution above). Selected LAST,
           so pre-2022 targets read the nvarchar(1) NULL placeholder at the same ordinal — byte-identical
           shape to the attributed form. No trailing comma: it is the final SELECT item. */
        string replicaRoleCol = hasReplicaAttribution
            ? "replica_role = qsr.replica_name"
            : "replica_role = CONVERT(nvarchar(1), NULL)";

        string replicaJoin = hasReplicaAttribution
            ? "LEFT JOIN sys.query_store_replicas AS qsr\n       ON qsr.replica_group_id = qsrs.replica_group_id"
            : "";

        /* Incremental: only fetch runtime_stats intervals newer than what we already have. */
        var cutoffTime = context.Watermark ?? context.CollectionTime.AddMinutes(-60);

        var escapedDbName = item.Replace("]", "]]", StringComparison.Ordinal);
        var text = $@"
EXECUTE [{escapedDbName}].sys.sp_executesql
    N'SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

     SELECT /* PerformanceMonitorLite */ TOP ({MaxRowsPerDatabase})
         query_id = qsq.query_id,
         plan_id = qsp.plan_id,
         execution_type_desc = qsrs.execution_type_desc,
         first_execution_time = qsrs.first_execution_time,
         last_execution_time = qsrs.last_execution_time,
         module_name =
             CASE
                 WHEN qsq.object_id = 0
                 THEN N''Adhoc''
                 ELSE COALESCE(
                     OBJECT_SCHEMA_NAME(qsq.object_id) + N''.'' + OBJECT_NAME(qsq.object_id),
                     N''Unknown'')
             END,
         query_sql_text = qst.query_sql_text,
         query_hash = CONVERT(varchar(64), qsq.query_hash, 1),
         count_executions = qsrs.count_executions,
         avg_duration = qsrs.avg_duration,
         min_duration = qsrs.min_duration,
         max_duration = qsrs.max_duration,
         avg_cpu_time = qsrs.avg_cpu_time,
         min_cpu_time = qsrs.min_cpu_time,
         max_cpu_time = qsrs.max_cpu_time,
         avg_logical_io_reads = qsrs.avg_logical_io_reads,
         min_logical_io_reads = qsrs.min_logical_io_reads,
         max_logical_io_reads = qsrs.max_logical_io_reads,
         avg_logical_io_writes = qsrs.avg_logical_io_writes,
         min_logical_io_writes = qsrs.min_logical_io_writes,
         max_logical_io_writes = qsrs.max_logical_io_writes,
         avg_physical_io_reads = qsrs.avg_physical_io_reads,
         min_physical_io_reads = qsrs.min_physical_io_reads,
         max_physical_io_reads = qsrs.max_physical_io_reads,
         avg_clr_time = qsrs.avg_clr_time,
         min_clr_time = qsrs.min_clr_time,
         max_clr_time = qsrs.max_clr_time,
         min_dop = qsrs.min_dop,
         max_dop = qsrs.max_dop,
         avg_query_max_used_memory = qsrs.avg_query_max_used_memory,
         min_query_max_used_memory = qsrs.min_query_max_used_memory,
         max_query_max_used_memory = qsrs.max_query_max_used_memory,
         avg_rowcount = qsrs.avg_rowcount,
         min_rowcount = qsrs.min_rowcount,
         max_rowcount = qsrs.max_rowcount,
         {numPhysIoReadsCols}
         {logBytesCols}
         {tempdbCols}
         {planTypeCol}
         {planForcingCol}
         is_forced_plan = qsp.is_forced_plan,
         force_failure_count = qsp.force_failure_count,
         last_force_failure_reason = qsp.last_force_failure_reason_desc,
         compatibility_level = qsp.compatibility_level,
         {planTextCol}
         query_plan_hash = CONVERT(varchar(64), qsp.query_plan_hash, 1),
         {replicaRoleCol}
     FROM sys.query_store_runtime_stats AS qsrs
     JOIN sys.query_store_plan AS qsp
       ON qsp.plan_id = qsrs.plan_id
     JOIN sys.query_store_query AS qsq
       ON qsq.query_id = qsp.query_id
     JOIN sys.query_store_query_text AS qst
       ON qst.query_text_id = qsq.query_text_id
     {replicaJoin}
     WHERE qsrs.last_execution_time > @cutoff_time
     AND   qst.query_sql_text NOT LIKE N''%PerformanceMonitorLite%''
     ORDER BY qsrs.last_execution_time DESC
     OPTION(RECOMPILE, LOOP JOIN);',
    N'@cutoff_time datetime2(7)',
    @cutoff_time;";

        return new CollectorQuery(text, new List<CollectorParameter>
        {
            new("@cutoff_time", cutoffTime, CollectorParameterType.DateTime2),
        });
    }

    public override async ValueTask ReadItemAsync(string item, DbDataReader reader, List<Row> rows, CollectorContext context, CancellationToken cancellationToken)
    {
        /* Reset the per-item truncation signal — the host reads it immediately after this returns. */
        context.PerItemTextBudgetExceeded = false;

        /* Client-side cumulative BYTE budget (#1556) — the PRIMARY memory bound. The server-side TOP caps
           the ROW COUNT, but a row carries two nvarchar(max) fields (query text + plan XML), so 50k rows
           can still be gigabytes. Accumulate the materialized text size and STOP reading at the budget,
           disposing the reader early, so one database can never balloon the process. */
        var budget = PerItemTextByteBudget ?? int.MaxValue;
        long textBytes = 0;

        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Row
            {
                DatabaseName = item,
                QueryId = reader.GetInt64(0),
                PlanId = reader.GetInt64(1),
                ExecutionTypeDesc = reader.IsDBNull(2) ? null : reader.GetString(2),
                FirstExecutionTime = reader.IsDBNull(3) ? null : ((DateTimeOffset)reader.GetValue(3)).UtcDateTime,
                LastExecutionTime = reader.IsDBNull(4) ? null : ((DateTimeOffset)reader.GetValue(4)).UtcDateTime,
                ModuleName = reader.IsDBNull(5) ? null : reader.GetString(5),
                QueryText = reader.IsDBNull(6) ? null : reader.GetString(6),
                QueryHash = reader.IsDBNull(7) ? null : reader.GetString(7),
                ExecutionCount = reader.GetInt64(8),
                AvgDurationUs = ReadNullableInt64(reader, 9),
                MinDurationUs = ReadNullableInt64(reader, 10),
                MaxDurationUs = ReadNullableInt64(reader, 11),
                AvgCpuTimeUs = ReadNullableInt64(reader, 12),
                MinCpuTimeUs = ReadNullableInt64(reader, 13),
                MaxCpuTimeUs = ReadNullableInt64(reader, 14),
                AvgLogicalIoReads = ReadNullableInt64(reader, 15),
                MinLogicalIoReads = ReadNullableInt64(reader, 16),
                MaxLogicalIoReads = ReadNullableInt64(reader, 17),
                AvgLogicalIoWrites = ReadNullableInt64(reader, 18),
                MinLogicalIoWrites = ReadNullableInt64(reader, 19),
                MaxLogicalIoWrites = ReadNullableInt64(reader, 20),
                AvgPhysicalIoReads = ReadNullableInt64(reader, 21),
                MinPhysicalIoReads = ReadNullableInt64(reader, 22),
                MaxPhysicalIoReads = ReadNullableInt64(reader, 23),
                AvgClrTimeUs = ReadNullableInt64(reader, 24),
                MinClrTimeUs = ReadNullableInt64(reader, 25),
                MaxClrTimeUs = ReadNullableInt64(reader, 26),
                MinDop = ReadNullableInt64(reader, 27),
                MaxDop = ReadNullableInt64(reader, 28),
                AvgQueryMaxUsedMemory = ReadNullableInt64(reader, 29),
                MinQueryMaxUsedMemory = ReadNullableInt64(reader, 30),
                MaxQueryMaxUsedMemory = ReadNullableInt64(reader, 31),
                AvgRowcount = ReadNullableInt64(reader, 32),
                MinRowcount = ReadNullableInt64(reader, 33),
                MaxRowcount = ReadNullableInt64(reader, 34),
                AvgNumPhysicalIoReads = ReadNullableInt64(reader, 35),
                MinNumPhysicalIoReads = ReadNullableInt64(reader, 36),
                MaxNumPhysicalIoReads = ReadNullableInt64(reader, 37),
                AvgLogBytesUsed = ReadNullableInt64(reader, 38),
                MinLogBytesUsed = ReadNullableInt64(reader, 39),
                MaxLogBytesUsed = ReadNullableInt64(reader, 40),
                AvgTempdbSpaceUsed = ReadNullableInt64(reader, 41),
                MinTempdbSpaceUsed = ReadNullableInt64(reader, 42),
                MaxTempdbSpaceUsed = ReadNullableInt64(reader, 43),
                PlanType = reader.IsDBNull(44) ? null : reader.GetString(44),
                PlanForcingType = reader.IsDBNull(45) ? null : reader.GetString(45),
                IsForcedPlan = !reader.IsDBNull(46) && reader.GetBoolean(46),
                ForceFailureCount = reader.IsDBNull(47) ? 0L : reader.GetInt64(47),
                LastForceFailureReason = reader.IsDBNull(48) ? null : reader.GetString(48),
                CompatibilityLevel = reader.IsDBNull(49) ? 0 : Convert.ToInt32(reader.GetValue(49), CultureInfo.InvariantCulture),
                QueryPlanText = reader.IsDBNull(50) ? null : reader.GetString(50),
                QueryPlanHash = reader.IsDBNull(51) ? null : reader.GetString(51),
                ReplicaRole = reader.IsDBNull(52) ? null : reader.GetString(52),
            };
            rows.Add(row);

            /* char count × 2 for UTF-16. The plan XML is deduped server-side (NULL on all but the newest
               interval per plan_id), but the query text repeats on every interval row, so this budget is
               what bounds that repetition too. At the budget, signal truncation and stop — the host
               surfaces the WARNING. Rows are read newest-first and the per-database watermark advances to
               the newest KEPT row, so the older unread intervals are deliberately ABANDONED behind it
               (bounded per cycle, by design) — not retried next cycle. */
            textBytes += ((long)(row.QueryText?.Length ?? 0) + (row.QueryPlanText?.Length ?? 0)) * 2L;
            if (textBytes >= budget)
            {
                context.PerItemTextBudgetExceeded = true;
                break;
            }
        }
    }

    /// <summary>Never called — enumeration drives this collector.</summary>
    public override ValueTask<List<Row>> ReadAsync(DbDataReader reader, CollectorContext context, CancellationToken cancellationToken)
        => throw new NotSupportedException("query_store enumerates databases; ReadItemAsync drives row reads.");

    /// <summary>
    /// Reads a nullable int64, converting float/decimal Query Store values to long.
    /// Query Store runtime_stats columns are stored as float in the catalog but represent
    /// integer-scale values.
    /// </summary>
    private static long ReadNullableInt64(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return 0L;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            long l => l,
            int i => i,
            short s => s,
            decimal d => (long)d,
            double dbl => (long)dbl,
            float f => (long)f,
            _ => Convert.ToInt64(value, CultureInfo.InvariantCulture)
        };
    }

    public override IReadOnlyList<CollectorColumn> PayloadColumns { get; } = new[]
    {
        new CollectorColumn("database_name", CollectorColumnType.Varchar),
        new CollectorColumn("query_id", CollectorColumnType.BigInt),
        new CollectorColumn("plan_id", CollectorColumnType.BigInt),
        new CollectorColumn("execution_type_desc", CollectorColumnType.Varchar),
        new CollectorColumn("first_execution_time", CollectorColumnType.Timestamp),
        new CollectorColumn("last_execution_time", CollectorColumnType.Timestamp),
        new CollectorColumn("module_name", CollectorColumnType.Varchar),
        new CollectorColumn("query_text", CollectorColumnType.Varchar),
        new CollectorColumn("query_hash", CollectorColumnType.Varchar),
        new CollectorColumn("execution_count", CollectorColumnType.BigInt),
        new CollectorColumn("avg_duration_us", CollectorColumnType.BigInt),
        new CollectorColumn("min_duration_us", CollectorColumnType.BigInt),
        new CollectorColumn("max_duration_us", CollectorColumnType.BigInt),
        new CollectorColumn("avg_cpu_time_us", CollectorColumnType.BigInt),
        new CollectorColumn("min_cpu_time_us", CollectorColumnType.BigInt),
        new CollectorColumn("max_cpu_time_us", CollectorColumnType.BigInt),
        new CollectorColumn("avg_logical_io_reads", CollectorColumnType.BigInt),
        new CollectorColumn("min_logical_io_reads", CollectorColumnType.BigInt),
        new CollectorColumn("max_logical_io_reads", CollectorColumnType.BigInt),
        new CollectorColumn("avg_logical_io_writes", CollectorColumnType.BigInt),
        new CollectorColumn("min_logical_io_writes", CollectorColumnType.BigInt),
        new CollectorColumn("max_logical_io_writes", CollectorColumnType.BigInt),
        new CollectorColumn("avg_physical_io_reads", CollectorColumnType.BigInt),
        new CollectorColumn("min_physical_io_reads", CollectorColumnType.BigInt),
        new CollectorColumn("max_physical_io_reads", CollectorColumnType.BigInt),
        new CollectorColumn("avg_clr_time_us", CollectorColumnType.BigInt),
        new CollectorColumn("min_clr_time_us", CollectorColumnType.BigInt),
        new CollectorColumn("max_clr_time_us", CollectorColumnType.BigInt),
        new CollectorColumn("min_dop", CollectorColumnType.BigInt),
        new CollectorColumn("max_dop", CollectorColumnType.BigInt),
        new CollectorColumn("avg_query_max_used_memory", CollectorColumnType.BigInt),
        new CollectorColumn("min_query_max_used_memory", CollectorColumnType.BigInt),
        new CollectorColumn("max_query_max_used_memory", CollectorColumnType.BigInt),
        new CollectorColumn("avg_rowcount", CollectorColumnType.BigInt),
        new CollectorColumn("min_rowcount", CollectorColumnType.BigInt),
        new CollectorColumn("max_rowcount", CollectorColumnType.BigInt),
        new CollectorColumn("avg_num_physical_io_reads", CollectorColumnType.BigInt),
        new CollectorColumn("min_num_physical_io_reads", CollectorColumnType.BigInt),
        new CollectorColumn("max_num_physical_io_reads", CollectorColumnType.BigInt),
        new CollectorColumn("avg_log_bytes_used", CollectorColumnType.BigInt),
        new CollectorColumn("min_log_bytes_used", CollectorColumnType.BigInt),
        new CollectorColumn("max_log_bytes_used", CollectorColumnType.BigInt),
        new CollectorColumn("avg_tempdb_space_used", CollectorColumnType.BigInt),
        new CollectorColumn("min_tempdb_space_used", CollectorColumnType.BigInt),
        new CollectorColumn("max_tempdb_space_used", CollectorColumnType.BigInt),
        new CollectorColumn("plan_type", CollectorColumnType.Varchar),
        new CollectorColumn("plan_forcing_type", CollectorColumnType.Varchar),
        new CollectorColumn("is_forced_plan", CollectorColumnType.Boolean),
        new CollectorColumn("force_failure_count", CollectorColumnType.BigInt),
        new CollectorColumn("last_force_failure_reason", CollectorColumnType.Varchar),
        new CollectorColumn("compatibility_level", CollectorColumnType.Integer),
        new CollectorColumn("query_plan_text", CollectorColumnType.Varchar),
        new CollectorColumn("query_plan_hash", CollectorColumnType.Varchar),
        /* Appended at the END, not grouped with the identity columns: both hosts' bulk writers are
           POSITIONAL (Lite's DuckDB appender, Darling's Npgsql binary COPY), and an upgraded store gets
           this column from an ALTER TABLE ADD COLUMN, which can only append. A mid-list position would
           put it mid-table on a FRESH store (whose DDL is generated from this list) and last on an
           UPGRADED one — the same COPY then writing shifted values on one of them. Same reasoning as
           deadlocks.database_name (Darling V27 / Lite v46). */
        new CollectorColumn("replica_role", CollectorColumnType.Varchar),
    };

    public override void WritePayload(Row row, ICollectorRowWriter writer, CollectorContext context)
    {
        writer
            .Value(row.DatabaseName)
            .Value(row.QueryId)
            .Value(row.PlanId)
            .Value(row.ExecutionTypeDesc)
            .Value(row.FirstExecutionTime)
            .Value(row.LastExecutionTime)
            .Value(row.ModuleName)
            .Value(row.QueryText)
            .Value(row.QueryHash)
            .Value(row.ExecutionCount)
            .Value(row.AvgDurationUs)
            .Value(row.MinDurationUs)
            .Value(row.MaxDurationUs)
            .Value(row.AvgCpuTimeUs)
            .Value(row.MinCpuTimeUs)
            .Value(row.MaxCpuTimeUs)
            .Value(row.AvgLogicalIoReads)
            .Value(row.MinLogicalIoReads)
            .Value(row.MaxLogicalIoReads)
            .Value(row.AvgLogicalIoWrites)
            .Value(row.MinLogicalIoWrites)
            .Value(row.MaxLogicalIoWrites)
            .Value(row.AvgPhysicalIoReads)
            .Value(row.MinPhysicalIoReads)
            .Value(row.MaxPhysicalIoReads)
            .Value(row.AvgClrTimeUs)
            .Value(row.MinClrTimeUs)
            .Value(row.MaxClrTimeUs)
            .Value(row.MinDop)
            .Value(row.MaxDop)
            .Value(row.AvgQueryMaxUsedMemory)
            .Value(row.MinQueryMaxUsedMemory)
            .Value(row.MaxQueryMaxUsedMemory)
            .Value(row.AvgRowcount)
            .Value(row.MinRowcount)
            .Value(row.MaxRowcount)
            .Value(row.AvgNumPhysicalIoReads)
            .Value(row.MinNumPhysicalIoReads)
            .Value(row.MaxNumPhysicalIoReads)
            .Value(row.AvgLogBytesUsed)
            .Value(row.MinLogBytesUsed)
            .Value(row.MaxLogBytesUsed)
            .Value(row.AvgTempdbSpaceUsed)
            .Value(row.MinTempdbSpaceUsed)
            .Value(row.MaxTempdbSpaceUsed)
            .Value(row.PlanType)
            .Value(row.PlanForcingType)
            .Value(row.IsForcedPlan)
            .Value(row.ForceFailureCount)
            .Value(row.LastForceFailureReason)
            .Value(row.CompatibilityLevel)
            .Value(row.QueryPlanText)
            .Value(row.QueryPlanHash)
            .Value(row.ReplicaRole);
    }
}
