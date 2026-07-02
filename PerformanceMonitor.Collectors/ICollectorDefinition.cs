/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// A collector definition is the shared, engine-neutral "monitoring brain" for one collector:
/// the T-SQL sent to the monitored server, the result-row mapping, the delta rules, and the
/// payload column order. Both SKUs (portable Lite writing DuckDB, Darling writing Postgres)
/// run the SAME definition, so a change lands once and the compiler forces every host to keep up
/// — this is what makes behavioral parity structural rather than manual (headless plan v5.1).
/// Definitions are stateless and thread-safe; per-cycle state rides in <see cref="CollectorContext"/>.
/// </summary>
/// <typeparam name="TRow">The definition's materialized result-row shape.</typeparam>
public interface ICollectorDefinition<TRow>
{
    /// <summary>Collector name as used in schedules and collection logs (e.g. "wait_stats").</summary>
    string Name { get; }

    /// <summary>Destination table; hosts prepend their standard prefix columns when writing.</summary>
    string TargetTable { get; }

    /// <summary>
    /// Whether this collector applies to the target at all — e.g. memory_pressure_events returns
    /// false for Azure SQL DB because sys.dm_os_ring_buffers is not exposed there. Hosts skip the
    /// cycle entirely (no query, zero rows) when false.
    /// </summary>
    bool AppliesTo(CollectorTargetInfo target);

    /// <summary>
    /// True when the query must run once per database with a per-database connection (Azure SQL
    /// DB scopes some DMVs to the connected database — e.g. dm_io_virtual_file_stats). The host
    /// enumerates databases, opens each connection, and calls <see cref="ReadAsync"/> per reader,
    /// aggregating rows; a database that errors is skipped and logged, matching the original
    /// collectors.
    /// </summary>
    bool RunsPerDatabase(CollectorTargetInfo target);

    /// <summary>
    /// Time column the host should read its latest already-collected value of (from the host's
    /// own store) before building the query — exposed to the definition as
    /// <see cref="CollectorContext.Watermark"/> for server-side filters and client-side dedup.
    /// Null when the collector needs no watermark (the common case).
    /// </summary>
    string? WatermarkColumn { get; }

    /// <summary>
    /// Builds the T-SQL (and any bound parameters) for this cycle. Constant for most collectors;
    /// target-aware definitions branch on <see cref="CollectorContext.Target"/> and
    /// <see cref="CollectorContext.Watermark"/>.
    /// </summary>
    CollectorQuery BuildQuery(CollectorContext context);

    /// <summary>
    /// Optional second query run best-effort on the same (single-path) connection after
    /// <see cref="ReadAsync"/> — e.g. server_properties' WS5 health probe. Null = none (the
    /// common case). The host isolates its failure: any exception is logged at debug and the
    /// cycle proceeds with the primary rows unchanged, so a supplemental can never fail the
    /// collector. Not executed for per-database collectors.
    /// </summary>
    CollectorQuery? BuildSupplementalQuery(CollectorContext context);

    /// <summary>Merges the supplemental reader's data into the already-read rows.</summary>
    ValueTask ApplySupplementalAsync(List<TRow> rows, DbDataReader reader, CollectorContext context, CancellationToken cancellationToken);

    /// <summary>Payload columns in exactly the order <see cref="WritePayload"/> emits them.</summary>
    IReadOnlyList<CollectorColumn> PayloadColumns { get; }

    /// <summary>
    /// Materializes result rows from the query's reader, applying any definition-owned filtering.
    /// Runs entirely in the SQL phase so hosts can time SQL and storage phases separately.
    /// </summary>
    ValueTask<List<TRow>> ReadAsync(DbDataReader reader, CollectorContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Emits one row's payload through the writer in <see cref="PayloadColumns"/> order,
    /// computing any deltas via <see cref="CollectorContext.Deltas"/>.
    /// </summary>
    void WritePayload(TRow row, ICollectorRowWriter writer, CollectorContext context);
}
