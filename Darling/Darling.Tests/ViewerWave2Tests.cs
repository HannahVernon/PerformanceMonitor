/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Windows.Media;
using PerformanceMonitor.Alerting;
using PerformanceMonitor.Analysis;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// Pins the viewer wave-2 SQL against the Darling store contract (no live Postgres needed):
/// the Queries top-50 read, and the two blocking reads whose XE-preferred/DMV-fallback merge
/// runs through the SHARED <see cref="BlockedProcessReportMerge"/> on the shared row shape.
/// </summary>
public sealed class ViewerWave2SqlTests
{
    [Fact]
    public void TopQueriesSql_GroupsQueryStatsByDatabaseAndHash_OverTheWindow()
    {
        Assert.Contains("FROM query_stats", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY database_name, query_hash", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
        /* Delta-artifact rows (plan cached, never executed) are excluded like Lite's read. */
        Assert.Contains("HAVING SUM(delta_execution_count) > 0 OR SUM(delta_elapsed_time) > 0", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
    }

    [Fact]
    public void TopQueriesSql_RanksByTotalElapsedDelta_LikeLitesDefaultSort()
    {
        /* Lite's grid default-sorts TotalElapsedMs descending and its SQL ranks by summed
           delta_elapsed_time — the viewer mirrors that, over-fetching 5 like Lite's
           LIMIT top + 5 so the WAITFOR trim can't shrink the page below 50. */
        Assert.Contains("ORDER BY SUM(delta_elapsed_time) DESC", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 55", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY r.total_elapsed_us DESC", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 50", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
        Assert.Contains("NOT LIKE 'WAITFOR%'", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
    }

    [Fact]
    public void TopQueriesSql_FetchesTheLatestNonNullQueryText_ViaLateralJoin()
    {
        Assert.Contains("LEFT JOIN LATERAL", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
        Assert.Contains("query_text IS NOT NULL", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY collection_time DESC", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 1", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
    }

    [Fact]
    public void TopQueriesSql_CastsEveryAggregateBackToBigint()
    {
        /* Postgres SUM(bigint) returns numeric; without the CAST the typed GetInt64 readers throw. */
        Assert.Contains("CAST(SUM(delta_execution_count) AS bigint)", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
        Assert.Contains("CAST(SUM(delta_worker_time) AS bigint)", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
        Assert.Contains("CAST(SUM(delta_elapsed_time) AS bigint)", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
        Assert.Contains("CAST(SUM(delta_logical_reads) AS bigint)", ViewerDataService.TopQueriesSql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ViewerDataService.BlockedProcessReportsSql, "FROM blocked_process_reports")]
    [InlineData(ViewerDataService.DmvBlockingSnapshotsSql, "FROM dmv_blocking_snapshots")]
    public void BlockingSql_WindowsOnCollectionTime_NewestEventsFirst_Cap200(string sql, string fromClause)
    {
        /* The alert path's read semantics (DarlingAlertReadAdapter), verbatim. */
        Assert.Contains(fromClause, sql, StringComparison.Ordinal);
        Assert.Contains("WHERE server_id = $1", sql, StringComparison.Ordinal);
        Assert.Contains("collection_time >= $2", sql, StringComparison.Ordinal);
        Assert.Contains("collection_time <= $3", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY event_time DESC", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT 200", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ViewerDataService.BlockedProcessReportsSql)]
    [InlineData(ViewerDataService.DmvBlockingSnapshotsSql)]
    public void BlockingSql_SelectsTheSharedRowColumns(string sql)
    {
        foreach (var column in new[]
        {
            "event_time", "database_name", "blocked_spid", "blocking_spid",
            "wait_time_ms", "lock_mode", "blocked_sql_text", "blocking_sql_text",
            "contentious_object",
        })
        {
            Assert.Contains(column, sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void BlockingSql_DeliberatelySkipsTheReportXmlColumn()
    {
        /* Nothing in this slice renders the XML and it can run to hundreds of KB per row. */
        Assert.DoesNotContain("blocked_process_report_xml", ViewerDataService.BlockedProcessReportsSql, StringComparison.Ordinal);
    }
}

/// <summary>
/// The viewer's blocking rows ARE the shared alert row shape, so the XE-preferred/DMV-fallback
/// dedup + re-cap semantics come from the one shared <see cref="BlockedProcessReportMerge"/> —
/// exercised here through <see cref="ViewerBlockedProcessRow"/> lists exactly as the read path
/// calls it.
/// </summary>
public sealed class ViewerBlockingMergeTests
{
    private static ViewerBlockedProcessRow Row(string source, DateTime eventTime, int blocked, int blocking)
        => new()
        {
            Source = source,
            EventTime = eventTime,
            BlockedSpid = blocked,
            BlockingSpid = blocking,
        };

    [Fact]
    public void ViewerRow_DerivesFromTheSharedAlertRow()
    {
        Assert.IsAssignableFrom<BlockedProcessAlertRow>(new ViewerBlockedProcessRow());
    }

    [Fact]
    public void Merge_KeepsXeRows_AndDropsDmvRowsCoveredWithinTheSameMinute()
    {
        var t = new DateTime(2026, 7, 1, 12, 0, 5);
        var items = new List<ViewerBlockedProcessRow>
        {
            Row(BlockedProcessAlertRow.XeReportSource, t, blocked: 51, blocking: 60),
        };
        var dmvItems = new List<ViewerBlockedProcessRow>
        {
            /* Same SPID pair, same minute — covered by the XE row, so dropped. */
            Row(BlockedProcessAlertRow.DmvSnapshotSource, t.AddSeconds(20), blocked: 51, blocking: 60),
            /* Different pair — appended. */
            Row(BlockedProcessAlertRow.DmvSnapshotSource, t.AddSeconds(30), blocked: 52, blocking: 61),
        };

        BlockedProcessReportMerge.AppendDmvFallbackRows(items, dmvItems);

        Assert.Equal(2, items.Count);
        Assert.Single(items, r => r.Source == BlockedProcessAlertRow.XeReportSource);
        var dmvSurvivor = Assert.Single(items, r => r.Source == BlockedProcessAlertRow.DmvSnapshotSource);
        Assert.Equal(52, dmvSurvivor.BlockedSpid);
    }

    [Fact]
    public void Merge_ReordersNewestFirst_AndReCapsToTheGridLimit()
    {
        var t = new DateTime(2026, 7, 1, 12, 0, 0);
        var items = new List<ViewerBlockedProcessRow>
        {
            Row(BlockedProcessAlertRow.XeReportSource, t, blocked: 51, blocking: 60),
        };
        var dmvItems = new List<ViewerBlockedProcessRow>
        {
            Row(BlockedProcessAlertRow.DmvSnapshotSource, t.AddMinutes(5), blocked: 70, blocking: 71),
        };

        BlockedProcessReportMerge.AppendDmvFallbackRows(items, dmvItems, cap: 1);

        /* Newest wins the cap — the later DMV row displaces the older XE row. */
        var survivor = Assert.Single(items);
        Assert.Equal(BlockedProcessAlertRow.DmvSnapshotSource, survivor.Source);
    }
}

/// <summary>The wave-2 pure display helpers: wait-time text, local times, and text truncation.</summary>
public sealed class ViewerWave2DisplayTests
{
    [Theory]
    [InlineData(0, "0 ms")]
    [InlineData(999, "999 ms")]
    [InlineData(1000, "1.0 sec")]
    [InlineData(1500, "1.5 sec")]
    [InlineData(30000, "30.0 sec")]
    public void FormatWaitTime_MirrorsLitesRendering(long waitMs, string expected)
    {
        Assert.Equal(expected, ViewerDataService.FormatWaitTime(waitMs));
    }

    [Fact]
    public void BlockedRow_EventTimeLocal_TreatsTheStoredValueAsUtc()
    {
        var storedUtc = new DateTime(2026, 7, 1, 3, 30, 0, DateTimeKind.Unspecified);
        var row = new ViewerBlockedProcessRow { EventTime = storedUtc };

        Assert.NotNull(row.EventTimeLocal);
        Assert.Equal(storedUtc.Ticks, row.EventTimeLocal!.Value.ToUniversalTime().Ticks);
        Assert.Null(new ViewerBlockedProcessRow().EventTimeLocal);
    }

    [Fact]
    public void TruncateForCell_CollapsesWhitespace_AndCapsWithEllipsis()
    {
        Assert.Equal("SELECT * FROM t", ViewerDataService.TruncateForCell("  SELECT *\r\n    FROM\tt  "));
        Assert.Equal("", ViewerDataService.TruncateForCell(null));

        var longText = new string('a', 400);
        var preview = ViewerDataService.TruncateForCell(longText);
        Assert.Equal(ViewerDataService.CellTextCap + 1, preview.Length); /* cap + ellipsis */
        Assert.EndsWith("…", preview, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncateForTooltip_KeepsLineBreaks_AndCaps()
    {
        Assert.Equal("SELECT 1\r\nFROM t", ViewerDataService.TruncateForTooltip("SELECT 1\r\nFROM t"));
        Assert.Equal("", ViewerDataService.TruncateForTooltip(null));

        var longText = new string('b', ViewerDataService.TooltipTextCap + 50);
        var tooltip = ViewerDataService.TruncateForTooltip(longText);
        Assert.Equal(ViewerDataService.TooltipTextCap + 1, tooltip.Length);
        Assert.EndsWith("…", tooltip, StringComparison.Ordinal);
    }
}

/// <summary>
/// The Recommendations tab's finding mapping: Lite's severity cutoffs and display order, the
/// headline-or-fact-key title, the composed detail text (advice via
/// <see cref="FactAdvice.GetForFinding"/>), and the read-only pretty-print of the persisted
/// remediation action.
/// </summary>
public sealed class ViewerFindingTests
{
    private static AnalysisFinding Finding(
        double severity = 1.0,
        string rootFactKey = "SOME_UNKNOWN_FACT_KEY",
        string storyText = "",
        string? database = null,
        RemediationAction? remediation = null)
        => new()
        {
            FindingId = 1,
            AnalysisTime = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc),
            ServerId = 1,
            ServerName = "SQL2022",
            DatabaseName = database,
            Severity = severity,
            Confidence = 0.9,
            Category = "cpu",
            StoryPath = "a -> b",
            StoryPathHash = "hash-" + rootFactKey + severity.ToString(CultureInfo.InvariantCulture),
            StoryText = storyText,
            RootFactKey = rootFactKey,
            FactCount = 2,
            Remediation = remediation,
        };

    [Theory]
    [InlineData(2.0, "CRITICAL")]
    [InlineData(1.5, "CRITICAL")]
    [InlineData(1.49, "WARNING")]
    [InlineData(0.75, "WARNING")]
    [InlineData(0.74, "INFO")]
    [InlineData(0.0, "INFO")]
    public void SeverityBand_UsesBothAppsSharedCutoffs(double severity, string expected)
    {
        Assert.Equal(expected, ViewerDataService.SeverityBand(severity));
    }

    [Fact]
    public void FindingTitle_PrefersTheFrozenAdviceHeadline_FallsBackToTheRootFactKey()
    {
        var frozen = FactAdvice.SerializeForStoryText(new AdviceBlock("Frozen headline", "look here", "do this"));

        Assert.Equal("Frozen headline", ViewerDataService.FindingTitle(Finding(storyText: frozen)));
        Assert.Equal("SOME_UNKNOWN_FACT_KEY", ViewerDataService.FindingTitle(Finding()));
    }

    [Fact]
    public void MapFindings_SortsBandDesc_ThenRawDesc_ThenDatabase()
    {
        var findings = new List<AnalysisFinding>
        {
            Finding(severity: 0.5),
            Finding(severity: 1.0, database: "beta"),
            Finding(severity: 1.0, database: "alpha"),
            Finding(severity: 1.9),
            Finding(severity: 1.2),
        };

        var rows = ViewerDataService.MapFindings(findings);

        Assert.Equal(new[] { "CRITICAL", "WARNING", "WARNING", "WARNING", "INFO" },
            rows.ConvertAll(r => r.SeverityBand));
        Assert.Equal(1.9, rows[0].RawSeverity);
        Assert.Equal(1.2, rows[1].RawSeverity);
        /* Equal band + raw: database ascending, case-insensitive. */
        Assert.Equal("alpha", rows[2].DatabaseName);
        Assert.Equal("beta", rows[3].DatabaseName);
    }

    [Fact]
    public void ComposeFindingDetailText_RendersTheComposedStory()
    {
        var frozen = FactAdvice.SerializeForStoryText(
            new AdviceBlock("CPU is on fire", "check the top queries", "tune or cap them"));

        var text = ViewerDataService.ComposeFindingDetailText(Finding(storyText: frozen));

        Assert.StartsWith("CPU is on fire", text, StringComparison.Ordinal);
        Assert.Contains("Investigation:", text, StringComparison.Ordinal);
        Assert.Contains("check the top queries", text, StringComparison.Ordinal);
        Assert.Contains("Remediation:", text, StringComparison.Ordinal);
        Assert.Contains("tune or cap them", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeFindingDetailText_UnknownFactKey_SaysSo()
    {
        var text = ViewerDataService.ComposeFindingDetailText(Finding());

        Assert.StartsWith("SOME_UNKNOWN_FACT_KEY", text, StringComparison.Ordinal);
        Assert.Contains("No advice text is available", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeFindingDetailText_AppendsThePersistedAction_AsReadOnlyJson()
    {
        var action = new RemediationAction(
            "PLAN_REGRESSION", "force",
            new[] { new ForcePlanTarget("StackOverflow", QueryId: 42, PlanId: 7) });

        var text = ViewerDataService.ComposeFindingDetailText(Finding(remediation: action));

        Assert.Contains("Stored remediation action (read-only):", text, StringComparison.Ordinal);
        Assert.Contains("\"FactKey\": \"PLAN_REGRESSION\"", text, StringComparison.Ordinal);
        /* No Apply affordance in the viewer — the action renders as text only. */
        Assert.DoesNotContain("Apply", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PrettyPrintRemediation_IndentsTheSharedSerializersJson_NullForNoAction()
    {
        Assert.Null(ViewerDataService.PrettyPrintRemediation(null));

        var action = new RemediationAction(
            "PLAN_REGRESSION", "force",
            new[] { new ForcePlanTarget("StackOverflow", QueryId: 42, PlanId: 7) });

        var json = ViewerDataService.PrettyPrintRemediation(action);

        Assert.NotNull(json);
        Assert.Contains(Environment.NewLine, json, StringComparison.Ordinal); /* indented, not compact */
        using var document = JsonDocument.Parse(json!); /* still valid JSON */
        Assert.Equal("PLAN_REGRESSION", document.RootElement.GetProperty("FactKey").GetString());
        Assert.Equal(42, document.RootElement.GetProperty("Targets")[0].GetProperty("QueryId").GetInt64());
    }
}

/// <summary>Wave 2 extends the status converter to the severity bands.</summary>
public sealed class SeverityBandBrushTests
{
    private static Color ColorFor(string? status)
    {
        var converter = new StatusToBrushConverter();
        var brush = Assert.IsType<SolidColorBrush>(converter.Convert(status, typeof(Brush), null, CultureInfo.InvariantCulture));
        return brush.Color;
    }

    [Fact]
    public void Convert_MapsSeverityBands_SharingTheHealthStatusColors()
    {
        /* CRITICAL reads like ERROR, WARNING like PERMISSIONS — one visual language. */
        Assert.Equal(ColorFor("ERROR"), ColorFor("CRITICAL"));
        Assert.Equal(ColorFor("PERMISSIONS"), ColorFor("WARNING"));

        var info = ColorFor("INFO");
        Assert.NotEqual(ColorFor("CRITICAL"), info);
        Assert.NotEqual(ColorFor("WARNING"), info);
        Assert.NotEqual(ColorFor("SUCCESS"), info);
        Assert.NotEqual(ColorFor("SKIPPED"), info); /* default foreground */

        Assert.Equal(info, ColorFor("info")); /* case-insensitive like the statuses */
    }
}
