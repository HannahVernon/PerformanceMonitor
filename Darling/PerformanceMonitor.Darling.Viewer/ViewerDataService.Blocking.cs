/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Alerting;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Blocking-tab grid row. All alert-consumed members (event time, database, SPID pair,
/// wait/lock, the query pair, contentious object, Source) live on the shared
/// <see cref="BlockedProcessAlertRow"/> base — the same shape Lite's grid row derives from —
/// so the viewer's store reads flow through the shared XE→DMV fallback merge
/// (<see cref="BlockedProcessReportMerge"/>) without a mapping copy. Only display extras live
/// here. event_time is stored naive-UTC (the XE @timestamp is UTC; the DMV snapshot stamps the
/// collector's UTC collection time), so it converts to viewer-local like every collection_time.
/// </summary>
public sealed class ViewerBlockedProcessRow : BlockedProcessAlertRow
{
    /// <summary>The stored naive-UTC event time in the viewer machine's local time.</summary>
    public DateTime? EventTimeLocal
        => EventTime is { } eventTime ? ViewerDataService.ToLocalTime(eventTime) : null;

    /// <summary>Lite's wait-time rendering: sub-second in ms, else one-decimal seconds.</summary>
    public string WaitTimeFormatted => ViewerDataService.FormatWaitTime(WaitTimeMs);

    public string BlockedSqlPreview => ViewerDataService.TruncateForCell(BlockedSqlText);

    public string BlockedSqlTooltip => ViewerDataService.TruncateForTooltip(BlockedSqlText);

    public string BlockingSqlPreview => ViewerDataService.TruncateForCell(BlockingSqlText);

    public string BlockingSqlTooltip => ViewerDataService.TruncateForTooltip(BlockingSqlText);
}

public sealed partial class ViewerDataService
{
    /// <summary>
    /// The XE blocked-process-report read — the alert path's
    /// (<c>DarlingAlertReadAdapter.BlockedProcessReportsSql</c>) query with one deliberate trim:
    /// <c>blocked_process_report_xml</c> is not selected, because nothing in this slice renders
    /// the XML and it can run to hundreds of KB per row. Same WHERE / ORDER BY event_time DESC /
    /// LIMIT 200 semantics. $1 server_id, $2 window start, $3 window end (naive UTC).
    /// </summary>
    public const string BlockedProcessReportsSql = """
        SELECT
            event_time,
            database_name,
            blocked_spid,
            blocking_spid,
            wait_time_ms,
            lock_mode,
            blocked_sql_text,
            blocking_sql_text,
            contentious_object
        FROM blocked_process_reports
        WHERE server_id = $1
        AND   collection_time >= $2
        AND   collection_time <= $3
        ORDER BY event_time DESC
        LIMIT 200
        """;

    /// <summary>
    /// The always-on DMV blocking-snapshot fallback read — the alert path's
    /// (<c>DarlingAlertReadAdapter.DmvBlockingSnapshotsSql</c>) query verbatim
    /// (dmv_blocking_snapshots has no report XML column to trim). Same parameters as
    /// <see cref="BlockedProcessReportsSql"/>.
    /// </summary>
    public const string DmvBlockingSnapshotsSql = """
        SELECT
            event_time,
            database_name,
            blocked_spid,
            blocking_spid,
            wait_time_ms,
            lock_mode,
            blocked_sql_text,
            blocking_sql_text,
            contentious_object
        FROM dmv_blocking_snapshots
        WHERE server_id = $1
        AND   collection_time >= $2
        AND   collection_time <= $3
        ORDER BY event_time DESC
        LIMIT 200
        """;

    /// <summary>
    /// Recent blocked-process events for one server — XE-preferred with the always-on DMV
    /// snapshot as fallback, merged through the shared <see cref="BlockedProcessReportMerge"/>
    /// (keep every XE row; append a DMV row only when no XE row covers the same
    /// blocked/blocker SPID pair within the same minute; re-cap to the 200 newest) — EXACTLY
    /// the alert path's semantics, on the shared row shape.
    /// </summary>
    public async Task<List<ViewerBlockedProcessRow>> GetRecentBlockedProcessReportsAsync(
        int serverId, DateTime startUtc, DateTime endUtc, CancellationToken cancellationToken = default)
    {
        var items = await ReadBlockedProcessRowsAsync(
            BlockedProcessReportsSql, serverId, startUtc, endUtc, BlockedProcessAlertRow.XeReportSource, cancellationToken);
        var dmvItems = await ReadBlockedProcessRowsAsync(
            DmvBlockingSnapshotsSql, serverId, startUtc, endUtc, BlockedProcessAlertRow.DmvSnapshotSource, cancellationToken);

        BlockedProcessReportMerge.AppendDmvFallbackRows(items, dmvItems);

        return items;
    }

    /// <summary>Both blocking reads share one column list, so one reader maps them.</summary>
    private async Task<List<ViewerBlockedProcessRow>> ReadBlockedProcessRowsAsync(
        string sql, int serverId, DateTime startUtc, DateTime endUtc, string source, CancellationToken cancellationToken)
    {
        var rows = new List<ViewerBlockedProcessRow>();

        await using var command = _dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(startUtc, DateTimeKind.Unspecified),
        });
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(endUtc, DateTimeKind.Unspecified),
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ViewerBlockedProcessRow
            {
                EventTime = reader.IsDBNull(0) ? null : reader.GetDateTime(0),
                DatabaseName = reader.IsDBNull(1) ? "" : reader.GetString(1),
                BlockedSpid = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                BlockingSpid = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                WaitTimeMs = reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                LockMode = reader.IsDBNull(5) ? "" : reader.GetString(5),
                BlockedSqlText = reader.IsDBNull(6) ? "" : reader.GetString(6),
                BlockingSqlText = reader.IsDBNull(7) ? "" : reader.GetString(7),
                ContentiousObject = reader.IsDBNull(8) ? "" : reader.GetString(8),
                Source = source,
            });
        }

        return rows;
    }

    /// <summary>Lite's wait-time rendering: &lt; 1000 ms as "N ms", else "N.N sec".</summary>
    public static string FormatWaitTime(long waitTimeMs)
        => waitTimeMs < 1000 ? $"{waitTimeMs} ms" : $"{waitTimeMs / 1000.0:F1} sec";
}
