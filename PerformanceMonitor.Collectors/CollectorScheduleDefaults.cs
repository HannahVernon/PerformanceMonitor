/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// The default per-collector cadence and retention — the shared source both SKUs schedule by,
/// so portable Lite and the Darling service collect on identical rhythms out of the box. Lite's
/// ScheduleManager carries the same table (user-editable per install) and an identity-pin test
/// asserts the two cannot drift; Darling consumes this directly (no schedule knobs until someone
/// needs them — defaults over speculative config). FrequencyMinutes 0 = collect once on server
/// load only (config snapshots).
/// </summary>
public static class CollectorScheduleDefaults
{
    public sealed record Entry(int FrequencyMinutes, int RetentionDays);

    public static IReadOnlyDictionary<string, Entry> All { get; } = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
    {
        ["wait_stats"] = new(1, 30),
        ["query_stats"] = new(1, 30),
        ["procedure_stats"] = new(1, 30),
        ["query_store"] = new(5, 30),
        ["query_snapshots"] = new(1, 7),
        ["cpu_utilization"] = new(1, 30),
        ["file_io_stats"] = new(1, 30),
        ["memory_stats"] = new(1, 30),
        ["memory_clerks"] = new(5, 30),
        ["memory_pressure_events"] = new(5, 30),
        ["tempdb_stats"] = new(1, 30),
        ["perfmon_stats"] = new(1, 30),
        ["deadlocks"] = new(1, 30),
        ["server_config"] = new(0, 30),
        ["database_config"] = new(0, 30),
        ["memory_grant_stats"] = new(1, 30),
        ["waiting_tasks"] = new(1, 7),
        ["dmv_blocking_snapshot"] = new(1, 30),
        ["blocked_process_report"] = new(1, 30),
        ["database_scoped_config"] = new(0, 30),
        ["trace_flags"] = new(0, 30),
        ["running_jobs"] = new(5, 7),
        ["database_size_stats"] = new(60, 90),
        ["index_object_stats"] = new(1440, 90),
        ["server_properties"] = new(0, 365),
        ["session_stats"] = new(5, 30),
    };
}
