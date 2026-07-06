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
using System.Threading.Tasks;
using PerformanceMonitor.Common;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Latches &amp; Spinlocks inner tab — the Dashboard-parity port of ResourceMetricsContent's Latch
/// Stats / Spinlock Stats sub-tabs into the Darling viewer. Each sub-tab pairs a per-second trend chart
/// for the TOP 5 contenders (latch classes by delta wait time, spinlocks by delta collisions) with a
/// latest-snapshot grid of the most recent collection in the settable window. The Darling cumulative-delta
/// tables carry no stored <c>sample_interval_seconds</c> (unlike the Dashboard's), so the ms/sec and
/// collisions/sec rates are computed in SQL from the per-contender <c>LAG</c> interval — the same idiom
/// the Wait Stats trend uses. Both sub-tabs (re)load on the parent tab's activation (mirroring the Memory
/// tab's full-refresh branch), so the sub-TabControl needs no SelectionChanged handler. Chart chrome /
/// legend / line polish flow through the shared <see cref="ChartStyle"/> / <see cref="ChartPalette"/> and
/// the <c>ViewerServerTab.ChartHelpers.cs</c> bridge, so the Y-floor-at-0 fix applies; series ride the
/// cycling <c>SeriesColors</c> (declared in ViewerServerTab.Charts.cs).
/// </summary>
public partial class ViewerServerTab
{
    private ChartHoverHelper? _latchStatsHover;
    private ChartHoverHelper? _spinlockStatsHover;

    /// <summary>Applies the shared chrome + hover to the latch/spinlock charts up front (constructor),
    /// so they don't flash white before the tab's first load — matching the CPU/Memory charts.</summary>
    private void InitializeLatchSpinlockCharts()
    {
        ApplyTheme(LatchStatsChart);
        LatchStatsChart.Refresh();
        ApplyTheme(SpinlockStatsChart);
        SpinlockStatsChart.Refresh();

        _latchStatsHover = new ChartHoverHelper(LatchStatsChart, "ms/sec");
        _spinlockStatsHover = new ChartHoverHelper(SpinlockStatsChart, "/sec");
    }

    /// <summary>
    /// Loads both sub-tabs (Latch Stats + Spinlock Stats) over the toolbar's settable window: the four
    /// reads (two trends, two snapshots) fire concurrently — NpgsqlDataSource pools a connection each —
    /// then the charts and grids render.
    /// </summary>
    private async Task LoadLatchSpinlockAsync()
    {
        var (startUtc, endUtc) = GetWindowUtc();

        var latchTrendTask = _dataService.GetLatchStatsTrendAsync(_server.ServerId, startUtc, endUtc);
        var latchSnapshotTask = _dataService.GetLatchStatsSnapshotAsync(_server.ServerId, startUtc, endUtc);
        var spinlockTrendTask = _dataService.GetSpinlockStatsTrendAsync(_server.ServerId, startUtc, endUtc);
        var spinlockSnapshotTask = _dataService.GetSpinlockStatsSnapshotAsync(_server.ServerId, startUtc, endUtc);

        await Task.WhenAll(latchTrendTask, latchSnapshotTask, spinlockTrendTask, spinlockSnapshotTask);

        RenderLatchStatsChart(latchTrendTask.Result);
        LatchStatsGrid.ItemsSource = latchSnapshotTask.Result;
        RenderSpinlockStatsChart(spinlockTrendTask.Result);
        SpinlockStatsGrid.ItemsSource = spinlockSnapshotTask.Result;
    }

