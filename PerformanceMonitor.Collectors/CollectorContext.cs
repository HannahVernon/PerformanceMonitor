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
/// Host-supplied context a collector definition runs under: the server identity and collection
/// timestamp the host stamps on every row, the delta calculator, and host-configured filters.
/// </summary>
public sealed class CollectorContext
{
    private static readonly IReadOnlySet<string> s_emptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public required int ServerId { get; init; }

    public required string ServerName { get; init; }

    /// <summary>UTC timestamp the host stamps as collection_time; definitions pass it to delta calls.</summary>
    public required DateTime CollectionTime { get; init; }

    public required ICollectorDeltaCalculator Deltas { get; init; }

    /// <summary>Target-server facts the definition may branch on (engine edition, etc.).</summary>
    public CollectorTargetInfo Target { get; init; } = new();

    /// <summary>
    /// The most recent already-collected value of the definition's <c>WatermarkColumn</c>, fetched
    /// by the host from ITS store (Lite: DuckDB; Darling: Postgres) before the query is built.
    /// Null when the definition declares no watermark or nothing was collected yet.
    /// </summary>
    public DateTime? Watermark { get; init; }

    /// <summary>Wait types excluded from collection (Lite: ignored_wait_types.json — #1240).</summary>
    public IReadOnlySet<string> IgnoredWaitTypes { get; init; } = s_emptySet;

    /// <summary>Per-server excluded database names (spliced via <see cref="DatabaseExclusionFilter"/>).</summary>
    public IReadOnlyList<string> ExcludedDatabases { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// Host-configured perfmon counter override (Lite: perfmon_counters.json). Null means the
    /// definition's curated default list applies.
    /// </summary>
    public IReadOnlyList<string>? PerfmonCounterOverride { get; init; }

    /// <summary>
    /// Result of the definition's enumeration probe (see
    /// <c>ICollectorDefinition.BuildEnumerationProbe</c>), set by the host between enumeration
    /// and the per-item loop. Null when the definition declares no probe, the probe failed, or
    /// it returned SQL NULL — the definition falls back to its documented default (query_store:
    /// PRODUCTVERSION 13). The only context member the host mutates mid-cycle.
    /// </summary>
    public object? EnumerationProbeResult { get; set; }
}
