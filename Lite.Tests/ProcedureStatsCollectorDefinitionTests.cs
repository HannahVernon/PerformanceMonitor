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
using PerformanceMonitorLite.Services;
using Xunit;

namespace Lite.Tests;

/// <summary>
/// Pins the parity contract of the extracted procedure_stats definition: the dynamic-SQL
/// standard variant with double-escaped literal exclusions, the Azure single-database variant
/// (token intentionally left unreplaced, as the original did), the plan_handle delta key with
/// its db.schema.object fallback, and the 34-column payload.
/// </summary>
public sealed class ProcedureStatsCollectorDefinitionTests
{
    private static readonly RecordingCollectorDeltaCalculator s_deltas = new();

    [Fact]
    public void BuildQuery_Standard_InterpolatesDoubleEscapedLiterals()
    {
        var plan = ProcedureStatsCollector.Instance.BuildQuery(new CollectorContext
        {
            ServerId = 42,
            ServerName = "test-server",
            CollectionTime = DateTime.UtcNow,
            Deltas = s_deltas,
            ExcludedDatabases = new[] { "O'Brien" },
        });

        /* Nested-dynamic-SQL escaping: quote doubled twice → N''O''''Brien'' */
        Assert.Contains("AND d.name NOT IN (N''O''''Brien'')", plan.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("/*EXCLUSION_FILTER*/", plan.Text, StringComparison.Ordinal);
        Assert.Contains("sys.dm_exec_trigger_stats", plan.Text, StringComparison.Ordinal);
        Assert.Contains("sys.dm_exec_function_stats", plan.Text, StringComparison.Ordinal);
        Assert.Empty(plan.Parameters);
    }

    [Fact]
    public void BuildLiteralClause_MatchesLiteOriginal_BothEscapeModes()
    {
        var names = new[] { "O'Brien", "plain" };

        Assert.Equal(
            RemoteCollectorService.BuildDatabaseExclusionLiteralClause(names, "d.name", forNestedDynamicSql: true),
            DatabaseExclusionFilter.BuildLiteralClause(names, "d.name", forNestedDynamicSql: true));
        Assert.Equal(
            RemoteCollectorService.BuildDatabaseExclusionLiteralClause(names, "d.name", forNestedDynamicSql: false),
            DatabaseExclusionFilter.BuildLiteralClause(names, "d.name", forNestedDynamicSql: false));
        Assert.Equal(string.Empty, DatabaseExclusionFilter.BuildLiteralClause(null, "d.name"));
    }

    [Fact]
    public void BuildQuery_Azure_SingleDatabase_TokenLeftInPlace()
    {
        var plan = ProcedureStatsCollector.Instance.BuildQuery(CollectorTestContext.Make(s_deltas, isAzureSqlDb: true));

        Assert.Contains("WHERE s.database_id = DB_ID()", plan.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("dm_exec_trigger_stats", plan.Text, StringComparison.Ordinal);
        /* The original never replaced the token on the Azure variant — pinned as-is. */
        Assert.Contains("/*EXCLUSION_FILTER*/", plan.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadColumns_MatchSchema_34Columns()
    {
        var names = ProcedureStatsCollector.Instance.PayloadColumns.Select(c => c.Name).ToArray();
        Assert.Equal(34, names.Length);
        Assert.Equal("database_name", names[0]);
        Assert.Equal("plan_handle", names[26]);
        Assert.Equal("delta_spills", names[33]);
    }

    [Fact]
    public async Task WritePayload_UsesPlanHandleDeltaKey_WithFallback()
    {
        var deltas = new RecordingCollectorDeltaCalculator();
        var context = CollectorTestContext.Make(deltas);

        using var reader = new FakeCollectorDataReader(
            MakeSqlRow(planHandle: "0x0600"),
            MakeSqlRow(planHandle: null));
        var rows = await ProcedureStatsCollector.Instance.ReadAsync(reader, context, CancellationToken.None);

        var writer = new RecordingCollectorRowWriter();
        ProcedureStatsCollector.Instance.WritePayload(rows[0], writer, context);
        Assert.Equal(34, writer.Values.Count);
        Assert.All(deltas.Calls, c => Assert.Equal("0x0600", c.Key));
        Assert.Equal(
            new[] { "proc_stats_exec", "proc_stats_worker", "proc_stats_elapsed", "proc_stats_reads", "proc_stats_writes", "proc_stats_phys_reads", "proc_stats_spills" },
            deltas.Calls.Select(c => c.Group).ToArray());

        deltas.Calls.Clear();
        ProcedureStatsCollector.Instance.WritePayload(rows[1], writer, context);
        Assert.All(deltas.Calls, c => Assert.Equal("SO.dbo.usp_GetUser", c.Key));
    }

    private static object[] MakeSqlRow(string? planHandle) => new object[]
    {
        "SO", "dbo", "usp_GetUser", "PROCEDURE",
        new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
        100L, 5000L, 8000L, 900L, 10L, 5L,
        1L, 2L, 3L, 4L, 5L, 6L, 7L, 8L, 9L, 10L,
        11L, 0L, 4L,
        "0x0200", planHandle is null ? DBNull.Value : planHandle,
    };
}
