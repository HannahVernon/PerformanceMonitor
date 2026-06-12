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
    /// Collects per-table and per-index size, usage, and locking statistics for growth
    /// trending, unused-index detection, and contention analysis.
    /// Size columns are absolute point-in-time values; usage and locking counters are
    /// cumulative (reset on instance restart / DB detach / AUTO_CLOSE) - sqlserver_start_time
    /// carries the reset boundary so deltas can be computed safely in the read layer.
    /// All three DMVs are database-scoped: on-prem iterates databases via a cursor + cross-DB
    /// sp_executesql into a #temp; Azure SQL DB connects to each database individually.
    /// In-Memory OLTP (Hekaton) objects are not represented by these DMVs.
    /// </summary>
    private async Task<int> CollectIndexObjectStatsAsync(ServerConnection server, CancellationToken cancellationToken)
    {
        var serverStatus = _serverManager.GetConnectionStatus(server.Id);
        bool isAzureSqlDb = serverStatus?.SqlEngineEdition == 5;

        string onPremQuery = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;
SET NOCOUNT ON;

DECLARE
    @sqlserver_start_time datetime2(7) =
        (SELECT osi.sqlserver_start_time FROM sys.dm_os_sys_info AS osi),
    @db_name sysname,
    @sql nvarchar(MAX);

CREATE TABLE #ios
(
    database_name sysname NOT NULL,
    database_id int NOT NULL,
    schema_name sysname NOT NULL,
    object_id int NOT NULL,
    table_name sysname NOT NULL,
    index_id int NOT NULL,
    index_name sysname NULL,
    index_type_desc nvarchar(60) NULL,
    is_unique bit NULL,
    is_primary_key bit NULL,
    is_filtered bit NULL,
    partition_count int NULL,
    reserved_mb decimal(19,2) NULL,
    used_mb decimal(19,2) NULL,
    in_row_data_mb decimal(19,2) NULL,
    lob_data_mb decimal(19,2) NULL,
    row_overflow_mb decimal(19,2) NULL,
    total_rows bigint NULL,
    user_seeks bigint NULL,
    user_scans bigint NULL,
    user_lookups bigint NULL,
    user_updates bigint NULL,
    last_user_seek datetime2(7) NULL,
    last_user_scan datetime2(7) NULL,
    last_user_lookup datetime2(7) NULL,
    last_user_update datetime2(7) NULL,
    leaf_insert_count bigint NULL,
    leaf_update_count bigint NULL,
    leaf_delete_count bigint NULL,
    range_scan_count bigint NULL,
    singleton_lookup_count bigint NULL,
    row_lock_count bigint NULL,
    row_lock_wait_count bigint NULL,
    row_lock_wait_in_ms bigint NULL,
    page_lock_count bigint NULL,
    page_lock_wait_count bigint NULL,
    page_lock_wait_in_ms bigint NULL,
    index_lock_promotion_attempt_count bigint NULL,
    index_lock_promotion_count bigint NULL,
    page_latch_wait_count bigint NULL,
    page_latch_wait_in_ms bigint NULL,
    page_io_latch_wait_count bigint NULL,
    page_io_latch_wait_in_ms bigint NULL
);

DECLARE db_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT
        d.name
    FROM sys.databases AS d
    WHERE d.state_desc = N'ONLINE'
    AND   d.database_id > 0
    AND   HAS_DBACCESS(d.name) = 1
    /*EXCLUSION_FILTER_CURSOR*/
    ORDER BY
        d.name;

OPEN db_cursor;
FETCH NEXT FROM db_cursor INTO @db_name;

WHILE @@FETCH_STATUS = 0
BEGIN
    BEGIN TRY
        SET @sql = N'EXECUTE ' + QUOTENAME(@db_name) + N'.sys.sp_executesql N''
INSERT #ios
(
    database_name, database_id, schema_name, object_id, table_name, index_id, index_name,
    index_type_desc, is_unique, is_primary_key, is_filtered, partition_count, reserved_mb,
    used_mb, in_row_data_mb, lob_data_mb, row_overflow_mb, total_rows, user_seeks, user_scans,
    user_lookups, user_updates, last_user_seek, last_user_scan, last_user_lookup, last_user_update,
    leaf_insert_count, leaf_update_count, leaf_delete_count, range_scan_count, singleton_lookup_count,
    row_lock_count, row_lock_wait_count, row_lock_wait_in_ms, page_lock_count, page_lock_wait_count,
    page_lock_wait_in_ms, index_lock_promotion_attempt_count, index_lock_promotion_count,
    page_latch_wait_count, page_latch_wait_in_ms, page_io_latch_wait_count, page_io_latch_wait_in_ms
)
SELECT
    DB_NAME(), DB_ID(), s.name, o.object_id, o.name, i.index_id, i.name, i.type_desc,
    i.is_unique, i.is_primary_key, i.has_filter, ps.partition_count,
    CONVERT(decimal(19,2), ps.reserved_pages * 8.0 / 1024.0),
    CONVERT(decimal(19,2), ps.used_pages * 8.0 / 1024.0),
    CONVERT(decimal(19,2), ps.in_row_pages * 8.0 / 1024.0),
    CONVERT(decimal(19,2), ps.lob_pages * 8.0 / 1024.0),
    CONVERT(decimal(19,2), ps.row_overflow_pages * 8.0 / 1024.0),
    ps.total_rows, us.user_seeks, us.user_scans, us.user_lookups, us.user_updates,
    us.last_user_seek, us.last_user_scan, us.last_user_lookup, us.last_user_update,
    os.leaf_insert_count, os.leaf_update_count, os.leaf_delete_count, os.range_scan_count,
    os.singleton_lookup_count, os.row_lock_count, os.row_lock_wait_count, os.row_lock_wait_in_ms,
    os.page_lock_count, os.page_lock_wait_count, os.page_lock_wait_in_ms,
    os.index_lock_promotion_attempt_count, os.index_lock_promotion_count,
    os.page_latch_wait_count, os.page_latch_wait_in_ms, os.page_io_latch_wait_count,
    os.page_io_latch_wait_in_ms
FROM sys.indexes AS i
JOIN sys.objects AS o
  ON o.object_id = i.object_id
JOIN sys.schemas AS s
  ON s.schema_id = o.schema_id
LEFT JOIN
(
    SELECT
        dps.object_id, dps.index_id,
        partition_count = COUNT_BIG(*),
        reserved_pages = SUM(dps.reserved_page_count),
        used_pages = SUM(dps.used_page_count),
        in_row_pages = SUM(dps.in_row_data_page_count),
        lob_pages = SUM(dps.lob_used_page_count),
        row_overflow_pages = SUM(dps.row_overflow_used_page_count),
        total_rows = SUM(dps.row_count)
    FROM sys.dm_db_partition_stats AS dps
    GROUP BY dps.object_id, dps.index_id
) AS ps
  ON ps.object_id = i.object_id AND ps.index_id = i.index_id
LEFT JOIN sys.dm_db_index_usage_stats AS us
  ON us.database_id = DB_ID() AND us.object_id = i.object_id AND us.index_id = i.index_id
LEFT JOIN
(
    SELECT
        ios.object_id, ios.index_id,
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
    FROM sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL) AS ios
    GROUP BY ios.object_id, ios.index_id
) AS os
  ON os.object_id = i.object_id AND os.index_id = i.index_id
