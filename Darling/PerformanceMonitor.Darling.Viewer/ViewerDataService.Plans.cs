/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// On-demand execution-plan reads for the Plan Viewer host (headless-plan wave): the plan XML the
/// Darling service captures alongside the query/query-store stats (PR #1349, gated on
/// <c>CollectorContext.CapturePlanXml</c> — Darling sets it true, TOAST compresses the text). Unlike
/// Lite — which fetches plans live from the monitored server's plan cache — the viewer never touches
/// SQL Server, so it reads the stored plan text straight out of Postgres by the grid row's key columns.
/// The grid reads carry only a cheap <c>has_query_plan</c> presence flag (no multi-KB XML per row); the
/// full plan is fetched here on click. These lookups are keyed (server + object identity), not windowed,
/// so there is no timestamp parameter and no naive-UTC concern — <c>ORDER BY collection_time DESC LIMIT 1</c>
/// returns the most recently captured plan for that key. Plans can be large; they are read as text with no
/// size games (the task's rule: TOAST handles storage, reading is fine).
/// </summary>
public sealed partial class ViewerDataService
{
    /// <summary>
    /// The latest captured query_stats plan for a Top-Queries row, keyed by (server, database, query_hash) —
    /// the grid groups by (database_name, query_hash), so the same key fetches the plan the row represents.
    /// $1 server_id, $2 database_name, $3 query_hash.
    /// </summary>
    public const string QueryStatsPlanXmlSql = """
        SELECT query_plan_xml
        FROM query_stats
        WHERE server_id = $1
        AND   database_name = $2
        AND   query_hash = $3
        AND   query_plan_xml IS NOT NULL
        ORDER BY collection_time DESC
        LIMIT 1
        """;

    /// <summary>
    /// The stored execution plan for a Top-Queries grid row, or null when no plan was captured for that
    /// (database, query_hash). Read as text — no length cap (the collector stored the whole plan).
    /// </summary>
    public async Task<string?> GetQueryStatsPlanXmlAsync(
        int serverId, string databaseName, string queryHash, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(queryHash))
        {
            return null;
        }

        await using var command = _dataSource.CreateCommand(QueryStatsPlanXmlSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = databaseName ?? "" });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = queryHash });
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string s ? s : null;
    }

    /// <summary>
    /// The latest captured query_store_stats plan for a Query-Store row, keyed by (server, database,
    /// query_id, plan_id) — a Query Store plan_id names one specific compiled plan, so (query_id, plan_id)
    /// uniquely identifies it. $1 server_id, $2 database_name, $3 query_id, $4 plan_id.
    /// </summary>
    public const string QueryStorePlanTextSql = """
        SELECT query_plan_text
        FROM query_store_stats
        WHERE server_id = $1
        AND   database_name = $2
        AND   query_id = $3
        AND   plan_id = $4
        AND   query_plan_text IS NOT NULL
        ORDER BY collection_time DESC
        LIMIT 1
        """;

    /// <summary>
    /// The stored Query Store execution plan for a grid row, or null when no plan text was captured for
    /// that (database, query_id, plan_id). Query Store already stores plans as ShowPlanXML text.
    /// </summary>
    public async Task<string?> GetQueryStorePlanTextAsync(
        int serverId, string databaseName, long queryId, long planId, CancellationToken cancellationToken = default)
    {
        await using var command = _dataSource.CreateCommand(QueryStorePlanTextSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = databaseName ?? "" });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = queryId });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = planId });
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is string s ? s : null;
    }
}
