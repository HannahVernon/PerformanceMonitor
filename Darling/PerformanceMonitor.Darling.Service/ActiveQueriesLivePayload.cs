/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Text.Json;
using PerformanceMonitor.Collectors;

namespace PerformanceMonitor.Darling.Service;

/// <summary>
/// Builds the <c>result_json</c> a <c>fetch_active_queries</c> command reports back to the viewer: a live DMV
/// snapshot of the requests running on a monitored server right now, shredded by the shared
/// <see cref="QuerySnapshotsCollector"/> into its <see cref="QuerySnapshotsCollector.Row"/> shape (the SAME
/// shape the collector stores), so the viewer renders the live rows in the same grid as the stored ones.
///
/// <para>Pure and side-effect-free so a unit test can pin the wire contract end-to-end WITHOUT a live SQL
/// Server: build <see cref="QuerySnapshotsCollector.Row"/>s, serialize here, and assert the viewer's
/// <c>ViewerDataService.ParseActiveQueriesLiveResult</c> reconstructs them. The row objects are serialized
/// whole (all columns), and the viewer parses them case-insensitively — the round-trip test guards the two
/// ends against drift. The payload carries a <c>fetchedAtUtc</c> so the viewer can stamp each row's
/// "collected" time (the DMV row itself has no capture timestamp — the collector stamps it at store time).</para>
/// </summary>
public static class ActiveQueriesLivePayload
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        /* No naming policy — the row properties serialize as PascalCase and the viewer parses
           case-insensitively, so the two ends never depend on a shared policy setting. */
    };

    /// <summary>
    /// Serializes the live snapshot rows into the command's <c>result_json</c>:
    /// <c>{"success":true,"fetchedAtUtc":"…","rows":[ …one object per running request… ]}</c>.
    /// </summary>
    public static string Serialize(IReadOnlyList<QuerySnapshotsCollector.Row> rows, DateTime fetchedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return JsonSerializer.Serialize(new
        {
            success = true,
            fetchedAtUtc,
            rows,
        }, s_options);
    }
}
