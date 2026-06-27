/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System.Collections.Generic;
using PerformanceMonitor.Ui;
using Xunit;

namespace PerformanceMonitorDashboard.Tests;

/// <summary>
/// Tests the pure, app-agnostic logic behind the shared chart click-to-isolate mechanic
/// (<see cref="ChartHoverHelper"/>): toggle transitions, the dim-vs-restore visual decision, the
/// isolate Y-fit range math (incl. the degenerate-flat guard and the deliberate no-zero-anchor),
/// and the axis-rules save/clear/restore bookkeeping. No live WpfPlot needed. Mirror of
/// Lite.Tests for parity.
/// </summary>
public class ChartClickIsolateTests
{
    // ── NextIsolate: toggle transitions ──────────────────────────────────────────────────────

    [Fact]
    public void NextIsolate_FromNothing_IsolatesClicked()
    {
        Assert.Equal("CXPACKET", ChartHoverHelper.NextIsolate(null, "CXPACKET"));
    }

    [Fact]
    public void NextIsolate_ClickingIsolatedSeries_TogglesOff()
    {
        Assert.Null(ChartHoverHelper.NextIsolate("CXPACKET", "CXPACKET"));
    }

    [Fact]
    public void NextIsolate_ClickingDifferentSeries_SwitchesTarget()
    {
        Assert.Equal("WRITELOG", ChartHoverHelper.NextIsolate("CXPACKET", "WRITELOG"));
    }

    [Fact]
    public void NextIsolate_IsCaseSensitive_DifferentCaseIsADifferentSeries()
    {
        // Labels are exact series identifiers; a case difference is a different series, not a toggle-off.
        Assert.Equal("cxpacket", ChartHoverHelper.NextIsolate("CXPACKET", "cxpacket"));
    }

    // ── ResolveSeriesVisual: dim vs restore decision + FillY flag ─────────────────────────────

    [Fact]
    public void ResolveSeriesVisual_NothingIsolated_EverySeriesIsFull()
    {
        var v = ChartHoverHelper.ResolveSeriesVisual(null, "AnySeries");
        Assert.False(v.Dim);
        Assert.True(v.FillRibbon);
    }

    [Fact]
    public void ResolveSeriesVisual_TargetSeries_IsFull()
    {
        var v = ChartHoverHelper.ResolveSeriesVisual("WRITELOG", "WRITELOG");
        Assert.False(v.Dim);
        Assert.True(v.FillRibbon);
    }

    [Fact]
    public void ResolveSeriesVisual_NonTargetSeries_IsDimmedWithNoFill()
    {
        var v = ChartHoverHelper.ResolveSeriesVisual("WRITELOG", "CXPACKET");
        Assert.True(v.Dim);
        Assert.False(v.FillRibbon);                 // the gradient ribbon is dropped while dimmed
        Assert.Equal(ChartHoverHelper.DimAlpha, v.LineAlpha);
    }

    [Fact]
    public void DimAlpha_IsFaintButVisible()
    {
        Assert.Equal((byte)40, ChartHoverHelper.DimAlpha);
        Assert.Equal((byte)40, ChartHoverHelper.IsolateVisual.Dimmed.LineAlpha);
        Assert.True(ChartHoverHelper.IsolateVisual.Full.FillRibbon);
        Assert.False(ChartHoverHelper.IsolateVisual.Full.Dim);
    }

    // ── ComputeIsolateYLimits: AutoFitY range math ────────────────────────────────────────────

    private static IReadOnlyList<(double X, double Y)> Pts(params (double X, double Y)[] p) => p;

    [Fact]
    public void ComputeIsolateYLimits_FitsVisibleMinMax_WithFivePercentMargin()
    {
        var r = ChartHoverHelper.ComputeIsolateYLimits(Pts((1, 0), (2, 10), (3, 5)), xMin: 1, xMax: 3);
        Assert.NotNull(r);
        Assert.Equal(-0.5, r!.Value.Min, 6);        // 0 - (10-0)*0.05
        Assert.Equal(10.5, r.Value.Max, 6);         // 10 + (10-0)*0.05
    }

