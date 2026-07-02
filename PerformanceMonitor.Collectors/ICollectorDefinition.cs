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

    /// <summary>The T-SQL executed against the monitored SQL Server.</summary>
    string Query { get; }

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
