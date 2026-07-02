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
/// Per-table and per-index size, usage, and locking statistics (growth trending, unused-index
/// detection, contention analysis). Extracted verbatim from Lite's
/// RemoteCollectorService.IndexObjectStats.cs. All three DMVs are database-scoped, so collection
/// runs ONE COMMAND PER DATABASE: on-prem enumerates accessible online databases then sends the
/// staged body through [db].sys.sp_executesql (quote-doubled); Azure SQL DB connects per database
/// (RunsPerDatabase). Within each database the three DMVs stage into #temps with single scans
/// then join — the sp_IndexCleanup technique that fixed the #1135 monolithic-join timeouts —
/// under a dedicated 300 s per-database budget (CommandTimeoutSecondsOverride; the enumeration
/// keeps the default). sqlserver_start_time flags restart-class counter resets for the read layer.
/// </summary>
public sealed class IndexObjectStatsCollector : CollectorDefinitionBase<IndexObjectStatsCollector.Row>
{
    public static IndexObjectStatsCollector Instance { get; } = new();

    private IndexObjectStatsCollector()
    {
    }

    public sealed class Row
    {
        public DateTime? SqlServerStartTime { get; set; }
        public string DatabaseName { get; set; } = "";
        public int DatabaseId { get; set; }
        public string SchemaName { get; set; } = "";
        public int ObjectId { get; set; }
        public string TableName { get; set; } = "";
        public int IndexId { get; set; }
        public string? IndexName { get; set; }
        public string? IndexTypeDesc { get; set; }
        public bool? IsUnique { get; set; }
        public bool? IsPrimaryKey { get; set; }
        public bool? IsFiltered { get; set; }
        public int? PartitionCount { get; set; }
        public decimal? ReservedMb { get; set; }
        public decimal? UsedMb { get; set; }
        public decimal? InRowDataMb { get; set; }
        public decimal? LobDataMb { get; set; }
        public decimal? RowOverflowMb { get; set; }
        public long? TotalRows { get; set; }
        public long? UserSeeks { get; set; }
        public long? UserScans { get; set; }
        public long? UserLookups { get; set; }
        public long? UserUpdates { get; set; }
        public DateTime? LastUserSeek { get; set; }
        public DateTime? LastUserScan { get; set; }
        public DateTime? LastUserLookup { get; set; }
        public DateTime? LastUserUpdate { get; set; }
        public long? LeafInsertCount { get; set; }
        public long? LeafUpdateCount { get; set; }
        public long? LeafDeleteCount { get; set; }
        public long? RangeScanCount { get; set; }
        public long? SingletonLookupCount { get; set; }
        public long? RowLockCount { get; set; }
        public long? RowLockWaitCount { get; set; }
        public long? RowLockWaitInMs { get; set; }
        public long? PageLockCount { get; set; }
        public long? PageLockWaitCount { get; set; }
        public long? PageLockWaitInMs { get; set; }
        public long? IndexLockPromotionAttemptCount { get; set; }
        public long? IndexLockPromotionCount { get; set; }
        public long? PageLatchWaitCount { get; set; }
        public long? PageLatchWaitInMs { get; set; }
        public long? PageIoLatchWaitCount { get; set; }
        public long? PageIoLatchWaitInMs { get; set; }
    }

