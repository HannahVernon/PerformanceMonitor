/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Npgsql;
using PerformanceMonitor.Collectors;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// Daily retention purge for the Darling Postgres store. The extension-free baseline is
/// DELETE-based and works on any Postgres; when the worker detected TimescaleDB
/// (<c>timescaleAvailable</c> — see TimescaleSupport in Darling.Storage) the collector tables
/// purge via hypertable <c>drop_chunks</c> instead, which detaches whole expired chunks in O(1)
/// instead of scanning rows. collection_log stays DELETE-based either way (never converted — a
/// registry-side table, see the TimescaleSupport scope remarks), as do the analysis tables
/// (PgFindingStore.CleanupOldFindingsAsync owns those). Retention horizons are the shared
/// per-collector <see cref="CollectorScheduleDefaults"/> (identity-pinned to Lite's
/// ScheduleManager table), so both SKUs keep the same data horizons out of the box. NOTE: Lite
/// archives expired rows to parquet before deleting (ArchiveService); Darling deliberately
/// purges without archiving — with Timescale, the compression policy on old chunks IS the
/// archival tier (compressed chunks stay queryable), and the plain-PG story remains
/// purge-without-archive for now.
/// <para>Every sweep writes one AUDITABLE run-record to collection_log under a fleet-sentinel server_id
/// (<see cref="DarlingObservability.FleetServerId"/>, never a real server) — SUCCESS, WARNING (some
/// tables failed their statement, already logged + isolated), or ERROR — so a stalled or partial purge is
/// visible in the collection log, not just the service log. The plain-PG DELETE path drains each table in
/// <see cref="DeleteBatchSize"/>-row batches (the Dashboard's DELETE TOP idiom); collection_log is kept 2x
/// the base data-retention window so a run-record outlives the metric rows it explains.</para>
/// </summary>
public static class DarlingRetention
{
    /// <summary>
    /// The base data-retention window the collection_log horizon is a multiple of. 30 days matches the
    /// dominant collector <see cref="CollectorScheduleDefaults"/> horizon and the Dashboard's
    /// <c>@effective_retention_days</c> default (config.data_retention).
    /// </summary>
    internal const int DataRetentionBaseDays = 30;

    /// <summary>
    /// collection_log isn't a collector, so it has no <see cref="CollectorScheduleDefaults"/> entry to carry
    /// its horizon. It is kept at 2x the base window (mirrors the Dashboard's <c>retention_date x2</c> rule)
    /// so a collector run-record survives long enough to diagnose WHY a collector failed AFTER its metric
    /// rows have aged out — a 30-day metric row and its failure log would otherwise expire together, erasing
    /// the evidence. Effectively 60 days.
    /// </summary>
    internal const int CollectionLogRetentionDays = DataRetentionBaseDays * 2;

    /* The per-DELETE batch cap — the Postgres twin of the Dashboard's DELETE TOP(10000) idiom
       (config.data_retention). A large first purge is drained in 10k-row batches instead of one giant
       DELETE, bounding lock duration, WAL generation, and dead-tuple bloat. Steady-state daily purges are
       far smaller and usually clear in a single batch. */
    private const int DeleteBatchSize = 10000;

    /* Each 10k-row batch is small and fast; the generous 300s per-batch command timeout (well above
       Npgsql's 30s default) is belt-and-suspenders for a slow disk. Batching is also what keeps a large
       first purge from ever hitting a timeout at all — the pre-batch single DELETE could roll back on a long
       backlog and never catch up (retried tomorrow with a day MORE to delete). */
    private const int DeleteTimeoutSeconds = 300;

