/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;

namespace PerformanceMonitor.Collectors;

/// <summary>
/// Builds the parameterized database-exclusion fragment collectors splice into their queries.
/// Ported verbatim from Lite's RemoteCollectorService.BuildDatabaseExclusionFilter (which remains
/// in place for the collectors not yet migrated — DatabaseExclusionFilterTests pins this copy so
/// the two cannot drift silently; the Lite copy dies when the last caller migrates).
/// Each name is parameterized (@excl_db_N, nvarchar(128)) — works on every supported SQL Server
/// version (no STRING_SPLIT/OPENJSON compatibility-level dependency).
/// </summary>
public static class DatabaseExclusionFilter
{
    /// <param name="excludedDatabaseNames">Names from the server's excluded-databases config.</param>
    /// <param name="columnExpression">SQL column to filter, e.g. "d.name".</param>
    public static (string Clause, List<CollectorParameter> Parameters) Build(
        IReadOnlyList<string>? excludedDatabaseNames, string columnExpression)
    {
        if (excludedDatabaseNames is null || excludedDatabaseNames.Count == 0)
        {
            return (string.Empty, new List<CollectorParameter>());
        }

        var paramNames = new List<string>(excludedDatabaseNames.Count);
        var parameters = new List<CollectorParameter>(excludedDatabaseNames.Count);
        for (int i = 0; i < excludedDatabaseNames.Count; i++)
        {
            string p = $"@excl_db_{i}";
            paramNames.Add(p);
            parameters.Add(new CollectorParameter(p, excludedDatabaseNames[i], CollectorParameterType.NVarChar128));
        }

        return ($"AND {columnExpression} NOT IN ({string.Join(", ", paramNames)})", parameters);
    }
}