    /// <summary>The per-database staged body — the ordinals of its final SELECT are the read contract.</summary>
    internal const string PerDatabaseStatsBody = @"
SET NOCOUNT ON;
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

/* Size + row counts (one scan of dm_db_partition_stats) */
SELECT
    dps.object_id,
    dps.index_id,
    partition_count = COUNT_BIG(*),
    reserved_pages = SUM(dps.reserved_page_count),
    used_pages = SUM(dps.used_page_count),
    in_row_pages = SUM(dps.in_row_data_page_count),
    lob_pages = SUM(dps.lob_used_page_count),
    row_overflow_pages = SUM(dps.row_overflow_used_page_count),
    total_rows = SUM(dps.row_count)
INTO #sizes
FROM sys.dm_db_partition_stats AS dps
GROUP BY
    dps.object_id,
    dps.index_id
OPTION(RECOMPILE);

/* Usage counters (one scan of dm_db_index_usage_stats for this database) */
SELECT
    us.object_id,
    us.index_id,
    us.user_seeks,
    us.user_scans,
    us.user_lookups,
    us.user_updates,
    us.last_user_seek,
    us.last_user_scan,
    us.last_user_lookup,
    us.last_user_update
INTO #usage
FROM sys.dm_db_index_usage_stats AS us
WHERE us.database_id = DB_ID()
OPTION(RECOMPILE);

/* Locking/latch counters (one scan of dm_db_index_operational_stats - the heavy DMV) */
SELECT
    ios.object_id,
    ios.index_id,
    leaf_insert_count = SUM(ios.leaf_insert_count),
    leaf_update_count = SUM(ios.leaf_update_count),
    leaf_delete_count = SUM(ios.leaf_delete_count),
    range_scan_count = SUM(ios.range_scan_count),
    singleton_lookup_count = SUM(ios.singleton_lookup_count),
    row_lock_count = SUM(ios.row_lock_count),
    row_lock_wait_count = SUM(ios.row_lock_wait_count),
    row_lock_wait_in_ms = SUM(ios.row_lock_wait_in_ms),
    page_lock_count = SUM(ios.page_lock_count),
    page_lock_wait_count = SUM(ios.page_lock_wait_count),
    page_lock_wait_in_ms = SUM(ios.page_lock_wait_in_ms),
    index_lock_promotion_attempt_count = SUM(ios.index_lock_promotion_attempt_count),
    index_lock_promotion_count = SUM(ios.index_lock_promotion_count),
    page_latch_wait_count = SUM(ios.page_latch_wait_count),
    page_latch_wait_in_ms = SUM(ios.page_latch_wait_in_ms),
    page_io_latch_wait_count = SUM(ios.page_io_latch_wait_count),
    page_io_latch_wait_in_ms = SUM(ios.page_io_latch_wait_in_ms)
INTO #ops
FROM sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL) AS ios
GROUP BY
    ios.object_id,
    ios.index_id
OPTION(RECOMPILE);

SELECT
    sqlserver_start_time = (SELECT osi.sqlserver_start_time FROM sys.dm_os_sys_info AS osi),
    database_name = DB_NAME(),
    database_id = DB_ID(),
    schema_name = s.name,
    object_id = o.object_id,
    table_name = o.name,
    index_id = i.index_id,
    index_name = i.name,
    index_type_desc = i.type_desc,
    is_unique = i.is_unique,
    is_primary_key = i.is_primary_key,
    is_filtered = i.has_filter,
    partition_count = ps.partition_count,
    reserved_mb = CONVERT(decimal(19,2), ps.reserved_pages * 8.0 / 1024.0),
    used_mb = CONVERT(decimal(19,2), ps.used_pages * 8.0 / 1024.0),
    in_row_data_mb = CONVERT(decimal(19,2), ps.in_row_pages * 8.0 / 1024.0),
    lob_data_mb = CONVERT(decimal(19,2), ps.lob_pages * 8.0 / 1024.0),
    row_overflow_mb = CONVERT(decimal(19,2), ps.row_overflow_pages * 8.0 / 1024.0),
    total_rows = ps.total_rows,
    user_seeks = us.user_seeks,
    user_scans = us.user_scans,
    user_lookups = us.user_lookups,
    user_updates = us.user_updates,
    last_user_seek = us.last_user_seek,
    last_user_scan = us.last_user_scan,
    last_user_lookup = us.last_user_lookup,
    last_user_update = us.last_user_update,
    leaf_insert_count = os.leaf_insert_count,
    leaf_update_count = os.leaf_update_count,
    leaf_delete_count = os.leaf_delete_count,
    range_scan_count = os.range_scan_count,
    singleton_lookup_count = os.singleton_lookup_count,
    row_lock_count = os.row_lock_count,
    row_lock_wait_count = os.row_lock_wait_count,
    row_lock_wait_in_ms = os.row_lock_wait_in_ms,
    page_lock_count = os.page_lock_count,
    page_lock_wait_count = os.page_lock_wait_count,
    page_lock_wait_in_ms = os.page_lock_wait_in_ms,
    index_lock_promotion_attempt_count = os.index_lock_promotion_attempt_count,
    index_lock_promotion_count = os.index_lock_promotion_count,
    page_latch_wait_count = os.page_latch_wait_count,
    page_latch_wait_in_ms = os.page_latch_wait_in_ms,
    page_io_latch_wait_count = os.page_io_latch_wait_count,
    page_io_latch_wait_in_ms = os.page_io_latch_wait_in_ms