WHERE o.is_ms_shipped = 0
AND   o.type IN (N''''U'''', N''''V'''')
OPTION(RECOMPILE);'';';

        EXECUTE sys.sp_executesql @sql;
    END TRY
    BEGIN CATCH
    END CATCH;

    FETCH NEXT FROM db_cursor INTO @db_name;
END;

CLOSE db_cursor;
DEALLOCATE db_cursor;

SELECT
    sqlserver_start_time = @sqlserver_start_time,
    x.database_name,
    x.database_id,
    x.schema_name,
    x.object_id,
    x.table_name,
    x.index_id,
    x.index_name,
    x.index_type_desc,
    x.is_unique,
    x.is_primary_key,
    x.is_filtered,
    x.partition_count,
    x.reserved_mb,
    x.used_mb,
    x.in_row_data_mb,
    x.lob_data_mb,
    x.row_overflow_mb,
    x.total_rows,
    x.user_seeks,
    x.user_scans,
    x.user_lookups,
    x.user_updates,
    x.last_user_seek,
    x.last_user_scan,
    x.last_user_lookup,
    x.last_user_update,
    x.leaf_insert_count,
    x.leaf_update_count,
    x.leaf_delete_count,
    x.range_scan_count,
    x.singleton_lookup_count,
    x.row_lock_count,
    x.row_lock_wait_count,
    x.row_lock_wait_in_ms,
    x.page_lock_count,
    x.page_lock_wait_count,
    x.page_lock_wait_in_ms,
    x.index_lock_promotion_attempt_count,
    x.index_lock_promotion_count,
    x.page_latch_wait_count,
    x.page_latch_wait_in_ms,
    x.page_io_latch_wait_count,
    x.page_io_latch_wait_in_ms
FROM #ios AS x
ORDER BY
    x.reserved_mb DESC;";

        var (exclusionClause, _) = BuildDatabaseExclusionFilter(server.ExcludedDatabases, "d.name");
        onPremQuery = onPremQuery.Replace("/*EXCLUSION_FILTER_CURSOR*/", exclusionClause);

        const string azureSqlDbQuery = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

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
LEFT JOIN
(
    SELECT
        dps.object_id, dps.index_id,
        partition_count = COUNT_BIG(*),
        reserved_pages = SUM(dps.reserved_page_count),
        used_pages = SUM(dps.used_page_count),
        in_row_pages = SUM(dps.in_row_data_page_count),
        lob_pages = SUM(dps.lob_used_page_count),
        row_overflow_pages = SUM(dps.row_overflow_used_page_count),
        total_rows = SUM(dps.row_count)
    FROM sys.dm_db_partition_stats AS dps
    GROUP BY dps.object_id, dps.index_id
) AS ps
  ON ps.object_id = i.object_id AND ps.index_id = i.index_id
LEFT JOIN sys.dm_db_index_usage_stats AS us
  ON us.database_id = DB_ID() AND us.object_id = i.object_id AND us.index_id = i.index_id
LEFT JOIN
(
    SELECT
        ios.object_id, ios.index_id,
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
    FROM sys.dm_db_index_operational_stats(DB_ID(), NULL, NULL, NULL) AS ios
    GROUP BY ios.object_id, ios.index_id
) AS os
  ON os.object_id = i.object_id AND os.index_id = i.index_id
WHERE o.is_ms_shipped = 0
AND   o.type IN (N'U', N'V')
ORDER BY
    reserved_mb DESC
OPTION(RECOMPILE);";

        var serverId = GetServerId(server);
        var serverName = GetServerNameForStorage(server);
        var collectionTime = DateTime.UtcNow;
        var rowsCollected = 0;
        _lastSqlMs = 0;
        _lastDuckDbMs = 0;

        var rows = new List<IndexObjectStatRow>();
        var sqlSw = Stopwatch.StartNew();

        if (isAzureSqlDb)
        {
            var databases = await GetAzureDatabaseListAsync(server, cancellationToken);
            foreach (var dbName in databases)
            {
                try
                {
                    using var dbConn = await OpenAzureDatabaseConnectionAsync(server, dbName, cancellationToken);
                    using var cmd = new SqlCommand(azureSqlDbQuery, dbConn);
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        rows.Add(ReadIndexObjectStatRow(reader));
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug("Skipping database '{Database}' for index/object stats: {Error}", dbName, ex.Message);
                }
            }
        }
        else
        {
            using var sqlConnection = await CreateConnectionAsync(server, cancellationToken);
            using var command = new SqlCommand(onPremQuery, sqlConnection);
            command.CommandTimeout = CommandTimeoutSeconds;
            var (_, exclusionParams) = BuildDatabaseExclusionFilter(server.ExcludedDatabases, "d.name");
            foreach (var p in exclusionParams) command.Parameters.Add(p);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(ReadIndexObjectStatRow(reader));
            }
        }
        sqlSw.Stop();

        var duckSw = Stopwatch.StartNew();
        using (var duckConnection = _duckDb.CreateConnection())
        {
            await duckConnection.OpenAsync(cancellationToken);
            using (var appender = duckConnection.CreateAppender("index_object_stats"))
            {
                foreach (var r in rows)
                {
                    appender.CreateRow()
                       .AppendValue(GenerateCollectionId())
                       .AppendValue(collectionTime)
                       .AppendValue(serverId)
                       .AppendValue(serverName)
                       .AppendValue(r.SqlServerStartTime)
                       .AppendValue(r.DatabaseName)
                       .AppendValue(r.DatabaseId)
                       .AppendValue(r.SchemaName)
                       .AppendValue(r.ObjectId)
                       .AppendValue(r.TableName)
                       .AppendValue(r.IndexId)
                       .AppendValue(r.IndexName)
                       .AppendValue(r.IndexTypeDesc)
                       .AppendValue(r.IsUnique)
                       .AppendValue(r.IsPrimaryKey)
                       .AppendValue(r.IsFiltered)
                       .AppendValue(r.PartitionCount)
                       .AppendValue(r.ReservedMb)
                       .AppendValue(r.UsedMb)
                       .AppendValue(r.InRowDataMb)
                       .AppendValue(r.LobDataMb)
                       .AppendValue(r.RowOverflowMb)
                       .AppendValue(r.TotalRows)
                       .AppendValue(r.UserSeeks)
                       .AppendValue(r.UserScans)
                       .AppendValue(r.UserLookups)
                       .AppendValue(r.UserUpdates)
                       .AppendValue(r.LastUserSeek)
                       .AppendValue(r.LastUserScan)
                       .AppendValue(r.LastUserLookup)
                       .AppendValue(r.LastUserUpdate)
                       .AppendValue(r.LeafInsertCount)
                       .AppendValue(r.LeafUpdateCount)
                       .AppendValue(r.LeafDeleteCount)
                       .AppendValue(r.RangeScanCount)
                       .AppendValue(r.SingletonLookupCount)
                       .AppendValue(r.RowLockCount)
                       .AppendValue(r.RowLockWaitCount)
                       .AppendValue(r.RowLockWaitInMs)
                       .AppendValue(r.PageLockCount)
                       .AppendValue(r.PageLockWaitCount)
                       .AppendValue(r.PageLockWaitInMs)
                       .AppendValue(r.IndexLockPromotionAttemptCount)
                       .AppendValue(r.IndexLockPromotionCount)
                       .AppendValue(r.PageLatchWaitCount)
                       .AppendValue(r.PageLatchWaitInMs)
                       .AppendValue(r.PageIoLatchWaitCount)
                       .AppendValue(r.PageIoLatchWaitInMs)
                       .EndRow();
                    rowsCollected++;
                }
            }
        }
        duckSw.Stop();

        _lastSqlMs = sqlSw.ElapsedMilliseconds;
        _lastDuckDbMs = duckSw.ElapsedMilliseconds;

        _logger?.LogDebug("Collected {RowCount} index/object stat rows for server '{Server}'", rowsCollected, server.DisplayName);
        return rowsCollected;
    }

    private static IndexObjectStatRow ReadIndexObjectStatRow(SqlDataReader reader)
    {
        long? L(int i) => reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i));
        int? I(int i) => reader.IsDBNull(i) ? null : Convert.ToInt32(reader.GetValue(i));
        decimal? D(int i) => reader.IsDBNull(i) ? null : reader.GetDecimal(i);
        DateTime? T(int i) => reader.IsDBNull(i) ? null : reader.GetDateTime(i);
        bool? B(int i) => reader.IsDBNull(i) ? null : (bool?)(Convert.ToInt32(reader.GetValue(i)) == 1);

        return new IndexObjectStatRow
        {
            SqlServerStartTime = T(0),
            DatabaseName = reader.GetString(1),
            DatabaseId = Convert.ToInt32(reader.GetValue(2)),
            SchemaName = reader.GetString(3),
            ObjectId = Convert.ToInt32(reader.GetValue(4)),
            TableName = reader.GetString(5),
            IndexId = Convert.ToInt32(reader.GetValue(6)),
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
            PageIoLatchWaitInMs = L(43)
        };
    }

    private sealed class IndexObjectStatRow
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
}
