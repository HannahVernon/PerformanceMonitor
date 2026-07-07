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
using PerformanceMonitor.Common;

namespace PerformanceMonitor.Darling.Service.Mcp;

/// <summary>
/// Service-side reads for the system_health parse-on-read MCP tools (<see cref="DarlingMcpHealthParserTools"/>)
/// — the SAME stored <c>system_health_events</c> the viewer's System Events tab reads
/// (<c>ViewerDataService.SystemEvents</c>), adapted here so the MCP host never references the WPF viewer
/// project. Each read is a STORED read (no live monitored-server hit): the raw <c>event_xml</c> for one XE
/// event type over the window is fetched from <c>v_system_health_events</c>, then the shared
/// <see cref="SystemHealthParser"/> (PerformanceMonitor.Common — Stage 2a, NOT re-implemented here) shreds
/// each blob into the category record, and <see cref="DarlingSystemHealthSignificance"/> (Stage 2b) keeps
/// only the SIGNIFICANT rows. This is exactly the viewer's pipeline; the raw table + the shared parser are
/// the whole thing (no persisted parsed tables).
///
/// <para>
/// The reads window on <c>event_time</c> (the XE <c>@timestamp</c> — the event's real time, which for the
/// ring-buffer categories can lag when it was collected), NOT <c>collection_time</c>, so "last 24 hours"
/// means events that happened in the last 24 hours — the viewer's deliberate choice (the sibling Dashboard
/// windows on collection_time). Bounds bind naive-UTC (Kind=Unspecified → the store's
/// <c>timestamp without time zone</c> columns). Severe-error <c>database_id</c> is resolved to a name from
/// the collected size-stats mapping (the viewer's <see cref="ResolveDatabaseName"/> derivation), since the
/// DB-free shred left it null. Every SQL string is a public const so Darling.Tests can pin the dialect +
/// columns without a live Postgres.
/// </para>
/// </summary>
internal static class DarlingSystemHealthReader
{
    /// <summary>
    /// Raw event_xml for one XE event type over the tab window, newest first — the viewer's
    /// <c>SystemHealthEventsByTypeSql</c>. Windows on <c>event_time</c> (the event's real time), and on the
    /// category's own <c>event_type</c> ($4). $1 server_id, $2/$3 window (naive UTC), $4 event_type.
    /// </summary>
    public const string SystemHealthEventsByTypeSql = """
        SELECT
            event_xml
        FROM v_system_health_events
        WHERE server_id = $1
        AND   event_time >= $2
        AND   event_time <= $3
        AND   event_type = $4
        AND   event_xml IS NOT NULL
        ORDER BY event_time DESC
        """;

    /// <summary>
    /// The server's latest database_id↔database_name mapping — the viewer's <c>DatabaseNameMapSql</c>.
    /// <c>DISTINCT ON (database_id)</c> keeps the most-recently-collected name per id (handles a dropped-and-
    /// recreated id). <c>database_size_stats</c> is the source because it is the only collected table carrying
    /// BOTH database_id and database_name for every online DB. Feeds the Severe Errors DB resolution.
    /// $1 server_id.
    /// </summary>
    public const string DatabaseNameMapSql = """
        SELECT DISTINCT ON (database_id)
            database_id,
            database_name
        FROM v_database_size_stats
        WHERE server_id = $1
        ORDER BY database_id, collection_time DESC
        """;

    /// <summary>Reads the raw event_xml blobs for one XE event type over the window (newest first).</summary>
    public static async Task<List<string>> ReadEventXmlAsync(
        NpgsqlDataSource postgres, int serverId, DateTime startUtc, DateTime endUtc, string eventType, CancellationToken cancellationToken = default)
    {
        var xmls = new List<string>();
        await using var command = postgres.CreateCommand(SystemHealthEventsByTypeSql);
        DarlingMcpReadParameters.AddInt(command, serverId);
        DarlingMcpReadParameters.AddTimestamp(command, startUtc);
        DarlingMcpReadParameters.AddTimestamp(command, endUtc);
        DarlingMcpReadParameters.AddText(command, eventType);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            xmls.Add(reader.GetString(0));
        }

        return xmls;
    }

    /// <summary>Loads the server's latest database_id → database_name map for Severe Errors DB resolution.</summary>
    public static async Task<Dictionary<int, string>> GetDatabaseNameMapAsync(
        NpgsqlDataSource postgres, int serverId, CancellationToken cancellationToken = default)
    {
        var map = new Dictionary<int, string>();
        await using var command = postgres.CreateCommand(DatabaseNameMapSql);
        DarlingMcpReadParameters.AddInt(command, serverId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
                continue;
            map[reader.GetInt32(0)] = reader.GetString(1);
        }

        return map;
    }

    /// <summary>
    /// Resolves a severe-error <c>database_id</c> to a display name using the collected mapping — the viewer's
    /// <c>ResolveDatabaseName</c>. A null or 0 id means "no database context" (error_reported often carries
    /// database_id 0; <c>DB_NAME(0)</c> is NULL server-side too) → empty. A real id absent from the map (a
    /// database dropped before the latest size-stats snapshot, or one never captured) is surfaced as its raw
    /// id rather than silently blanked.
    /// </summary>
    public static string ResolveDatabaseName(int? databaseId, IReadOnlyDictionary<int, string> databaseNameMap)
    {
        if (databaseId is not { } id || id == 0)
            return string.Empty;
        if (databaseNameMap.TryGetValue(id, out var name))
            return name;
        return $"database_id {id}";
    }
}