    /// <summary>
    /// Purges every collector table past its shared <see cref="CollectorScheduleDefaults"/>
    /// RetentionDays, plus collection_log past <see cref="CollectionLogRetentionDays"/>.
    /// When <paramref name="timescaleAvailable"/> (the worker's startup detection), the
    /// collector tables purge via <c>drop_chunks</c> (<see cref="DropChunksSqlFor"/>) with a
    /// per-table DELETE fallback so a table that failed hypertable conversion still honors its
    /// horizon; when false, the extension-free DELETE path runs unchanged. collection_log is
    /// DELETE-based either way. Failure-isolated per table: one failed statement is logged as a
    /// warning and the sweep continues (that table is retried on the next purge). Safe on a
    /// fresh/empty store — a purge that matches nothing removes nothing. Returns a coarse
    /// activity count: rows deleted by the DELETE paths plus whole chunks dropped by
    /// drop_chunks (Timescale doesn't report per-row counts for dropped chunks).
    /// </summary>
    /// <param name="retentionDaysFor">
    /// Optional resolver for a collector's effective retention horizon (control-plane fleet-wide overrides
    /// layered on <see cref="CollectorScheduleDefaults"/>). Null (or a value it does not override) uses the
    /// shared default. A per-server override cannot apply here — the purge is per shared table, not per server.
    /// The on-demand <c>purge_now</c> command passes a <c>_ =&gt; customDays</c> resolver for its custom-N mode.
    /// </param>
    /// <returns>
    /// A <see cref="PurgeSummary"/>: how many tables were touched and the coarse activity count (DELETE rows
    /// plus dropped chunks). The daily caller discards it; the on-demand <c>purge_now</c> command reports it.
    /// </returns>
    public static async Task<PurgeSummary> PurgeAsync(
        NpgsqlDataSource postgres, bool timescaleAvailable, ILogger? logger, CancellationToken cancellationToken,
        Func<string, int>? retentionDaysFor = null)
    {
        var sw = Stopwatch.StartNew();
        var tablesPurged = 0;
        var totalRowsDeleted = 0;
        var totalChunksDropped = 0;
        var tablesFailed = 0;

        /* Naive-UTC storage: Npgsql 6+ rejects Kind=Utc against `timestamp` — see PgCollectorRowWriter. */
        var utcNow = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

        try
        {
            foreach (var definition in CollectorCatalog.All)
            {
                if (!CollectorScheduleDefaults.All.TryGetValue(definition.Name, out var schedule))
                {
                    /* Impossible while the retention coverage test holds — belt-and-suspenders so schedule
                       drift degrades to a loud warning (and a WARNING run-record) instead of killing the sweep. */
                    logger?.LogWarning("Retention purge: no schedule entry for '{Collector}' — {Table} was not purged",
                        definition.Name, definition.TargetTable);
                    tablesFailed++;
                    continue;
                }

                /* Clamp at the destructive sink (belt-and-suspenders with the resolver + the V17 CHECK): a
                   retention of 0/negative would flip the cutoff into the present/future and drop_chunks /
                   DELETE the entire table. Never purge with a horizon under 1 day. */
                var retentionDays = Math.Max(1, retentionDaysFor?.Invoke(definition.Name) ?? schedule.RetentionDays);

                if (timescaleAvailable)
                {
                    var dropped = await DropChunksOneAsync(
                        postgres, definition.TargetTable, DropChunksSqlFor(definition, retentionDays),
                        logger, cancellationToken);
                    if (dropped is not null)
                    {
                        tablesPurged++;
                        totalChunksDropped += dropped.Value;
                        continue;
                    }

                    /* drop_chunks failed (warned) — most likely this one table failed hypertable
                       conversion and is still plain. Fall back to the extension-free DELETE so the
                       table still honors its horizon instead of growing unbounded. */
                }

                var deleted = await PurgeOneAsync(
                    postgres, definition.TargetTable, DeleteSqlFor(definition),
                    utcNow.AddDays(-retentionDays), logger, cancellationToken);
                if (deleted is not null)
                {
                    tablesPurged++;
                    totalRowsDeleted += deleted.Value;
                }
                else
                {
                    tablesFailed++;
                }
            }

            var logDeleted = await PurgeOneAsync(
                postgres, "collection_log", BatchedDeleteSql("collection_log", "collection_time"),
                utcNow.AddDays(-CollectionLogRetentionDays), logger, cancellationToken);
            if (logDeleted is not null)
            {
                tablesPurged++;
                totalRowsDeleted += logDeleted.Value;
            }
            else
            {
                tablesFailed++;
            }

            var summary = new PurgeSummary(tablesPurged, totalRowsDeleted, totalChunksDropped);
            logger?.LogInformation(
                "Retention purge: {Tables} table(s) purged, {Rows} row(s) deleted, {Chunks} chunk(s) dropped, {Failed} failed, {ElapsedMs}ms",
                tablesPurged, totalRowsDeleted, totalChunksDropped, tablesFailed, sw.ElapsedMilliseconds);

            /* Auditable run-record: a clean sweep writes SUCCESS, a sweep where one or more tables failed
               their statement writes WARNING (the per-table failures were already logged + isolated above).
               Fleet-wide, so it lands under the sentinel server_id (DarlingObservability.LogRetentionRunAsync),
               which is failure-isolated and never breaks the loop. */
            var status = tablesFailed == 0 ? "SUCCESS" : "WARNING";
            var message = tablesFailed == 0
                ? $"Purged {tablesPurged.ToString(CultureInfo.InvariantCulture)} table(s): {totalRowsDeleted.ToString(CultureInfo.InvariantCulture)} row(s) deleted, {totalChunksDropped.ToString(CultureInfo.InvariantCulture)} chunk(s) dropped"
                : $"Purged {tablesPurged.ToString(CultureInfo.InvariantCulture)} table(s), {tablesFailed.ToString(CultureInfo.InvariantCulture)} failed (see prior warnings): {totalRowsDeleted.ToString(CultureInfo.InvariantCulture)} row(s) deleted, {totalChunksDropped.ToString(CultureInfo.InvariantCulture)} chunk(s) dropped";
            await DarlingObservability.LogRetentionRunAsync(
                postgres, status, summary.TotalPurged, sw.ElapsedMilliseconds, message, logger, cancellationToken);

            return summary;
        }
        catch (OperationCanceledException)
        {
            /* Shutdown/cancellation — propagate exactly like the per-table helpers (no ERROR run-record; the
               purge simply didn't finish and retries on the next daily tick). */
            throw;
        }
        catch (Exception ex)
        {
            /* The per-table helpers isolate their own failures, so reaching here means something unexpected
               escaped the loop. Record an ERROR run-record for the audit trail and return what we managed to
               purge — PurgeAsync must never throw at the daily caller (it is not wrapped there), so a broken
               purge surfaces as an auditable ERROR row, not a crashed collection loop. */
            logger?.LogError("Retention purge failed: {Message}", ex.Message);
            await DarlingObservability.LogRetentionRunAsync(
                postgres, "ERROR", totalRowsDeleted + totalChunksDropped, sw.ElapsedMilliseconds, ex.Message, logger, cancellationToken);
            return new PurgeSummary(tablesPurged, totalRowsDeleted, totalChunksDropped);
        }
    }

