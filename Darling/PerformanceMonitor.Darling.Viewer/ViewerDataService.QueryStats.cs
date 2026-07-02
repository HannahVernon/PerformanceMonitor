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

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// One Queries-tab grid row: a (database, query_hash) group's summed deltas over the window,
/// with the latest captured query text. Numeric members stay in the store's units (microseconds
/// for CPU/duration); the display properties convert, mirroring Lite's grid (Total CPU (ms) /
/// Total Duration (ms)). <see cref="LastExecutionTime"/> is the SQL SERVER's local wall clock
/// (dm_exec_query_stats), NOT UTC — it is shown raw under a "(server)" header rather than run
/// through the naive-UTC-to-local conversion the collection_time columns get.
/// </summary>
public sealed record QueryStatsGridRow(
    string DatabaseName,
    string QueryHash,
    string QueryText,
    long Executions,
    long TotalCpuUs,
    long TotalElapsedUs,
    long TotalLogicalReads,
    DateTime? LastExecutionTime)
{
    public double TotalCpuMs => TotalCpuUs / 1000.0;

    public double TotalElapsedMs => TotalElapsedUs / 1000.0;

    /// <summary>Single-line, capped preview for the grid cell.</summary>
    public string QueryTextPreview => ViewerDataService.TruncateForCell(QueryText);

    /// <summary>Longer capped text for the cell tooltip.</summary>
    public string QueryTextTooltip => ViewerDataService.TruncateForTooltip(QueryText);
}

public sealed partial class ViewerDataService
{
    /// <summary>
    /// The Queries tab read — Lite's <c>GetTopQueriesByCpuAsync</c> top-N shape ported to
    /// Postgres and trimmed to the viewer's column subset: group query_stats by
    /// (database_name, query_hash) over the window, sum the deltas, rank by
    /// <c>SUM(delta_elapsed_time) DESC</c> (Lite's default — its grid default-sorts
    /// TotalElapsedMs descending), fetch each group's latest non-null query text via a
    /// LATERAL join, drop WAITFOR shells (over-fetch by 5, exactly like Lite's
    /// <c>LIMIT top + 5</c> → filter → <c>LIMIT top</c>), and cap at 50.
    /// PG dialect notes: SUM(bigint) returns numeric, so each aggregate is CAST back to
    /// bigint for the typed readers; Lite's server-local <c>last_execution_time</c> staleness
    /// filter is deliberately dropped (the viewer has no per-server UTC offset; the
    /// delta HAVING already excludes plans that never ran in the window).
    /// $1 server_id, $2 window start (naive UTC).
    /// </summary>
    public const string TopQueriesSql = """
        WITH ranked AS (
            SELECT
                database_name,
                query_hash,
                MAX(last_execution_time) AS last_execution_time,
                CAST(SUM(delta_execution_count) AS bigint) AS total_executions,
                CAST(SUM(delta_worker_time) AS bigint) AS total_cpu_us,
                CAST(SUM(delta_elapsed_time) AS bigint) AS total_elapsed_us,
                CAST(SUM(delta_logical_reads) AS bigint) AS total_reads
            FROM query_stats
            WHERE server_id = $1
            AND   collection_time >= $2
            GROUP BY database_name, query_hash
            HAVING SUM(delta_execution_count) > 0 OR SUM(delta_elapsed_time) > 0
            ORDER BY SUM(delta_elapsed_time) DESC
            LIMIT 55
        )
        SELECT
            r.database_name,
            r.query_hash,
            t.query_text,
            r.total_executions,
            r.total_cpu_us,
            r.total_elapsed_us,
            r.total_reads,
            r.last_execution_time
        FROM ranked AS r
        LEFT JOIN LATERAL (
            SELECT query_text
            FROM query_stats
            WHERE server_id = $1
            AND   query_hash = r.query_hash
            AND   database_name = r.database_name
            AND   query_text IS NOT NULL
            ORDER BY collection_time DESC
            LIMIT 1
        ) AS t ON TRUE
        WHERE t.query_text IS NULL OR t.query_text NOT LIKE 'WAITFOR%'
        ORDER BY r.total_elapsed_us DESC
        LIMIT 50
        """;

    /// <summary>Grid cell preview cap for query/SQL text (single line).</summary>
    public const int CellTextCap = 160;

    /// <summary>Tooltip cap for query/SQL text — enough to read, not a 40 KB balloon.</summary>
    public const int TooltipTextCap = 4000;

    /// <summary>
    /// The top-50 query-stats groups for one server since <paramref name="sinceUtc"/>,
    /// pre-sorted by total elapsed time descending (the grid's default sort).
    /// </summary>
    public async Task<List<QueryStatsGridRow>> GetTopQueriesAsync(
        int serverId, DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        var rows = new List<QueryStatsGridRow>();

        await using var command = _dataSource.CreateCommand(TopQueriesSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Unspecified),
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new QueryStatsGridRow(
                reader.IsDBNull(0) ? "" : reader.GetString(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetDateTime(7)));
        }

        return rows;
    }

    /// <summary>
    /// Collapses whitespace runs (query text arrives with its original formatting) to a single
    /// space and caps at <see cref="CellTextCap"/> with an ellipsis — the one-line grid preview.
    /// </summary>
    public static string TruncateForCell(string? text)
        => Truncate(CollapseWhitespace(text), CellTextCap);

    /// <summary>Caps the raw text at <see cref="TooltipTextCap"/> with an ellipsis, keeping line breaks.</summary>
    public static string TruncateForTooltip(string? text)
        => Truncate(text ?? "", TooltipTextCap);

    private static string Truncate(string text, int cap)
        => text.Length <= cap ? text : text.Substring(0, cap) + "…";

    private static string CollapseWhitespace(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var sb = new System.Text.StringBuilder(Math.Min(text.Length, CellTextCap + 16));
        var inWhitespace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inWhitespace = true;
                continue;
            }

            if (inWhitespace && sb.Length > 0)
            {
                sb.Append(' ');
            }

            inWhitespace = false;
            sb.Append(c);

            if (sb.Length > CellTextCap)
            {
                break; /* preview cap — no need to walk a multi-KB statement */
            }
        }

        return sb.ToString();
    }
}
