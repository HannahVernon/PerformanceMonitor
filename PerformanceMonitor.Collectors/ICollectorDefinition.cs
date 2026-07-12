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
public interface ICollectorDefinition<TRow> : ICollectorSchemaInfo
{
    /// <summary>
    /// Per-collector command timeout override in seconds, for collectors whose sweep is far
    /// heavier than the default budget (index_object_stats: 300 s per database — the #1135 fix).
    /// Null = the host's default timeout. Applies to the main/per-item/per-database commands;
    /// enumeration queries always use the host default, matching the originals.
    /// </summary>
    int? CommandTimeoutSecondsOverride { get; }

    /* AppliesTo(CollectorTargetInfo) is declared on the base ICollectorSchemaInfo — the single
       authoritative target gate, evaluable by name off CollectorCatalog.All without the row type. */

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
    /// Numeric (bigint) column the host should read its latest already-collected value of (from the
    /// host's own store) before building the query — exposed to the definition as
    /// <see cref="CollectorContext.NumericWatermark"/> for server-side filters and client-side dedup
    /// on a monotonic identity/sequence column (job_history's <c>instance_id</c>). The bigint twin of
    /// <see cref="WatermarkColumn"/>. Null when the collector needs no numeric watermark (the common
    /// case — every existing collector).
    /// </summary>
    string? NumericWatermarkColumn { get; }

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

    /// <summary>
    /// Optional enumeration shape (the "[db].sys.sp_executesql" idiom): when non-null, the host
    /// runs this query first (single string column, e.g. database names), then executes
    /// <see cref="BuildPerItemQuery"/> once per item ON THE SAME CONNECTION, feeding each reader
    /// to <see cref="ReadItemAsync"/>. An item whose query fails with a SqlException is skipped
    /// with a warning, matching the original collectors. Zero items short-circuits the cycle.
    /// <see cref="ReadAsync"/> is not called for enumerating collectors.
    /// </summary>
    CollectorQuery? BuildEnumerationQuery(CollectorContext context);

    /// <summary>
    /// Optional quick scalar probe on the enumeration path, run once after items are listed
    /// (only when at least one item exists) and before the per-item loop — e.g. query_store's
    /// live PRODUCTVERSION check that decides its version-gated columns (deliberately probed
    /// per cycle rather than trusting cached connection status, which can be version-unknown).
    /// The host runs it best-effort with a short timeout and exposes the scalar as
    /// <see cref="CollectorContext.EnumerationProbeResult"/>; on any failure the result stays
    /// null and the definition uses its documented default. Null = no probe (the common case).
    /// </summary>
    CollectorQuery? BuildEnumerationProbe(CollectorContext context);

    /// <summary>Builds the per-item query for one enumerated item (e.g. one database).</summary>
    CollectorQuery BuildPerItemQuery(string item, CollectorContext context);

    /// <summary>Reads one enumerated item's result rows, appending to the shared accumulator.</summary>
    ValueTask ReadItemAsync(string item, DbDataReader reader, List<TRow> rows, CollectorContext context, CancellationToken cancellationToken);

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
