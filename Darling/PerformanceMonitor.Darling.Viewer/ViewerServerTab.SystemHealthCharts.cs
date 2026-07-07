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
/// The System Events tab's two SYSTEM-component counter-chart sub-tabs — Corruption Events and Contention
/// Events — the faithful reproduction of the sibling Dashboard's <c>SystemEventsContent</c> Corruption /
/// Contention 2x2 chart grids (<c>LoadCorruptionEventsCharts</c> / <c>LoadContentionEventsCharts</c>) in
/// the viewer's chart idiom. Both sub-tabs read the ONE SYSTEM-component feed
/// (<see cref="ViewerDataService.GetSystemHealthAsync"/>) and select their own columns, so a single load
/// renders all eight charts — exactly as the Dashboard's <c>RefreshSystemHealthAsync</c> feeds both tabs.
/// Unlike the grid categories these plot EVERY collected snapshot over time (no significance filter);
/// counts floor at 0 and the time axis is bound to the toolbar's settable window
/// (<see cref="ViewerServerTab.GetWindowUtc"/>). Chrome / line polish / colors flow through the shared
/// <see cref="ChartStyle"/> / <see cref="ChartPalette"/> and the <c>ViewerServerTab.ChartHelpers.cs</c>
/// bridge, matching the CPU / Latch-Spinlock chart tabs; hover tooltips are kept (no per-chart context
/// menu, consistent with the viewer's other charts).
/// </summary>
public partial class ViewerServerTab
{
    private ChartHoverHelper? _badPagesHover;
    private ChartHoverHelper? _dumpRequestsHover;
    private ChartHoverHelper? _accessViolationsHover;
    private ChartHoverHelper? _writeAccessViolationsHover;
    private ChartHoverHelper? _nonYieldingTasksHover;
    private ChartHoverHelper? _latchWarningsHover;
    private ChartHoverHelper? _sickSpinlocksHover;
    private ChartHoverHelper? _cpuComparisonHover;

    /// <summary>Applies the shared chrome + hover to the eight Corruption/Contention charts up front
    /// (constructor), so they don't flash white before the tab's first load — matching the CPU/Latch charts.
    /// Hover units mirror the Dashboard's (events / backoffs / %).</summary>
    private void InitializeSystemHealthCharts()
    {
        foreach (var chart in new[]
        {
            BadPagesChart, DumpRequestsChart, AccessViolationsChart, WriteAccessViolationsChart,
            NonYieldingTasksChart, LatchWarningsChart, SickSpinlocksChart, CpuComparisonChart,
        })
        {
            ApplyTheme(chart);
            chart.Refresh();
        }

        _badPagesHover = new ChartHoverHelper(BadPagesChart, "events");
        _dumpRequestsHover = new ChartHoverHelper(DumpRequestsChart, "events");
        _accessViolationsHover = new ChartHoverHelper(AccessViolationsChart, "events");
        _writeAccessViolationsHover = new ChartHoverHelper(WriteAccessViolationsChart, "events");
        _nonYieldingTasksHover = new ChartHoverHelper(NonYieldingTasksChart, "events");
        _latchWarningsHover = new ChartHoverHelper(LatchWarningsChart, "events");
        _sickSpinlocksHover = new ChartHoverHelper(SickSpinlocksChart, "backoffs");
        _cpuComparisonHover = new ChartHoverHelper(CpuComparisonChart, "%");
    }

    /// <summary>
    /// Loads both Corruption + Contention chart sub-tabs from a single SYSTEM-component read over the
    /// toolbar's settable window (the reads are already bounded on both ends, so no client-side end-trim
    /// is needed unlike the start-only CPU/tempdb reads). Mirrors the Dashboard's one-refresh-feeds-both.
    /// </summary>
    private async Task LoadSystemHealthChartsAsync()
    {
        var (startUtc, endUtc) = GetWindowUtc();
        var data = await _dataService.GetSystemHealthAsync(_server.ServerId, startUtc, endUtc);

        double xMin = ViewerTimeHelper.ForDisplay(startUtc).ToOADate();
        double xMax = ViewerTimeHelper.ForDisplay(endUtc).ToOADate();

        // Corruption Events (2x2).
        RenderCounterChart(BadPagesChart, _badPagesHover, data, d => d.BadPagesDetected, "Bad Pages",
            ChartPalette.CyclingColor(3), xMin, xMax);
        RenderCounterChart(DumpRequestsChart, _dumpRequestsHover, data, d => d.IntervalDumpRequests, "Dump Requests",
            ChartPalette.CyclingColor(2), xMin, xMax);
        RenderCounterChart(AccessViolationsChart, _accessViolationsHover, data, d => d.IsAccessViolationOccurred, "Access Violations",
            ChartPalette.CyclingColor(4), xMin, xMax);
        RenderCounterChart(WriteAccessViolationsChart, _writeAccessViolationsHover, data, d => d.WriteAccessViolationCount, "Write Access Violations",
            ChartPalette.CyclingColor(0), xMin, xMax);

        // Contention Events (2x2).
        RenderCounterChart(NonYieldingTasksChart, _nonYieldingTasksHover, data, d => d.NonYieldingTasksReported, "Non-Yielding Tasks",
            ChartPalette.CyclingColor(3), xMin, xMax);
        RenderCounterChart(LatchWarningsChart, _latchWarningsHover, data, d => d.LatchWarnings, "Latch Warnings",
            ChartPalette.CyclingColor(2), xMin, xMax);
        RenderSickSpinlocksChart(data, xMin, xMax);
        RenderCpuComparisonChart(data, xMin, xMax);
    }

