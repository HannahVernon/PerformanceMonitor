/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Npgsql;
using NpgsqlTypes;

namespace PerformanceMonitor.Darling.Viewer;

public sealed partial class ViewerDataService
{
    /// <summary>
    /// #1319: the global database filter parameter for a database-scoped Postgres reader. Each such
    /// reader's SQL bakes in an always-present predicate of the form
    /// <c>AND ($n::text[] IS NULL OR database_name = ANY($n))</c> and ALWAYS binds this parameter, so a
    /// null/empty selection binds SQL NULL and the guard short-circuits to unfiltered (today's behavior).
    /// A non-empty selection binds a <c>text[]</c> and restricts to those databases. Mirrors the array
    /// binding in <see cref="ViewerDataService.MonitoredServers"/>' <c>AddTextArray</c>.
    /// </summary>
    internal static NpgsqlParameter DatabaseFilterParameter(IReadOnlyList<string>? databases) =>
        new()
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            Value = databases is { Count: > 0 } ? databases.ToArray() : (object)DBNull.Value,
        };

    /// <summary>
    /// The always-present SQL predicate for the global database filter, spliced into a database-scoped
    /// WHERE clause. <paramref name="column"/> is the (optionally qualified) database-name column and
    /// <paramref name="paramIndex"/> is the positional index bound by <see cref="DatabaseFilterParameter"/>.
    /// </summary>
    internal static string DatabaseFilterClause(string column, int paramIndex) =>
        $" AND (${paramIndex}::text[] IS NULL OR {column} = ANY(${paramIndex}))";
}
