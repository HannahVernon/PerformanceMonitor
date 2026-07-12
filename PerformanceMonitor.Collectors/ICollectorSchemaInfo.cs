/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// The engine-neutral schema surface of a collector definition — everything a storage host needs
/// to create the destination table and address the standard prefix columns, without knowing the
/// definition's row type. <see cref="ICollectorDefinition{TRow}"/> extends this, so
/// <see cref="CollectorCatalog.All"/> can enumerate every definition heterogeneously (Darling
/// generates its Postgres DDL from exactly this surface).
/// </summary>
public interface ICollectorSchemaInfo
{
    /// <summary>Collector name as used in schedules and collection logs (e.g. "wait_stats").</summary>
    string Name { get; }

    /// <summary>Destination table; hosts prepend their standard prefix columns when writing.</summary>
    string TargetTable { get; }

    /// <summary>
    /// Whether the target table's standard prefix includes an id as its first column
    /// (running_jobs is keyed by (collection_time, server) alone and has none).
    /// </summary>
    bool IncludesCollectionId { get; }

    /// <summary>
    /// Name of the prefix id column in the destination schema. "collection_id" almost everywhere;
    /// the XE tables use "deadlock_id"/"blocked_report_id" and the config snapshots "config_id".
    /// Storage is positional in Lite (DuckDB appender), so these names only matter to hosts that
    /// generate DDL or address columns by name (Darling's Postgres store, which mirrors Lite's
    /// names exactly so analysis SQL can twin without translation).
    /// </summary>
    string PrefixIdColumnName { get; }

    /// <summary>
    /// Name of the prefix timestamp column in the destination schema. "collection_time" almost
    /// everywhere; the config snapshots use "capture_time".
    /// </summary>
    string PrefixTimeColumnName { get; }

    /// <summary>Payload columns in exactly the order the definition emits them.</summary>
    IReadOnlyList<CollectorColumn> PayloadColumns { get; }

    /// <summary>
    /// Whether this collector applies to the target at all — the single authoritative target gate
    /// both SKUs share. e.g. memory_pressure_events returns false for Azure SQL DB (no
    /// <c>sys.dm_os_ring_buffers</c>); the SQL-Agent collectors return false without msdb access.
    /// Hosts skip the cycle entirely (no query, zero rows) when false: Darling's runner calls this
    /// directly, and Lite consults it (via <see cref="CollectorCatalog.AppliesTo(string, CollectorTargetInfo)"/>)
    /// for its pre-dispatch SKIPPED log. Declared here — on the non-generic surface
    /// <see cref="CollectorCatalog.All"/> exposes — so the gate can be evaluated by name without the
    /// row type, keeping the gate CONDITION in one place (each definition's override) rather than
    /// re-encoded per host.
    /// </summary>
    bool AppliesTo(CollectorTargetInfo target);
}