    /// <summary>
    /// One single-series counter chart (Bad Pages / Dump Requests / Access Violations / Write AV /
    /// Non-Yielding Tasks / Latch Warnings): a scatter of the selected counter over event time, Y floored at
    /// 0, no legend. Empty window shows the Dashboard's centered "No data" text. <paramref name="selector"/>
    /// null-coalesces to 0 so a snapshot missing the attribute plots a zero, never a gap.
    /// </summary>
    private void RenderCounterChart(
        ScottPlot.WPF.WpfPlot chart, ChartHoverHelper? hover, List<SystemHealthRecord> data,
        Func<SystemHealthRecord, long?> selector, string label, string colorHex, double xMin, double xMax)
    {
        ClearChart(chart);
        hover?.Clear();
        ApplyTheme(chart);

        double max = 0;
        if (data.Count == 0)
        {
            AddNoDataText(chart, xMin, xMax);
        }
        else
        {
            var xs = data.Select(d => ViewerTimeHelper.ForDisplay(d.EventTime!.Value).ToOADate()).ToArray();
            var ys = data.Select(d => (double)(selector(d) ?? 0)).ToArray();

            var scatter = chart.Plot.Add.Scatter(xs, ys);
            scatter.Color = ScottPlot.Color.FromHex(colorHex);
            ChartStyle.StyleScatter(scatter);
            hover?.Add(scatter, label);

            max = ys.Length > 0 ? ys.Max() : 0;
        }

        chart.Plot.Axes.DateTimeTicksBottomDateChange();
        ReapplyAxisColors(chart);
        chart.Plot.Axes.SetLimitsX(xMin, xMax);
        /* The tiles are borderless (no in-panel title), so the descriptive Y-axis label is the chart's
           identifier — the same convention the File I/O charts use ("Read Latency (ms)"). */
        chart.Plot.YLabel(label);
        chart.Plot.Axes.SetLimitsY(0, max > 0 ? max * 1.15 : 1);
        chart.Refresh();
    }

    /// <summary>
    /// Sick Spinlocks by Type: one series per distinct <c>sickSpinlockType</c> (top 5, to bound clutter)
    /// plotting the spinlock backoffs over event time, with a bottom legend — mirrors the Dashboard's
    /// grouped multi-series chart (backoffs, or 1 when the count is absent).
    /// </summary>
    private void RenderSickSpinlocksChart(List<SystemHealthRecord> data, double xMin, double xMax)
    {
        ClearChart(SickSpinlocksChart);
        _sickSpinlocksHover?.Clear();
        ApplyTheme(SickSpinlocksChart);

        double max = 0;
        if (data.Count == 0)
        {
            AddNoDataText(SickSpinlocksChart, xMin, xMax);
        }
        else
        {
            var spinlockTypes = data
                .Where(d => !string.IsNullOrEmpty(d.SickSpinlockType))
                .Select(d => d.SickSpinlockType)
                .Distinct()
                .Take(5)
                .ToList();

            int colorIdx = 0;
            foreach (var spinlockType in spinlockTypes)
            {
                var typeData = data.Where(d => d.SickSpinlockType == spinlockType).ToList();
                if (typeData.Count == 0)
                    continue;

                var xs = typeData.Select(d => ViewerTimeHelper.ForDisplay(d.EventTime!.Value).ToOADate()).ToArray();
                var ys = typeData.Select(d => (double)(d.SpinlockBackoffs ?? 1)).ToArray();

                var scatter = SickSpinlocksChart.Plot.Add.Scatter(xs, ys);
                scatter.Color = ScottPlot.Color.FromHex(SeriesColors[colorIdx % SeriesColors.Length]);
                ChartStyle.StyleScatter(scatter);
                scatter.LegendText = spinlockType ?? "Unknown";
                _sickSpinlocksHover?.Add(scatter, spinlockType ?? "Unknown");
                colorIdx++;

                if (ys.Length > 0)
                    max = Math.Max(max, ys.Max());
            }

            if (spinlockTypes.Count > 0)
                ShowChartLegend(SickSpinlocksChart);
        }

        SickSpinlocksChart.Plot.Axes.DateTimeTicksBottomDateChange();
        ReapplyAxisColors(SickSpinlocksChart);
        SickSpinlocksChart.Plot.Axes.SetLimitsX(xMin, xMax);
        SickSpinlocksChart.Plot.YLabel("Sick Spinlocks (backoffs)");
        SetChartYLimitsWithLegendPadding(SickSpinlocksChart, 0, max);
        SickSpinlocksChart.Refresh();
    }

