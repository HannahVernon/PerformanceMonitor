/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Diagnostics;
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
/// </summary>
public static class DarlingRetention
{
    /// <summary>
    /// collection_log isn't a collector, so it has no <see cref="CollectorScheduleDefaults"/>
    /// entry to carry its horizon — the constant lives here for now, until observability
    /// retention grows a shared home. 30 days matches the dominant collector horizon.
    /// </summary>
    private const int CollectionLogRetentionDays = 30;

    /* The first purge against a long backlog can delete a lot of rows; Npgsql's default
       30-second command timeout would roll that DELETE back and the table would never catch up
       (retried tomorrow with a day MORE to delete). Daily steady-state purges are far smaller. */
    private const int DeleteTimeoutSeconds = 300;

    private const string CollectionLogDeleteSql = "DELETE FROM collection_log WHERE collection_time < $1";

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
    public static async Task<int> PurgeAsync(NpgsqlDataSource postgres, bool timescaleAvailable, ILogger? logger, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var tablesPurged = 0;
        var totalRowsDeleted = 0;
        var totalChunksDropped = 0;

        /* Naive-UTC storage: Npgsql 6+ rejects Kind=Utc against `timestamp` — see PgCollectorRowWriter. */
        var utcNow = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);

        foreach (var definition in CollectorCatalog.All)
        {
            if (!CollectorScheduleDefaults.All.TryGetValue(definition.Name, out var schedule))
            {
                /* Impossible while the retention coverage test holds — belt-and-suspenders so
                   schedule drift degrades to a loud warning instead of killing the sweep. */
                logger?.LogWarning("Retention purge: no schedule entry for '{Collector}' — {Table} was not purged",
                    definition.Name, definition.TargetTable);
                continue;
            }

            if (timescaleAvailable)
            {
                var dropped = await DropChunksOneAsync(
                    postgres, definition.TargetTable, DropChunksSqlFor(definition, schedule.RetentionDays),
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
                utcNow.AddDays(-schedule.RetentionDays), logger, cancellationToken);
            if (deleted is not null)
            {
                tablesPurged++;
                totalRowsDeleted += deleted.Value;
            }
        }

        var logDeleted = await PurgeOneAsync(
            postgres, "collection_log", CollectionLogDeleteSql,
            utcNow.AddDays(-CollectionLogRetentionDays), logger, cancellationToken);
        if (logDeleted is not null)
        {
            tablesPurged++;
            totalRowsDeleted += logDeleted.Value;
        }

        logger?.LogInformation("Retention purge: {Tables} table(s) purged, {Rows} row(s) deleted, {Chunks} chunk(s) dropped, {ElapsedMs}ms",
            tablesPurged, totalRowsDeleted, totalChunksDropped, sw.ElapsedMilliseconds);
        return totalRowsDeleted + totalChunksDropped;
    }

    /// <summary>
    /// The purge statement for one collector table — DELETE everything older than the cutoff on
    /// the definition's own prefix time column ("collection_time" almost everywhere; the config
    /// snapshots purge on "capture_time"). Table and column names come from the shared catalog
    /// constants, never from user input, so interpolation is safe here — the same reasoning as
    /// the runner's watermark read (DarlingCollectorRunner.GetLastCollectedTimeAsync).
    /// </summary>
    internal static string DeleteSqlFor(ICollectorSchemaInfo schema)
        => $"DELETE FROM {schema.TargetTable} WHERE {schema.PrefixTimeColumnName} < $1";

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

    /// <summary>One table's DELETE; null when it failed (warned, sweep continues).</summary>
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
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* Failure-isolated per table — one stuck DELETE must not stop the sweep. */
            logger?.LogWarning("Retention purge failed for {Table}: {Message}", tableName, ex.Message);
            return null;
        }
    }
}