FROM sys.indexes AS i
JOIN sys.objects AS o
  ON o.object_id = i.object_id
JOIN sys.schemas AS s
  ON s.schema_id = o.schema_id
LEFT JOIN #sizes AS ps
  ON  ps.object_id = i.object_id
  AND ps.index_id = i.index_id
LEFT JOIN #usage AS us
  ON  us.object_id = i.object_id
  AND us.index_id = i.index_id
LEFT JOIN #ops AS os
  ON  os.object_id = i.object_id
  AND os.index_id = i.index_id
WHERE o.is_ms_shipped = 0
AND   o.type IN (N'U', N'V')
OPTION(RECOMPILE);";

    public override string Name => "index_object_stats";

    public override string TargetTable => "index_object_stats";

    /// <summary>The heavy per-database sweep gets the dedicated #1135 budget.</summary>
    public override int? CommandTimeoutSecondsOverride => 300;

    /// <summary>Azure SQL DB cannot cross databases — one connection per database.</summary>
    public override bool RunsPerDatabase(CollectorTargetInfo target) => target.IsAzureSqlDb;

    /// <summary>Azure per-database connections run the staged body directly.</summary>
    public override CollectorQuery BuildQuery(CollectorContext context) => new(PerDatabaseStatsBody);

    /// <summary>On-prem: enumerate accessible online databases (with exclusions), then per-item.</summary>
    public override CollectorQuery? BuildEnumerationQuery(CollectorContext context)
    {
        if (context.Target.IsAzureSqlDb)
        {
            return null;
        }

        var (exclusionClause, exclusionParameters) = DatabaseExclusionFilter.Build(context.ExcludedDatabases, "d.name");
        var text = $@"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SELECT
    d.name
FROM sys.databases AS d
WHERE d.state_desc = N'ONLINE'
AND   d.database_id > 0
AND   HAS_DBACCESS(d.name) = 1
{exclusionClause}
ORDER BY
    d.name;";

        return new CollectorQuery(text, exclusionParameters);
    }

    public override CollectorQuery BuildPerItemQuery(string item, CollectorContext context)
    {
        /* Double single quotes so the body survives nesting inside [db].sys.sp_executesql N'...' */
        var escapedBody = PerDatabaseStatsBody.Replace("'", "''", StringComparison.Ordinal);
        var escapedDbName = item.Replace("]", "]]", StringComparison.Ordinal);
        return new CollectorQuery($"EXECUTE [{escapedDbName}].sys.sp_executesql N'{escapedBody}';");
    }

    public override async ValueTask<List<Row>> ReadAsync(DbDataReader reader, CollectorContext context, CancellationToken cancellationToken)
    {
        var rows = new List<Row>();
        await ParseRowsAsync(reader, rows, cancellationToken);
        return rows;
    }

    public override ValueTask ReadItemAsync(string item, DbDataReader reader, List<Row> rows, CollectorContext context, CancellationToken cancellationToken)
        => new(ParseRowsAsync(reader, rows, cancellationToken));

    private static async Task ParseRowsAsync(DbDataReader reader, List<Row> rows, CancellationToken cancellationToken)
    {
        long? L(int i) => reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);
        int? I(int i) => reader.IsDBNull(i) ? null : Convert.ToInt32(reader.GetValue(i), CultureInfo.InvariantCulture);
        decimal? D(int i) => reader.IsDBNull(i) ? null : reader.GetDecimal(i);
        DateTime? T(int i) => reader.IsDBNull(i) ? null : reader.GetDateTime(i);
        bool? B(int i) => reader.IsDBNull(i) ? null : (bool?)(Convert.ToInt32(reader.GetValue(i), CultureInfo.InvariantCulture) == 1);

        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new Row
            {
                SqlServerStartTime = T(0),
                DatabaseName = reader.GetString(1),
                DatabaseId = Convert.ToInt32(reader.GetValue(2), CultureInfo.InvariantCulture),
                SchemaName = reader.GetString(3),
                ObjectId = Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture),
                TableName = reader.GetString(5),
                IndexId = Convert.ToInt32(reader.GetValue(6), CultureInfo.InvariantCulture),
                IndexName = reader.IsDBNull(7) ? null : reader.GetString(7),
                IndexTypeDesc = reader.IsDBNull(8) ? null : reader.GetString(8),
                IsUnique = B(9),
                IsPrimaryKey = B(10),
                IsFiltered = B(11),
                PartitionCount = I(12),
                ReservedMb = D(13),
                UsedMb = D(14),
                InRowDataMb = D(15),
                LobDataMb = D(16),
                RowOverflowMb = D(17),
                TotalRows = L(18),
                UserSeeks = L(19),
                UserScans = L(20),
                UserLookups = L(21),
                UserUpdates = L(22),
                LastUserSeek = T(23),
                LastUserScan = T(24),
                LastUserLookup = T(25),
                LastUserUpdate = T(26),
                LeafInsertCount = L(27),
                LeafUpdateCount = L(28),
                LeafDeleteCount = L(29),
                RangeScanCount = L(30),
                SingletonLookupCount = L(31),
                RowLockCount = L(32),
                RowLockWaitCount = L(33),
                RowLockWaitInMs = L(34),
                PageLockCount = L(35),
                PageLockWaitCount = L(36),
                PageLockWaitInMs = L(37),
                IndexLockPromotionAttemptCount = L(38),
                IndexLockPromotionCount = L(39),
                PageLatchWaitCount = L(40),
                PageLatchWaitInMs = L(41),
                PageIoLatchWaitCount = L(42),
                PageIoLatchWaitInMs = L(43),
            });
        }
    }

    public override IReadOnlyList<CollectorColumn> PayloadColumns { get; } = new[]
    {
        new CollectorColumn("sqlserver_start_time", CollectorColumnType.Timestamp),
        new CollectorColumn("database_name", CollectorColumnType.Varchar),
        new CollectorColumn("database_id", CollectorColumnType.Integer),
        new CollectorColumn("schema_name", CollectorColumnType.Varchar),
        new CollectorColumn("object_id", CollectorColumnType.Integer),
        new CollectorColumn("table_name", CollectorColumnType.Varchar),
        new CollectorColumn("index_id", CollectorColumnType.Integer),
        new CollectorColumn("index_name", CollectorColumnType.Varchar),
        new CollectorColumn("index_type_desc", CollectorColumnType.Varchar),
        new CollectorColumn("is_unique", CollectorColumnType.Boolean),
        new CollectorColumn("is_primary_key", CollectorColumnType.Boolean),
        new CollectorColumn("is_filtered", CollectorColumnType.Boolean),
        new CollectorColumn("partition_count", CollectorColumnType.Integer),
        new CollectorColumn("reserved_mb", CollectorColumnType.Decimal, 19, 2),
        new CollectorColumn("used_mb", CollectorColumnType.Decimal, 19, 2),
        new CollectorColumn("in_row_data_mb", CollectorColumnType.Decimal, 19, 2),
        new CollectorColumn("lob_data_mb", CollectorColumnType.Decimal, 19, 2),
        new CollectorColumn("row_overflow_mb", CollectorColumnType.Decimal, 19, 2),
        new CollectorColumn("total_rows", CollectorColumnType.BigInt),
        new CollectorColumn("user_seeks", CollectorColumnType.BigInt),
        new CollectorColumn("user_scans", CollectorColumnType.BigInt),
        new CollectorColumn("user_lookups", CollectorColumnType.BigInt),
        new CollectorColumn("user_updates", CollectorColumnType.BigInt),
        new CollectorColumn("last_user_seek", CollectorColumnType.Timestamp),
        new CollectorColumn("last_user_scan", CollectorColumnType.Timestamp),
        new CollectorColumn("last_user_lookup", CollectorColumnType.Timestamp),
        new CollectorColumn("last_user_update", CollectorColumnType.Timestamp),
        new CollectorColumn("leaf_insert_count", CollectorColumnType.BigInt),
        new CollectorColumn("leaf_update_count", CollectorColumnType.BigInt),
        new CollectorColumn("leaf_delete_count", CollectorColumnType.BigInt),
        new CollectorColumn("range_scan_count", CollectorColumnType.BigInt),
        new CollectorColumn("singleton_lookup_count", CollectorColumnType.BigInt),
        new CollectorColumn("row_lock_count", CollectorColumnType.BigInt),
        new CollectorColumn("row_lock_wait_count", CollectorColumnType.BigInt),
        new CollectorColumn("row_lock_wait_in_ms", CollectorColumnType.BigInt),
        new CollectorColumn("page_lock_count", CollectorColumnType.BigInt),
        new CollectorColumn("page_lock_wait_count", CollectorColumnType.BigInt),
        new CollectorColumn("page_lock_wait_in_ms", CollectorColumnType.BigInt),
        new CollectorColumn("index_lock_promotion_attempt_count", CollectorColumnType.BigInt),
        new CollectorColumn("index_lock_promotion_count", CollectorColumnType.BigInt),
        new CollectorColumn("page_latch_wait_count", CollectorColumnType.BigInt),
        new CollectorColumn("page_latch_wait_in_ms", CollectorColumnType.BigInt),
        new CollectorColumn("page_io_latch_wait_count", CollectorColumnType.BigInt),
        new CollectorColumn("page_io_latch_wait_in_ms", CollectorColumnType.BigInt),
    };

    public override void WritePayload(Row row, ICollectorRowWriter writer, CollectorContext context)
    {
        writer
            .Value(row.SqlServerStartTime)
            .Value(row.DatabaseName)
            .Value(row.DatabaseId)
            .Value(row.SchemaName)
            .Value(row.ObjectId)
            .Value(row.TableName)
            .Value(row.IndexId)
            .Value(row.IndexName)
            .Value(row.IndexTypeDesc)
            .Value(row.IsUnique)
            .Value(row.IsPrimaryKey)
            .Value(row.IsFiltered)
            .Value(row.PartitionCount)
            .Value(row.ReservedMb)
            .Value(row.UsedMb)
            .Value(row.InRowDataMb)
            .Value(row.LobDataMb)
            .Value(row.RowOverflowMb)
            .Value(row.TotalRows)
            .Value(row.UserSeeks)
            .Value(row.UserScans)
            .Value(row.UserLookups)
            .Value(row.UserUpdates)
            .Value(row.LastUserSeek)
            .Value(row.LastUserScan)
            .Value(row.LastUserLookup)
            .Value(row.LastUserUpdate)
            .Value(row.LeafInsertCount)
            .Value(row.LeafUpdateCount)
            .Value(row.LeafDeleteCount)
            .Value(row.RangeScanCount)
            .Value(row.SingletonLookupCount)
            .Value(row.RowLockCount)
            .Value(row.RowLockWaitCount)
            .Value(row.RowLockWaitInMs)
            .Value(row.PageLockCount)
            .Value(row.PageLockWaitCount)
            .Value(row.PageLockWaitInMs)
            .Value(row.IndexLockPromotionAttemptCount)
            .Value(row.IndexLockPromotionCount)
            .Value(row.PageLatchWaitCount)
            .Value(row.PageLatchWaitInMs)
            .Value(row.PageIoLatchWaitCount)
            .Value(row.PageIoLatchWaitInMs);
    }
}