    [Fact]
    public void ComputeIsolateYLimits_OnlyConsidersPointsInVisibleXRange()
    {
        // The x=1 spike at y=100 is outside the visible window [2,3] and must be excluded.
        var r = ChartHoverHelper.ComputeIsolateYLimits(Pts((1, 100), (2, 0), (3, 10)), xMin: 2, xMax: 3);
        Assert.NotNull(r);
        Assert.Equal(-0.5, r!.Value.Min, 6);
        Assert.Equal(10.5, r.Value.Max, 6);
    }

    [Fact]
    public void ComputeIsolateYLimits_DegenerateFlatSeries_WidensToNonZeroHeight()
    {
        // A flat line (min == max) would give a zero-height axis; the guard widens it to [min, min+1].
        var r = ChartHoverHelper.ComputeIsolateYLimits(Pts((1, 5), (2, 5), (3, 5)), xMin: 1, xMax: 3);
        Assert.NotNull(r);
        Assert.Equal(4.95, r!.Value.Min, 6);        // 5   - (6-5)*0.05
        Assert.Equal(6.05, r.Value.Max, 6);         // 5+1 + (6-5)*0.05
    }

    [Fact]
    public void ComputeIsolateYLimits_HighBaseline_DoesNotAnchorToZero()
    {
        // The whole point of isolate: reveal a series' own variation even at a high baseline.
        var r = ChartHoverHelper.ComputeIsolateYLimits(Pts((1, 5000), (2, 5100)), xMin: 1, xMax: 2);
        Assert.NotNull(r);
        Assert.Equal(4995, r!.Value.Min, 6);        // 5000 - (5100-5000)*0.05  — NOT 0
        Assert.Equal(5105, r.Value.Max, 6);
    }

    [Fact]
    public void ComputeIsolateYLimits_NoPointsVisible_FallsBackToWholeSeries()
    {
        var r = ChartHoverHelper.ComputeIsolateYLimits(Pts((1, 2), (2, 4)), xMin: 10, xMax: 20);
        Assert.NotNull(r);
        Assert.Equal(1.9, r!.Value.Min, 6);         // falls back to the full series: 2 - (4-2)*0.05
        Assert.Equal(4.1, r.Value.Max, 6);
    }

    [Fact]
    public void ComputeIsolateYLimits_IgnoresNaNAndInfinity()
    {
        var r = ChartHoverHelper.ComputeIsolateYLimits(
            Pts((1, double.NaN), (2, 10), (3, double.PositiveInfinity), (4, 20)), xMin: 1, xMax: 4);
        Assert.NotNull(r);
        Assert.Equal(9.5, r!.Value.Min, 6);         // only y=10 and y=20 count
        Assert.Equal(20.5, r.Value.Max, 6);
    }

    [Fact]
    public void ComputeIsolateYLimits_EmptySeries_ReturnsNull()
    {
        Assert.Null(ChartHoverHelper.ComputeIsolateYLimits(Pts(), xMin: 0, xMax: 1));
    }

    [Fact]
    public void ComputeIsolateYLimits_AllNonFinite_ReturnsNull()
    {
        var r = ChartHoverHelper.ComputeIsolateYLimits(
            Pts((1, double.NaN), (2, double.NegativeInfinity)), xMin: 1, xMax: 2);
        Assert.Null(r);
    }

    // ── SaveAndClearRules / RestoreAxisRules: bookkeeping ─────────────────────────────────────

    [Fact]
    public void SaveAndClearRules_SnapshotsThenEmptiesLiveList()
    {
        var live = new List<string> { "LockedVertical", "OtherRule" };
        var saved = ChartHoverHelper.SaveAndClearRules(live);

        Assert.Equal(new[] { "LockedVertical", "OtherRule" }, saved);
        Assert.Empty(live);                          // cleared so the isolate Y-fit can stick
    }

