/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lite.Tests.Helpers;
using PerformanceMonitor.Collectors;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the parity contract of the extracted query_stats definition: the full row-identity delta
/// key (sql_handle:start:end:plan_handle — the multi-statement cross-contamination fix), the
/// interval-captured worker delta feeding sample_interval_seconds, the two query variants, and
/// the 50-column payload with the query_plan_xml placeholder.
/// </summary>
public sealed class QueryStatsCollectorDefinitionTests
{
    private static readonly RecordingCollectorDeltaCalculator s_deltas = new();

    [Fact]
    public void BuildQuery_Standard_JoinsPlanAttributes_WithExclusions()
    {
        var plan = QueryStatsCollector.Instance.BuildQuery(new CollectorContext
        {
            ServerId = 42,
            ServerName = "test-server",
            CollectionTime = DateTime.UtcNow,
            Deltas = s_deltas,
            ExcludedDatabases = new[] { "SO" },
        });

        Assert.Contains("sys.dm_exec_plan_attributes", plan.Text, StringComparison.Ordinal);
        Assert.Contains("AND d.name NOT IN (@excl_db_0)", plan.Text, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE N'%PerformanceMonitorLite%'", plan.Text, StringComparison.Ordinal);
        Assert.Equal("SO", Assert.Single(plan.Parameters).Value);
    }

    [Fact]
    public void BuildQuery_Azure_SkipsPlanAttributes_RunsPerDatabase()
    {
        var plan = QueryStatsCollector.Instance.BuildQuery(CollectorTestContext.Make(s_deltas, isAzureSqlDb: true));

        Assert.DoesNotContain("dm_exec_plan_attributes", plan.Text, StringComparison.Ordinal);
        Assert.Contains("database_name = DB_NAME()", plan.Text, StringComparison.Ordinal);
        Assert.True(QueryStatsCollector.Instance.RunsPerDatabase(new CollectorTargetInfo { IsAzureSqlDb = true }));
        Assert.Empty(plan.Parameters);
    }

    [Fact]
    public void PayloadColumns_MatchSchemaOrder_50Columns()
    {
        var names = QueryStatsCollector.Instance.PayloadColumns.Select(c => c.Name).ToArray();
        Assert.Equal(50, names.Length);
        Assert.Equal("database_name", names[0]);
        Assert.Equal("query_plan_xml", names[37]);
        Assert.Equal("sample_interval_seconds", names[49]);
    }

    [Fact]
    public async Task WritePayload_PinsFullRowIdentityDeltaKey_AndIntervalCapture()
    {
        var deltas = new RecordingCollectorDeltaCalculator();
        var context = CollectorTestContext.Make(deltas);

        var row42 = new object[42];
        row42[0] = "SO"; row42[1] = "0xQH"; row42[2] = "0xQPH";
        row42[3] = new DateTime(2026, 7, 2, 1, 0, 0, DateTimeKind.Utc);
        row42[4] = new DateTime(2026, 7, 2, 2, 0, 0, DateTimeKind.Utc);
        for (int i = 5; i < 36; i++) row42[i] = (long)i;
        row42[22] = 4L; row42[23] = 8L;   /* dop as long via GetValue */
        row42[36] = "0xSH"; row42[37] = "0xPH"; row42[38] = "SELECT 1";
        row42[39] = 3L; row42[40] = 66; row42[41] = 512;

        using var reader = new FakeCollectorDataReader(row42);
        var rows = await QueryStatsCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        var writer = new RecordingCollectorRowWriter();
        QueryStatsCollector.Instance.WritePayload(Assert.Single(rows), writer, context);

        Assert.Equal(50, writer.Values.Count);
        Assert.Null(writer.Values[37]);                                   /* query_plan_xml placeholder */
        Assert.Equal(0, writer.Values[49]);                               /* interval from recording fake */
        Assert.Equal(8, deltas.Calls.Count);
        Assert.All(deltas.Calls, c => Assert.Equal("0xSH:66:512:0xPH", c.Key));
        Assert.Equal(
            new[] { "query_stats_exec", "query_stats_worker", "query_stats_elapsed", "query_stats_reads", "query_stats_writes", "query_stats_phys_reads", "query_stats_rows", "query_stats_spills" },
            deltas.Calls.Select(c => c.Group).ToArray());
    }
}
