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
using System.Threading;
using System.Threading.Tasks;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// TempDB space usage from tempdb.sys.dm_db_file_space_usage plus the top tempdb-consuming
/// session (two result sets → one row). Extracted verbatim from Lite's
/// RemoteCollectorService.TempDb.cs. Always yields exactly one row — zeros when the result
/// sets are empty — matching the original collector's behavior.
/// </summary>
public sealed class TempDbStatsCollector : ICollectorDefinition<TempDbStatsCollector.Row>
{
    public static TempDbStatsCollector Instance { get; } = new();

    private TempDbStatsCollector()
    {
    }

    public readonly record struct Row(
        decimal UserObjectReservedMb,
        decimal InternalObjectReservedMb,
        decimal VersionStoreReservedMb,
        decimal TotalReservedMb,
        decimal UnallocatedMb,
        long TotalSessions,
        int TopSessionId,
        decimal TopSessionMb);

    public string Name => "tempdb_stats";

    public string TargetTable => "tempdb_stats";

    public string? WatermarkColumn => null;

    public bool AppliesTo(CollectorTargetInfo target) => true;

    public CollectorQuery BuildQuery(CollectorContext context) => new(QueryText);

    private const string QueryText = @"
SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;

SELECT /* PerformanceMonitorLite */
    user_object_reserved_mb = CONVERT(decimal(18,2), SUM(dsu.user_object_reserved_page_count) * 8 / 1024.0),
    internal_object_reserved_mb = CONVERT(decimal(18,2), SUM(dsu.internal_object_reserved_page_count) * 8 / 1024.0),
    version_store_reserved_mb = CONVERT(decimal(18,2), SUM(dsu.version_store_reserved_page_count) * 8 / 1024.0),
    total_reserved_mb = CONVERT(decimal(18,2), SUM(dsu.user_object_reserved_page_count + dsu.internal_object_reserved_page_count + dsu.version_store_reserved_page_count) * 8 / 1024.0),
    unallocated_mb = CONVERT(decimal(18,2), SUM(dsu.unallocated_extent_page_count) * 8 / 1024.0)
FROM tempdb.sys.dm_db_file_space_usage AS dsu
OPTION(RECOMPILE);

SELECT /* PerformanceMonitorLite */ TOP (1)
    session_id = ssu.session_id,
    tempdb_mb = CONVERT(decimal(18,2), (ssu.user_objects_alloc_page_count + ssu.internal_objects_alloc_page_count) * 8 / 1024.0),
    total_sessions = (SELECT COUNT_BIG(*) FROM sys.dm_db_session_space_usage WHERE user_objects_alloc_page_count + internal_objects_alloc_page_count > 0)
FROM sys.dm_db_session_space_usage AS ssu
ORDER BY (ssu.user_objects_alloc_page_count + ssu.internal_objects_alloc_page_count) DESC
OPTION(RECOMPILE);";

    public IReadOnlyList<CollectorColumn> PayloadColumns { get; } = new[]
    {
        new CollectorColumn("user_object_reserved_mb", CollectorColumnType.Decimal),
        new CollectorColumn("internal_object_reserved_mb", CollectorColumnType.Decimal),
        new CollectorColumn("version_store_reserved_mb", CollectorColumnType.Decimal),
        new CollectorColumn("total_reserved_mb", CollectorColumnType.Decimal),
        new CollectorColumn("unallocated_mb", CollectorColumnType.Decimal),
        new CollectorColumn("total_sessions_using_tempdb", CollectorColumnType.BigInt),
        new CollectorColumn("top_session_id", CollectorColumnType.Integer),
        new CollectorColumn("top_session_tempdb_mb", CollectorColumnType.Decimal),
    };

    public async ValueTask<List<Row>> ReadAsync(DbDataReader reader, CollectorContext context, CancellationToken cancellationToken)
    {
        decimal userObjMb = 0, internalObjMb = 0, versionStoreMb = 0, totalReservedMb = 0, unallocatedMb = 0;
        int topSessionId = 0;
        long totalSessions = 0;
        decimal topSessionMb = 0;

        if (await reader.ReadAsync(cancellationToken))
        {
            userObjMb = reader.IsDBNull(0) ? 0m : reader.GetDecimal(0);
            internalObjMb = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
            versionStoreMb = reader.IsDBNull(2) ? 0m : reader.GetDecimal(2);
            totalReservedMb = reader.IsDBNull(3) ? 0m : reader.GetDecimal(3);
            unallocatedMb = reader.IsDBNull(4) ? 0m : reader.GetDecimal(4);
        }

        if (await reader.NextResultAsync(cancellationToken) && await reader.ReadAsync(cancellationToken))
        {
            topSessionId = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetValue(0));
            topSessionMb = reader.IsDBNull(1) ? 0m : reader.GetDecimal(1);
            totalSessions = reader.IsDBNull(2) ? 0L : reader.GetInt64(2);
        }

        return new List<Row>
        {
            new(userObjMb, internalObjMb, versionStoreMb, totalReservedMb, unallocatedMb, totalSessions, topSessionId, topSessionMb),
        };
    }

    public void WritePayload(Row row, ICollectorRowWriter writer, CollectorContext context)
    {
        writer
            .Value(row.UserObjectReservedMb)      /* user_object_reserved_mb DECIMAL */
            .Value(row.InternalObjectReservedMb)  /* internal_object_reserved_mb DECIMAL */
            .Value(row.VersionStoreReservedMb)    /* version_store_reserved_mb DECIMAL */
            .Value(row.TotalReservedMb)           /* total_reserved_mb DECIMAL */
            .Value(row.UnallocatedMb)             /* unallocated_mb DECIMAL */
            .Value(row.TotalSessions)             /* total_sessions_using_tempdb BIGINT */
            .Value(row.TopSessionId)              /* top_session_id INTEGER */
            .Value(row.TopSessionMb);             /* top_session_tempdb_mb DECIMAL */
    }
}