    [Fact]
    public void RestoreAxisRules_ReplacesWhateverARerenderInstalled_WithTheSavedRules()
    {
        var live = new List<string> { "LockedVertical" };
        var saved = ChartHoverHelper.SaveAndClearRules(live);

        // Simulate a re-render installing a fresh rule into the now-empty live list.
        live.Add("FreshlyInstalledRule");

        ChartHoverHelper.RestoreAxisRules(live, saved);
        Assert.Equal(new[] { "LockedVertical" }, live);
    }

    [Fact]
    public void RestoreAxisRules_NullSaved_IsNoOp_AndLeavesLiveRulesIntact()
    {
        // Lite installs no rules, so _savedRules is null there — restore must NOT clear the live list.
        var live = new List<string> { "SomeLiveRule" };
        ChartHoverHelper.RestoreAxisRules(live, (IReadOnlyList<string>?)null);
        Assert.Equal(new[] { "SomeLiveRule" }, live);
    }

    [Fact]
    public void SaveAndRestore_RoundTrips_EmptyRuleSet()
    {
        var live = new List<string>();
        var saved = ChartHoverHelper.SaveAndClearRules(live);
        Assert.Empty(saved);

        live.Add("AddedDuringIsolate");
        ChartHoverHelper.RestoreAxisRules(live, saved);
        Assert.Empty(live);                          // back to the original (empty) rule set
    }

    // ── RestoreSeriesVisual: faithful restore for line-only AND fill charts (regression) ─────────

    [Fact]
    public void RestoreSeriesVisual_LineOnlyChart_StaysLineOnly_NoPhantomMarkersOrFill()
    {
        // CollectorDuration / trend charts build line-only (MarkerSize 0, no fill) and never call
        // StyleScatter. Restore must NOT re-run StyleScatter — that would add density markers + a fill.
        var plot = new ScottPlot.Plot();
        var sc = plot.Add.Scatter(new double[] { 1, 2, 3 }, new double[] { 1, 2, 3 });
        var identity = ScottPlot.Color.FromHex("#4E79A7");
        sc.Color = identity;
        sc.LineWidth = 1.5f;
        sc.MarkerSize = 0;
        sc.FillY = false;
        var entry = new ChartHoverHelper.SeriesEntry(sc, "Collector", identity,
            sc.LineColor, sc.LineWidth, sc.MarkerSize, sc.FillY);

        sc.Color = identity.WithAlpha(ChartHoverHelper.DimAlpha);   // simulate a dim
        ChartHoverHelper.RestoreSeriesVisual(entry);

        Assert.Equal(0f, sc.MarkerSize);             // no phantom density markers
        Assert.False(sc.FillY);                      // no phantom fill ribbon
        Assert.Equal(1.5f, sc.LineWidth);            // original width preserved
    }

    [Fact]
    public void RestoreSeriesVisual_FillChart_RebuildsTheStyleScatterLook()
    {
        // A StyleScatter'd fill chart restores via StyleScatter, which rebuilds the gradient from the
        // unchanged data — reproducing the original look (isolate never spans a re-render).
        var plot = new ScottPlot.Plot();
        var sc = plot.Add.Scatter(new double[] { 1, 2, 3 }, new double[] { 0, 5, 10 });
        var identity = ScottPlot.Color.FromHex("#4E79A7");
        sc.Color = identity;
        ChartStyle.StyleScatter(sc);
        var entry = new ChartHoverHelper.SeriesEntry(sc, "Wait", identity,
            sc.LineColor, sc.LineWidth, sc.MarkerSize, sc.FillY);
        Assert.True(entry.OrigFillY);                // StyleScatter set FillY=true (data has a range)

        sc.Color = identity.WithAlpha(ChartHoverHelper.DimAlpha);
        sc.FillY = false;                            // simulate a dim
        ChartHoverHelper.RestoreSeriesVisual(entry);

        Assert.True(sc.FillY);                       // fill ribbon rebuilt
        Assert.Equal(2f, sc.LineWidth);              // StyleScatter's signature line width
    }
}