    /// <summary>
    /// The batched purge statement for one collector table — DELETE up to <see cref="DeleteBatchSize"/> rows
    /// older than the cutoff on the definition's own prefix time column ("collection_time" almost everywhere;
    /// the config snapshots purge on "capture_time"), executed in a loop until the table is drained
    /// (<see cref="PurgeOneAsync"/>). Table and column names come from the shared catalog constants, never
    /// from user input, so interpolation is safe here — the same reasoning as the runner's watermark read
    /// (DarlingCollectorRunner.GetLastCollectedTimeAsync).
    /// </summary>
    internal static string DeleteSqlFor(ICollectorSchemaInfo schema)
        => BatchedDeleteSql(schema.TargetTable, schema.PrefixTimeColumnName);

    /// <summary>
    /// The Postgres twin of the Dashboard's <c>DELETE TOP(10000)</c> batched purge: delete up to
    /// <see cref="DeleteBatchSize"/> expired rows per statement via a <c>ctid IN (SELECT … LIMIT N)</c>
    /// subquery (Postgres has no <c>DELETE … LIMIT</c>). The time predicate is repeated in the OUTER delete —
    /// <c>WHERE {col} &lt; $1 AND ctid IN (…)</c> — deliberately: on a plain table it is merely redundant, but
    /// on a TimescaleDB hypertable a ctid is unique only WITHIN a chunk, so a bare <c>ctid IN (…)</c> could
    /// match a same-ctid row in a DIFFERENT chunk; the outer <c>{col} &lt; $1</c> guarantees only
    /// genuinely-expired rows are ever deleted, so a colliding fresh row is always safe. In production this
    /// path only ever runs on plain tables anyway (hypertables purge via <c>drop_chunks</c>; collection_log is
    /// never a hypertable), but the guard keeps the statement correct even against a store where a collector
    /// table happens to be a hypertable. <c>$1</c> is bound once and referenced by both positions.
    /// Table/column come from catalog constants (never user input), so interpolation is safe.
    /// </summary>
    internal static string BatchedDeleteSql(string table, string timeColumn)
        => $"DELETE FROM {table} WHERE {timeColumn} < $1 AND ctid IN (SELECT ctid FROM {table} WHERE {timeColumn} < $1 LIMIT {DeleteBatchSize})";

