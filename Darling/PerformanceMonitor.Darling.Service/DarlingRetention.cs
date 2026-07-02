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
/// Daily retention purge for the Darling Postgres store. DELETE-based on purpose: it is the
/// extension-free baseline that works on any Postgres. TimescaleDB's hypertable
/// <c>drop_chunks</c> is the planned upgrade path once the hypertable migration lands
/// (see the PgMigrations remarks), and this class is where that extension-present branch will
/// live. Retention horizons are the shared per-collector
/// <see cref="CollectorScheduleDefaults"/> (identity-pinned to Lite's ScheduleManager table),
/// so both SKUs keep the same data horizons out of the box. NOTE: Lite archives expired rows to
/// parquet before deleting (ArchiveService); Darling deliberately purges without archiving for
/// now — archival for the centralized store is a future milestone decision.
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
    /// Failure-isolated per table: one failed DELETE is logged as a warning and the sweep
    /// continues (that table is retried on the next purge). Safe on a fresh/empty store — a
    /// DELETE that matches nothing deletes nothing. Returns the total rows deleted.
    /// </summary>
    public static async Task<int> PurgeAsync(NpgsqlDataSource postgres, ILogger? logger, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var tablesPurged = 0;
        var totalRowsDeleted = 0;

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

        logger?.LogInformation("Retention purge: {Tables} table(s) purged, {Rows} row(s) deleted, {ElapsedMs}ms",
            tablesPurged, totalRowsDeleted, sw.ElapsedMilliseconds);
        return totalRowsDeleted;
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