    /// <summary>
    /// SQL CPU vs System CPU: two percent series over event time on a fixed 0-100 axis with a bottom
    /// legend — the Dashboard's CPU-comparison chart (System CPU on the cycling accent, SQL CPU on the
    /// shared SqlCpu identity color).
    /// </summary>
    private void RenderCpuComparisonChart(List<SystemHealthRecord> data, double xMin, double xMax)
    {
        ClearChart(CpuComparisonChart);
        _cpuComparisonHover?.Clear();
        ApplyTheme(CpuComparisonChart);

        if (data.Count == 0)
        {
            AddNoDataText(CpuComparisonChart, xMin, xMax);
        }
        else
        {
            var xs = data.Select(d => ViewerTimeHelper.ForDisplay(d.EventTime!.Value).ToOADate()).ToArray();

            var sysScatter = CpuComparisonChart.Plot.Add.Scatter(xs, data.Select(d => (double)(d.SystemCpuUtilization ?? 0)).ToArray());
            sysScatter.Color = ScottPlot.Color.FromHex(ChartPalette.CyclingColor(0));
            ChartStyle.StyleScatter(sysScatter);
            sysScatter.LegendText = "System CPU %";
            _cpuComparisonHover?.Add(sysScatter, "System CPU %");

            var sqlScatter = CpuComparisonChart.Plot.Add.Scatter(xs, data.Select(d => (double)(d.SqlCpuUtilization ?? 0)).ToArray());
            sqlScatter.Color = ScottPlot.Color.FromHex(ChartPalette.SeriesColor("SqlCpu"));
            ChartStyle.StyleScatter(sqlScatter);
            sqlScatter.LegendText = "SQL CPU %";
            _cpuComparisonHover?.Add(sqlScatter, "SQL CPU %");

            ShowChartLegend(CpuComparisonChart);
        }

        CpuComparisonChart.Plot.Axes.DateTimeTicksBottomDateChange();
        ReapplyAxisColors(CpuComparisonChart);
        CpuComparisonChart.Plot.Axes.SetLimitsX(xMin, xMax);
        CpuComparisonChart.Plot.YLabel("CPU %");
        CpuComparisonChart.Plot.Axes.SetLimitsY(0, 100);
        CpuComparisonChart.Refresh();
    }

    /// <summary>Centered "No data for selected time range" annotation for an empty counter chart,
    /// matching the Dashboard's no-data placeholder text (placeholder accent color).</summary>
    private static void AddNoDataText(ScottPlot.WPF.WpfPlot chart, double xMin, double xMax)
    {
        double xCenter = xMin + (xMax - xMin) / 2;
        var text = chart.Plot.Add.Text("No data for selected time range", xCenter, 0.5);
        text.LabelFontSize = 14;
        text.LabelFontColor = ScottPlot.Color.FromHex(ChartPalette.AccentColor("Placeholder"));
        text.LabelAlignment = ScottPlot.Alignment.MiddleCenter;
    }

    /// <summary>Tears down the eight Corruption/Contention hover helpers (mirrors the other tabs' dispose)
    /// so their tooltip popups + chart event handlers don't outlive a closed server tab.</summary>
    public void DisposeSystemHealthChartHelpers()
    {
        _badPagesHover?.Dispose();
        _dumpRequestsHover?.Dispose();
        _accessViolationsHover?.Dispose();
        _writeAccessViolationsHover?.Dispose();
        _nonYieldingTasksHover?.Dispose();
        _latchWarningsHover?.Dispose();
        _sickSpinlocksHover?.Dispose();
        _cpuComparisonHover?.Dispose();
    }
}