    /// <summary>
    /// The Timescale purge statement for one collector table — <c>drop_chunks</c> detaches every
    /// chunk wholly older than the horizon (validated live on TimescaleDB 2.28.1; the partition
    /// column is implicit in the hypertable's dimension, so no time column appears here). An
    /// accepted coarseness: drop_chunks only drops WHOLE chunks, so rows inside a
    /// partially-expired chunk survive until the entire chunk ages past the horizon (with the
    /// default 7-day chunk interval, up to ~7 days of grace) — the trade for a metadata-only
    /// purge that never scans or rewrites rows. RetentionDays comes from the shared
    /// <see cref="CollectorScheduleDefaults"/> constants, never from user input, so
    /// interpolation is safe here — the same reasoning as <see cref="DeleteSqlFor"/>.
    /// </summary>
    internal static string DropChunksSqlFor(ICollectorSchemaInfo schema, int retentionDays)
        => $"SELECT drop_chunks('{schema.TargetTable}', older_than => make_interval(days => {retentionDays}))";

    /// <summary>
    /// One table's drop_chunks; returns the number of chunks dropped, or null when it failed
    /// (warned; the caller falls back to DELETE for that table). drop_chunks returns one row per
    /// dropped chunk, so the count comes from reading the result set.
    /// </summary>
    private static async Task<int?> DropChunksOneAsync(
        NpgsqlDataSource postgres,
        string tableName,
        string dropChunksSql,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(dropChunksSql, connection) { CommandTimeout = DeleteTimeoutSeconds };

            var chunksDropped = 0;
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                chunksDropped++;
            }

            return chunksDropped;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* Failure-isolated per table — warned here, then the caller's DELETE fallback runs. */
            logger?.LogWarning("Retention purge (drop_chunks) failed for {Table} — falling back to DELETE: {Message}",
                tableName, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// One table's batched DELETE: re-executes <paramref name="deleteSql"/> (a <see cref="BatchedDeleteSql"/>
    /// statement capped at <see cref="DeleteBatchSize"/> rows) until a batch clears fewer rows than the cap —
    /// i.e. the table is drained. Returns the total rows deleted across all batches, or null when it failed
    /// (warned, sweep continues). Batching bounds lock/WAL/dead-tuple growth on a large first purge; a small
    /// steady-state purge finishes in one batch. The connection and command (with its single bound cutoff
    /// parameter) are reused across the loop.
    /// </summary>
    private static async Task<int?> PurgeOneAsync(
        NpgsqlDataSource postgres,
        string tableName,
        string deleteSql,
        DateTime cutoff,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await postgres.OpenConnectionAsync(cancellationToken);
            using var command = new NpgsqlCommand(deleteSql, connection) { CommandTimeout = DeleteTimeoutSeconds };
            command.Parameters.AddWithValue(cutoff);

            var totalDeleted = 0;
            while (true)
            {
                var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
                totalDeleted += deleted;

                /* A batch that clears fewer than the cap means no expired rows remain — the table is drained.
                   A full-cap batch means there may be more, so go again. */
                if (deleted < DeleteBatchSize)
                {
                    break;
                }
            }

            return totalDeleted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* Failure-isolated per table — one stuck DELETE must not stop the sweep. */
            logger?.LogWarning("Retention purge failed for {Table}: {Message}", tableName, ex.Message);
            return null;
        }
    }
}

/// <summary>
/// The outcome of one <see cref="DarlingRetention.PurgeAsync"/> sweep: how many tables were touched
/// (<paramref name="TablesPurged"/>) and the coarse activity count split into DELETE rows
/// (<paramref name="RowsDeleted"/>) and dropped Timescale chunks (<paramref name="ChunksDropped"/> —
/// drop_chunks doesn't report per-row counts). <see cref="TotalPurged"/> is the single headline number the
/// daily log and the on-demand <c>purge_now</c> result report.
/// </summary>
public readonly record struct PurgeSummary(int TablesPurged, int RowsDeleted, int ChunksDropped)
{
    /// <summary>Rows deleted plus whole chunks dropped — the coarse "how much did this purge remove" count.</summary>
    public int TotalPurged => RowsDeleted + ChunksDropped;
}
