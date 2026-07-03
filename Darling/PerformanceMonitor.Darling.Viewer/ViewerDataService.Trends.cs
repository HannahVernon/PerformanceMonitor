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
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PerformanceMonitor.Common;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>One CPU sample for the Overview trend: SQL Server and other-process CPU % at a collection.</summary>
public sealed record CpuTrendPoint(DateTime CollectionTime, double SqlServerCpu, double OtherProcessCpu);

/// <summary>One raw wait row feeding the category roll-up: a collection time, a wait type, and its delta.</summary>
public sealed record WaitCategoryTrendPoint(DateTime CollectionTime, string WaitType, long DeltaWaitTimeMs);

/// <summary>One wait-category chart series: a category label with its aligned time/value arrays.</summary>
public sealed record WaitCategorySeries(string Category, DateTime[] Times, double[] Values);

/// <summary>
/// The Overview tab's two trend-chart reads: CPU utilization and wait time by category. Both window
/// on <c>collection_time</c> (the naive-UTC collection prefix, like every other Darling read — the
/// CPU table's own <c>sample_time</c> is server-LOCAL wall clock and is deliberately not used for the
/// axis). The wait read returns the raw per-type deltas so the C# <see cref="ChartPalette.WaitCategory"/>
/// classifier — the single source of truth the plan-viewer wait list also uses — stays authoritative;
/// the roll-up into categories happens in <see cref="RollUpWaitCategories"/> so it is unit-testable
/// without a live Postgres.
/// </summary>
public sealed partial class ViewerDataService
{
    /// <summary>The top wait categories the Overview chart plots (matches the section header).</summary>
    public const int WaitCategoryChartLimit = 8;

    /// <summary>
    /// CPU % over the window, averaged per collection so the 60-per-run ring-buffer samples collapse
    /// to one point per collection (a clean trend line). AVG(integer) is numeric in Postgres, so the
    /// aggregates are CAST to double precision for the typed GetDouble reader — the same reason the
    /// Queries read casts its SUMs back to bigint. other_process_cpu is NULL on SQL-on-Linux (#1048),
    /// where AVG returns NULL and the reader reads it as 0, mirroring Lite's CPU grid.
    /// </summary>
    public const string CpuTrendSql = """
        SELECT
            collection_time,
            CAST(AVG(sqlserver_cpu_utilization) AS double precision) AS avg_sql_cpu,
            CAST(AVG(other_process_cpu_utilization) AS double precision) AS avg_other_cpu
        FROM cpu_utilization_stats
        WHERE server_id = $1
        AND   collection_time >= $2
        GROUP BY collection_time
        ORDER BY collection_time
        """;

    /// <summary>
    /// Every wait row (all types, not just the top few) since <paramref name="sinceUtc"/>, ordered by
    /// time. The category roll-up needs all types because the top categories are not the top types —
    /// many small types can sum into a dominant category.
    /// </summary>
    public const string WaitCategoryTrendSql = """
        SELECT collection_time, wait_type, delta_wait_time_ms
        FROM wait_stats
        WHERE server_id = $1
        AND   collection_time >= $2
        ORDER BY collection_time
        """;

    /// <summary>
    /// CPU utilization for one server since <paramref name="sinceUtc"/>, one point per collection.
    /// </summary>
    public async Task<List<CpuTrendPoint>> GetCpuTrendAsync(int serverId, DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        var points = new List<CpuTrendPoint>();

        await using var command = _dataSource.CreateCommand(CpuTrendSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Unspecified),
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            points.Add(new CpuTrendPoint(
                reader.GetDateTime(0),
                reader.IsDBNull(1) ? 0 : reader.GetDouble(1),
                /* NULL other-process CPU (SQL on Linux) reads as 0, like Lite's CPU grid. */
                reader.IsDBNull(2) ? 0 : reader.GetDouble(2)));
        }

        return points;
    }

    /// <summary>
    /// The raw wait rows for one server since <paramref name="sinceUtc"/>, ready for
    /// <see cref="RollUpWaitCategories"/>. Rows arrive time-ordered so each rolled-up series stays
    /// in chronological order.
    /// </summary>
    public async Task<List<WaitCategoryTrendPoint>> GetWaitCategoryTrendAsync(int serverId, DateTime sinceUtc, CancellationToken cancellationToken = default)
    {
        var points = new List<WaitCategoryTrendPoint>();

        await using var command = _dataSource.CreateCommand(WaitCategoryTrendSql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = serverId });
        command.Parameters.Add(new NpgsqlParameter<DateTime>
        {
            TypedValue = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Unspecified),
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            points.Add(new WaitCategoryTrendPoint(
                reader.GetDateTime(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? 0 : reader.GetInt64(2)));
        }

        return points;
    }

    /// <summary>
    /// Rolls the raw wait rows up into per-category series: each wait type is classified with
    /// <see cref="ChartPalette.WaitCategory"/> (the shared classifier), the deltas are summed per
    /// (category, collection_time), and the top <paramref name="topCategories"/> categories by total
    /// delta wait time over the window are returned — ranked by total descending, ties broken by
    /// category name so the order (and therefore the color assignment) is deterministic. Each series'
    /// times are chronological and index-aligned with its values.
    /// </summary>
    public static IReadOnlyList<WaitCategorySeries> RollUpWaitCategories(
        IReadOnlyList<WaitCategoryTrendPoint> points,
        int topCategories = WaitCategoryChartLimit)
    {
        if (points is null)
        {
            throw new ArgumentNullException(nameof(points));
        }

        var byCategory = new Dictionary<string, SortedDictionary<DateTime, double>>(StringComparer.Ordinal);
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var point in points)
        {
            var category = ChartPalette.WaitCategory(point.WaitType);

            if (!byCategory.TryGetValue(category, out var samples))
            {
                samples = new SortedDictionary<DateTime, double>();
                byCategory[category] = samples;
            }

            samples.TryGetValue(point.CollectionTime, out var running);
            samples[point.CollectionTime] = running + point.DeltaWaitTimeMs;

            totals.TryGetValue(category, out var categoryTotal);
            totals[category] = categoryTotal + point.DeltaWaitTimeMs;
        }

        return totals
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(topCategories)
            .Select(kv =>
            {
                var samples = byCategory[kv.Key];
                return new WaitCategorySeries(kv.Key, samples.Keys.ToArray(), samples.Values.ToArray());
            })
            .ToList();
    }
}