/// <summary>
/// The service-side twin of the viewer's <c>SystemEventSignificance</c> (Stage 2b of the system_health
/// parity port) — the per-category "significant / warning" predicates the System Events tab applies AFTER
/// <see cref="SystemHealthParser"/> shreds the raw events. The Common parser (Stage 2a) deliberately
/// preserves every field and applies NO WHERE filter; this re-applies sp_HealthParser's per-category
/// filtering at the product's canonical parameters (<c>@warnings_only = 1</c>, <c>@pending_task_threshold =
/// 10</c>) so the MCP tools return the same SIGNIFICANT set the Dashboard's <c>get_health_parser_*</c> tools
/// surface (the Dashboard's server-side collector runs sp_HealthParser with <c>@warnings_only = 1</c>, so its
/// <c>collect.HealthParser_*</c> tables already hold only the warning rows).
///
/// <para>
/// Reproduced service-side (not shared) because the canonical <c>SystemEventSignificance</c> lives in the WPF
/// viewer project, which the headless MCP host must not reference — the same reason slice 4 reproduced the
/// Dashboard/reporting CASE derivations service-side. The predicates + constants are byte-identical to the
/// viewer's (each cites the sp_HealthParser source line via that class). This service exposes seven of the
/// eight parsed categories that need gating; the always-on wait predicate (wait_info / SignificantWaits) has
/// no tool here, and System Health (the corruption/contention counter series) is intentionally UNGATED —
/// its tool plots every snapshot, matching the viewer's <c>GetSystemHealthAsync</c>.
/// </para>
/// </summary>
internal static class DarlingSystemHealthSignificance
{
    /// <summary>sp_HealthParser severe-error cutoff under <c>@warnings_only = 1</c>: severity &gt;= 19.</summary>
    public const int SevereErrorMinSeverity = 19;

    /// <summary>sp_HealthParser <c>@pending_task_threshold</c> default (10): the minimum pendingTasks for a
    /// significant CPU-task warning.</summary>
    public const long CpuTaskMinPendingTasks = 10;

    /// <summary>The scheduler / component <c>state</c>/<c>status</c> text kept under warnings_only.</summary>
    public const string WarningStatus = "WARNING";

    /// <summary>The low-physical-memory notification kept under warnings_only for MemoryConditions / MemoryBroker.</summary>
    public const string LowMemoryNotification = "RESOURCE_MEMPHYSICAL_LOW";

    /// <summary>error_reported numbers sp_HealthParser always excludes — 17830 (network TDS/connection-reset)
    /// and 18056 (benign "connection was reset by the client" pool churn): severity 20 but not actionable.</summary>
    public static readonly IReadOnlySet<int> IgnoredSevereErrorNumbers = new HashSet<int> { 17830, 18056 };

    /// <summary>A scheduler-monitor row is significant when its status is WARNING (a non-yielding or offline
    /// scheduler); routine heartbeat rows are dropped.</summary>
    public static bool IsSignificant(SchedulerIssueRecord record) =>
        string.Equals(record.Status, WarningStatus, StringComparison.Ordinal);

    /// <summary>An error_reported row is significant when severity &gt;= 19 and the error number is not one of
    /// the benign connection-reset numbers. A missing severity drops the row; a missing error number keeps it.</summary>
    public static bool IsSignificant(SevereErrorRecord record) =>
        record.Severity >= SevereErrorMinSeverity &&
        (record.ErrorNumber is not { } errorNumber || !IgnoredSevereErrorNumbers.Contains(errorNumber));

    /// <summary>A memory-conditions snapshot is significant when its lastNotification is RESOURCE_MEMPHYSICAL_LOW.</summary>
    public static bool IsSignificant(MemoryConditionsRecord record) =>
        string.Equals(record.LastNotification, LowMemoryNotification, StringComparison.Ordinal);

    /// <summary>A memory-broker ratio-change row is significant when its notification is RESOURCE_MEMPHYSICAL_LOW.</summary>
    public static bool IsSignificant(MemoryBrokerRecord record) =>
        string.Equals(record.Notification, LowMemoryNotification, StringComparison.Ordinal);

    /// <summary>Every recorded memory-node OOM is significant — sp_HealthParser applies NO WHERE filter to
    /// this category (its CROSS APPLY has no predicate). Present for symmetry so the read filters uniformly.</summary>
    public static bool IsSignificant(MemoryNodeOomRecord record) => true;

    /// <summary>A CPU-task-details row is significant when the QUERY_PROCESSING component state is WARNING and
    /// its pendingTasks is at least the threshold. A missing state or a below-threshold (incl. null) pendingTasks
    /// drops the row.</summary>
    public static bool IsSignificant(CpuTasksRecord record) =>
        string.Equals(record.State, WarningStatus, StringComparison.Ordinal) &&
        record.PendingTasks >= CpuTaskMinPendingTasks;

    /// <summary>A potential-IO-issue row is significant when the IO_SUBSYSTEM component state is WARNING. (The
    /// "has a pending request with a duration" predicate is applied upstream in
    /// <see cref="SystemHealthParser.ParseIoIssues"/>, which emits no record for an event without one.)</summary>
    public static bool IsSignificant(IoIssuesRecord record) =>
        string.Equals(record.State, WarningStatus, StringComparison.Ordinal);
}
