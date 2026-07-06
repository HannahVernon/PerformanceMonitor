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
using System.Linq;
using System.Threading.Tasks;
using PerformanceMonitor.Common;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Plan Cache sub-tab (under the Memory tab, matching the Dashboard's Memory &gt; Plan Cache) — the
/// Darling-viewer surface for the plan_cache_stats collector. The trend chart plots single-use vs
/// multi-use plan-cache size (MB) over the settable window (the single-use bloat signal, the Dashboard's
/// Plan Cache chart shape), the summary strip shows total plans + oldest-plan age, and the latest-snapshot
/// grid breaks the cache down per (cacheobjtype, objtype) group. Loaded from <c>LoadMemoryAsync</c>
/// alongside the other Memory sub-tabs (the Memory tab's full-refresh branch). Chart chrome flows through
/// the shared <see cref="ChartStyle"/> / <see cref="ChartPalette"/> and the ChartHelpers bridge, so the
/// Y-floor-at-0 fix applies, and the two series ride the same palette keys the Dashboard uses for cross-app
/// color identity.
/// </summary>
public partial class ViewerServerTab
{
    private ChartHoverHelper? _planCacheHover;

    /// <summary>Applies the shared chrome + hover to the plan-cache chart up front (constructor).</summary>
    private void InitializePlanCacheChart()
    {
        ApplyTheme(PlanCacheChart);
        PlanCacheChart.Refresh();
        _planCacheHover = new ChartHoverHelper(PlanCacheChart, "MB");
    }

    /// <summary>
    /// Loads the Plan Cache sub-tab over the toolbar's settable window: the size trend and the latest
    /// composition snapshot read concurrently, then the chart, summary strip, and grid render. Called from
    /// <see cref="LoadMemoryAsync"/> (the Memory tab loads all its sub-tabs on activation).
    /// </summary>
    private async Task LoadPlanCacheAsync()
    {
        var (startUtc, endUtc) = GetWindowUtc();

        var trendTask = _dataService.GetPlanCacheTrendAsync(_server.ServerId, startUtc, endUtc);
        var snapshotTask = _dataService.GetPlanCacheSnapshotAsync(_server.ServerId, startUtc, endUtc);
        await Task.WhenAll(trendTask, snapshotTask);

        RenderPlanCacheChart(trendTask.Result);

        var snapshot = snapshotTask.Result;
        PlanCacheCompositionGrid.ItemsSource = snapshot;
        RenderPlanCacheSummary(snapshot);
    }

    private void RenderPlanCacheChart(List<PlanCacheTrendPoint> data)
    {
        ClearChart(PlanCacheChart);
        _planCacheHover?.Clear();
        ApplyTheme(PlanCacheChart);

        var (startUtc, endUtc) = GetWindowUtc();
        PlanCacheChart.Plot.YLabel("Plan Cache Size (MB)");

        double globalMax = 0;
        if (data.Count > 0)
        {
            var times = data.Select(d => ViewerDataService.ToLocalTime(d.CollectionTime).ToOADate()).ToArray();

            var singleUse = data.Select(d => d.SingleUseSizeMb).ToArray();
            var singlePlot = PlanCacheChart.Plot.Add.Scatter(times, singleUse);
            singlePlot.LegendText = "Single-Use";
            singlePlot.Color = ScottPlot.Color.FromHex(ChartPalette.SeriesColor("SinglePagePlans"));
            ChartStyle.StyleScatter(singlePlot);
            _planCacheHover?.Add(singlePlot, "Single-Use");

            var multiUse = data.Select(d => d.MultiUseSizeMb).ToArray();
            var multiPlot = PlanCacheChart.Plot.Add.Scatter(times, multiUse);
            multiPlot.LegendText = "Multi-Use";
            multiPlot.Color = ScottPlot.Color.FromHex(ChartPalette.SeriesColor("MultiPagePlans"));
            ChartStyle.StyleScatter(multiPlot);
            _planCacheHover?.Add(multiPlot, "Multi-Use");

            globalMax = Math.Max(singleUse.DefaultIfEmpty(0).Max(), multiUse.DefaultIfEmpty(0).Max());
        }

        PlanCacheChart.Plot.Axes.DateTimeTicksBottomDateChange();
        var rangeStart = ViewerDataService.ToLocalTime(startUtc);
        var rangeEnd = ViewerDataService.ToLocalTime(endUtc);
        PlanCacheChart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
        ReapplyAxisColors(PlanCacheChart);
        SetChartYLimitsWithLegendPadding(PlanCacheChart, 0, globalMax > 0 ? globalMax : 10);
        ShowChartLegend(PlanCacheChart);
        PlanCacheChart.Refresh();
    }

    /// <summary>The summary strip: total plans at the latest snapshot + the oldest cached plan's age
    /// (a plan-cache-stability signal — older = more stable), mirroring the Dashboard's Plan Cache summary.</summary>
    private void RenderPlanCacheSummary(List<PlanCacheSnapshotRow> snapshot)
    {
        if (snapshot.Count == 0)
        {
            PlanCacheTotalPlansText.Text = "--";
            PlanCacheOldestPlanText.Text = "--";
            return;
        }

        int totalPlans = snapshot.Sum(r => r.TotalPlans);
        PlanCacheTotalPlansText.Text = totalPlans.ToString("N0", CultureInfo.CurrentCulture);

        /* oldest_plan_create_time is the store-wide MIN the collector stamps on every group row, so any
           non-null row carries it. It comes from a DMV (sys.dm_exec_query_stats.creation_time) in the
           monitored server's local clock, which the viewer — having no per-server offset — measures against
           UtcNow: exact for a UTC server, off by the server's offset otherwise (the same approximation the
           viewer already accepts for server-local sample_time; a reliable de-skew is deferred). Age is a
           coarse d/h/m stability bucket, so a few hours' offset rarely changes the qualitative read. */
        var oldest = snapshot
            .Where(r => r.OldestPlanCreateTime.HasValue)
            .Select(r => r.OldestPlanCreateTime!.Value)
            .DefaultIfEmpty()
            .Min();

        if (oldest == default)
        {
            PlanCacheOldestPlanText.Text = "--";
            return;
        }

        var age = DateTime.UtcNow - DateTime.SpecifyKind(oldest, DateTimeKind.Utc);
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        PlanCacheOldestPlanText.Text = age.TotalDays >= 1
            ? $"{(int)age.TotalDays}d {age.Hours}h"
            : age.TotalHours >= 1
                ? $"{age.Hours}h {age.Minutes}m"
                : $"{age.Minutes}m";
    }

    /// <summary>Tears down the plan-cache hover helper (mirrors the other tabs' dispose).</summary>
    public void DisposePlanCacheHelpers()
    {
        _planCacheHover?.Dispose();
    }
}