    private void RenderLatchStatsChart(List<LatchStatsTrendPoint> data)
    {
        ClearChart(LatchStatsChart);
        _latchStatsHover?.Clear();
        ApplyTheme(LatchStatsChart);

        var (startUtc, endUtc) = GetWindowUtc();
        LatchStatsChart.Plot.YLabel("Wait Time (ms/sec)");

        if (data.Count == 0)
        {
            FinishContentionChart(LatchStatsChart, startUtc, endUtc, 0);
            return;
        }

        /* Order the series by total ms/sec desc so the heaviest latch gets the first palette color
           (deterministic, mirrors the Dashboard's re-group-by-total). */
        var byClass = data
            .GroupBy(d => d.LatchClass)
            .OrderByDescending(g => g.Sum(d => d.WaitTimeMsPerSecond))
            .ToList();

        double globalMax = 0;
        int colorIdx = 0;
        foreach (var group in byClass)
        {
            var points = group.OrderBy(d => d.CollectionTime).ToList();
            var times = points.Select(d => ViewerDataService.ToLocalTime(d.CollectionTime).ToOADate()).ToArray();
            var values = points.Select(d => d.WaitTimeMsPerSecond).ToArray();

            var plot = LatchStatsChart.Plot.Add.Scatter(times, values);
            plot.LegendText = TruncateName(group.Key);
            plot.Color = ScottPlot.Color.FromHex(SeriesColors[colorIdx % SeriesColors.Length]);
            ChartStyle.StyleScatter(plot);
            _latchStatsHover?.Add(plot, group.Key);
            colorIdx++;

            if (values.Length > 0) globalMax = Math.Max(globalMax, values.Max());
        }

        FinishContentionChart(LatchStatsChart, startUtc, endUtc, globalMax);
    }

    private void RenderSpinlockStatsChart(List<SpinlockStatsTrendPoint> data)
    {
        ClearChart(SpinlockStatsChart);
        _spinlockStatsHover?.Clear();
        ApplyTheme(SpinlockStatsChart);

        var (startUtc, endUtc) = GetWindowUtc();
        SpinlockStatsChart.Plot.YLabel("Collisions/sec");

        if (data.Count == 0)
        {
            FinishContentionChart(SpinlockStatsChart, startUtc, endUtc, 0);
            return;
        }

        var byName = data
            .GroupBy(d => d.SpinlockName)
            .OrderByDescending(g => g.Sum(d => d.CollisionsPerSecond))
            .ToList();

        double globalMax = 0;
        int colorIdx = 0;
        foreach (var group in byName)
        {
            var points = group.OrderBy(d => d.CollectionTime).ToList();
            var times = points.Select(d => ViewerDataService.ToLocalTime(d.CollectionTime).ToOADate()).ToArray();
            var values = points.Select(d => d.CollisionsPerSecond).ToArray();

            var plot = SpinlockStatsChart.Plot.Add.Scatter(times, values);
            plot.LegendText = TruncateName(group.Key);
            plot.Color = ScottPlot.Color.FromHex(SeriesColors[colorIdx % SeriesColors.Length]);
            ChartStyle.StyleScatter(plot);
            _spinlockStatsHover?.Add(plot, group.Key);
            colorIdx++;

            if (values.Length > 0) globalMax = Math.Max(globalMax, values.Max());
        }

        FinishContentionChart(SpinlockStatsChart, startUtc, endUtc, globalMax);
    }

    /// <summary>Shared chart finish for the latch/spinlock trends: date axis, window X-limits, Y floor at
    /// 0 with legend padding, and the legend — the one place the window bounds + Y-floor fix apply.</summary>
    private void FinishContentionChart(ScottPlot.WPF.WpfPlot chart, DateTime startUtc, DateTime endUtc, double globalMax)
    {
        chart.Plot.Axes.DateTimeTicksBottomDateChange();
        var rangeStart = ViewerDataService.ToLocalTime(startUtc);
        var rangeEnd = ViewerDataService.ToLocalTime(endUtc);
        chart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
        ReapplyAxisColors(chart);
        SetChartYLimitsWithLegendPadding(chart, 0, globalMax > 0 ? globalMax : 10);
        ShowChartLegend(chart);
        chart.Refresh();
    }

    /// <summary>Legend names for latch classes / spinlock names get long; clip to 20 chars + ellipsis
    /// exactly like the Dashboard's Latch/Spinlock chart legends.</summary>
    private static string TruncateName(string name)
        => name.Length > 20 ? name.Substring(0, 20) + "..." : name;

    /// <summary>Tears down the latch/spinlock hover helpers (mirrors the other tabs' dispose) so their
    /// tooltip popups + chart event handlers don't outlive a closed server tab.</summary>
    public void DisposeLatchSpinlockHelpers()
    {
        _latchStatsHover?.Dispose();
        _spinlockStatsHover?.Dispose();
    }
}
