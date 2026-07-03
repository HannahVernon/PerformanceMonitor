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
using System.Windows;
using System.Windows.Controls;
using PerformanceMonitor.Common;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Blocking inner tab — upgraded to Lite's sub-tab structure (ServerTab.xaml:1435-1469): "Trends"
/// (lock-wait rate, blocking incidents, deadlocks) and "Current Waits" (waiting-task duration by wait
/// type, blocked sessions by database) precede the existing "Blocked Process Reports" grid, which is
/// re-hosted unchanged as the third sub-tab. The five trend-chart bodies are COPIES of Lite's
/// <c>ServerTab.Charts.cs</c> Update* methods, reads rewired to <see cref="ViewerDataService"/> Postgres.
/// Two render-body deviations from Lite: (1) the time axis runs through
/// <see cref="ViewerDataService.ToLocalTime"/> instead of Lite's per-server <c>UtcOffsetMinutes</c> shift;
/// (2) the viewer's fixed 24-hour window supplies the X-axis range (Lite's hoursBack / custom-range
/// parameters are dropped). The spike-plot / zero-line-when-empty shapes, the fixed
/// <see cref="ChartPalette.SeriesColor"/> identities for the single-series charts, and the cycling
/// <see cref="ChartPalette"/> colors for the multi-series charts are preserved. W1e adds Lite's full
/// Blocked Process Reports grid (widened + slicer + block-chain viewer) and the Deadlocks sub-tab
/// (deadlock-graph viewer); chart drill-downs remain deferred (no Active Queries surface yet).
/// </summary>
public partial class ViewerServerTab
{
    private ChartHoverHelper? _lockWaitTrendHover;
    private ChartHoverHelper? _blockingTrendHover;
    private ChartHoverHelper? _deadlockTrendHover;
    private ChartHoverHelper? _currentWaitsDurationHover;
    private ChartHoverHelper? _currentWaitsBlockedHover;

    /// <summary>
    /// Applies the shared chrome to the five Blocking trend charts and wires their hover tooltips
    /// (Lite's per-chart units). Called from the constructor after <c>InitializeComponent</c> so the
    /// charts don't flash white before the tab's first load.
    /// </summary>
    private void InitializeBlockingCharts()
    {
        ApplyTheme(LockWaitTrendChart);
        LockWaitTrendChart.Refresh();
        ApplyTheme(BlockingTrendChart);
        BlockingTrendChart.Refresh();
        ApplyTheme(DeadlockTrendChart);
        DeadlockTrendChart.Refresh();
        ApplyTheme(CurrentWaitsDurationChart);
        CurrentWaitsDurationChart.Refresh();
        ApplyTheme(CurrentWaitsBlockedChart);
        CurrentWaitsBlockedChart.Refresh();

        _lockWaitTrendHover = new ChartHoverHelper(LockWaitTrendChart, "ms/sec");
        _blockingTrendHover = new ChartHoverHelper(BlockingTrendChart, "incidents");
        _deadlockTrendHover = new ChartHoverHelper(DeadlockTrendChart, "deadlocks");
        _currentWaitsDurationHover = new ChartHoverHelper(CurrentWaitsDurationChart, "ms");
        _currentWaitsBlockedHover = new ChartHoverHelper(CurrentWaitsBlockedChart, "sessions");

        /* The Blocked Process Reports + Deadlocks sub-tabs each carry a UTC slicer; dragging it re-reads
           its grid over the selection (Lite's OnBlockingSlicerChanged / OnDeadlockSlicerChanged). */
        BlockingSlicer.RangeChanged += OnBlockingSlicerChanged;
        DeadlockSlicer.RangeChanged += OnDeadlockSlicerChanged;
    }

    /// <summary>
    /// A Blocking sub-tab switch reloads through the shell's overlap-guarded
    /// <see cref="RefreshActiveInnerTabAsync"/> (the Blocking tab is the active inner tab whenever its
    /// sub-tabs are visible, so the loader dispatches to <see cref="LoadBlockingAsync"/> and loads only
    /// the newly-visible sub-tab). Gated on <see cref="System.Windows.FrameworkElement.IsLoaded"/> and
    /// the sub-TabControl's own selection so build-time and bubbled selections are ignored.
    /// </summary>
    private async void BlockingSubTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, BlockingSubTabs) || !IsLoaded)
        {
            return;
        }

        await RefreshActiveInnerTabAsync();
    }

    /// <summary>
    /// Loads the Blocking tab's ACTIVE sub-tab only (mirrors Lite's subTabOnly gating): Trends reads the
    /// three trend series concurrently, Current Waits reads the two current-waits series concurrently,
    /// and Blocked Process Reports reads the XE-with-DMV-fallback grid. All window on the fixed 24-hour
    /// range.
    /// </summary>
    private async Task LoadBlockingAsync()
    {
        var endUtc = DateTime.UtcNow;
        var startUtc = endUtc - s_dataWindow;

        switch (BlockingSubTabs.SelectedIndex)
        {
            case 0: // Trends
                var lockWaitTask = _dataService.GetLockWaitTrendAsync(_server.ServerId, startUtc, endUtc);
                var blockingTask = _dataService.GetBlockingTrendAsync(_server.ServerId, startUtc, endUtc);
                var deadlockTask = _dataService.GetDeadlockTrendAsync(_server.ServerId, startUtc, endUtc);
                var lockWaits = await lockWaitTask;
                var blocking = await blockingTask;
                var deadlocks = await deadlockTask;
                RenderLockWaitTrendChart(lockWaits);
                RenderBlockingTrendChart(blocking);
                RenderDeadlockTrendChart(deadlocks);
                break;
            case 1: // Current Waits
                var durationTask = _dataService.GetWaitingTaskTrendAsync(_server.ServerId, startUtc, endUtc);
                var blockedTask = _dataService.GetBlockedSessionTrendAsync(_server.ServerId, startUtc, endUtc);
                var duration = await durationTask;
                var blocked = await blockedTask;
                RenderCurrentWaitsDurationChart(duration);
                RenderCurrentWaitsBlockedChart(blocked);
                break;
            case 2: // Blocked Process Reports
                await LoadBlockedProcessReportsAsync(startUtc, endUtc);
                break;
            case 3: // Deadlocks
            default:
                await LoadDeadlocksAsync(startUtc, endUtc);
                break;
        }
    }

    /// <summary>
    /// Loads the Blocked Process Reports sub-tab (W1e Lite parity): the widened XE-preferred / DMV-fallback
    /// grid bound through the filter manager (so active column filters survive the refresh) plus the UTC
    /// slicer over the same window. The grid reads the full 24-hour window; dragging the slicer re-reads it
    /// over the selection (<see cref="OnBlockingSlicerChanged"/>). The Npgsql reads are genuinely async, so
    /// unlike Lite's DuckDB path there is no Task.Run wrap.
    /// </summary>
    private async Task LoadBlockedProcessReportsAsync(DateTime startUtc, DateTime endUtc)
    {
        var rows = await _dataService.GetRecentBlockedProcessReportsAsync(_server.ServerId, startUtc, endUtc);
        _blockedProcessFilterMgr!.UpdateData(rows);
        await LoadBlockingSlicerAsync(startUtc, endUtc);
    }

    /// <summary>
    /// Loads the Deadlocks sub-tab (W1e Lite parity): reads the recent deadlock events, parses each graph
    /// into per-process detail rows OFF the UI thread (Lite's #1193 fix — the graph walk is CPU-bound XML
    /// work), binds them through the filter manager, and loads the UTC slicer.
    /// </summary>
    private async Task LoadDeadlocksAsync(DateTime startUtc, DateTime endUtc)
    {
        var rows = await _dataService.GetRecentDeadlocksAsync(_server.ServerId, startUtc, endUtc);
        var details = await ParseDeadlocksOffUiThreadAsync(rows);
        _deadlockFilterMgr!.UpdateData(details);
        await LoadDeadlockSlicerAsync(startUtc, endUtc);
    }

    // ── Blocking / Deadlock slicers (W1e) ──

    private string _blockingSlicerMetric = "Events";
    private List<TimeSliceBucket>? _blockingSlicerData;
    private List<TimeSliceBucket>? _deadlockSlicerData;

    private async Task LoadBlockingSlicerAsync(DateTime startUtc, DateTime endUtc)
    {
        var data = await _dataService.GetBlockingSlicerDataAsync(_server.ServerId, startUtc, endUtc);
        _blockingSlicerData = data;
        _blockingSlicerMetric = "Events";
        if (data.Count > 0)
            BlockingSlicer.LoadData(data, "Blocking Events", startUtc, endUtc);
    }

    private async Task LoadDeadlockSlicerAsync(DateTime startUtc, DateTime endUtc)
    {
        var data = await _dataService.GetDeadlockSlicerDataAsync(_server.ServerId, startUtc, endUtc);
        _deadlockSlicerData = data;
        if (data.Count > 0)
            DeadlockSlicer.LoadData(data, "Deadlocks", startUtc, endUtc);
    }

    /// <summary>The slicer sends UTC bounds; the viewer's reads take naive UTC directly (no clock shift).</summary>
    private async void OnBlockingSlicerChanged(object? sender, SlicerRangeEventArgs e)
    {
        try
        {
            var rows = await _dataService.GetRecentBlockedProcessReportsAsync(_server.ServerId, e.StartUtc, e.EndUtc);
            _blockedProcessFilterMgr!.UpdateData(rows);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"blocking slicer failed: {ex.Message}");
        }
    }

    private async void OnDeadlockSlicerChanged(object? sender, SlicerRangeEventArgs e)
    {
        try
        {
            var rows = await _dataService.GetRecentDeadlocksAsync(_server.ServerId, e.StartUtc, e.EndUtc);
            _deadlockFilterMgr!.UpdateData(await ParseDeadlocksOffUiThreadAsync(rows));
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"deadlock slicer failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-metrics the blocking slicer overlay to the sorted column (Lite's BlockedProcessReportGrid_Sorting):
    /// sorting the grid by wait / blocker / blocked / database swaps the slicer's aggregate curve to match.
    /// </summary>
    private void BlockedProcessReportGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (_blockingSlicerData == null || _blockingSlicerData.Count == 0) return;

        var col = e.Column.SortMemberPath ?? "";
        if (string.IsNullOrEmpty(col))
        {
            if (e.Column is DataGridBoundColumn bc && bc.Binding is System.Windows.Data.Binding b)
                col = b.Path.Path;
        }
        var (metric, label) = col switch
        {
            "WaitTimeMs" => ("TotalCpu", "Total Wait (sec)"),
            "BlockingSpid" => ("TotalElapsed", "Distinct Blockers"),
            "BlockedSpid" => ("TotalReads", "Distinct Blocked"),
            "DatabaseName" => ("TotalLogicalReads", "Distinct Databases"),
            _ => ("Events", "Blocking Events"),
        };

        if (metric == _blockingSlicerMetric) return;
        _blockingSlicerMetric = metric;

        foreach (var bucket in _blockingSlicerData)
        {
            bucket.Value = metric switch
            {
                "TotalCpu" => bucket.TotalCpu,
                "TotalElapsed" => bucket.TotalElapsed,
                "TotalReads" => bucket.TotalReads,
                "TotalLogicalReads" => bucket.TotalLogicalReads,
                _ => bucket.SessionCount,
            };
        }

        BlockingSlicer.UpdateMetric(label);
    }

    /* Parse each deadlock graph into its per-process rows on the thread pool — CPU-bound XML work that
       would hitch the dispatcher on the Blocking tab (Lite's #1193 fix). Only the grid bind stays on the UI. */
    private static Task<List<DeadlockProcessDetail>> ParseDeadlocksOffUiThreadAsync(List<ViewerDeadlockRow> rows)
        => Task.Run(() => DeadlockProcessDetail.ParseFromRows(rows));

    private void RenderLockWaitTrendChart(List<LockWaitTrendPoint> data)
    {
        ClearChart(LockWaitTrendChart);
        ApplyTheme(LockWaitTrendChart);

        var rangeStart = ViewerDataService.ToLocalTime(DateTime.UtcNow - s_dataWindow);
        var rangeEnd = ViewerDataService.ToLocalTime(DateTime.UtcNow);

        _lockWaitTrendHover?.Clear();
        if (data.Count == 0)
        {
            var zeroLine = LockWaitTrendChart.Plot.Add.Scatter(
                new[] { rangeStart.ToOADate(), rangeEnd.ToOADate() },
                new[] { 0.0, 0.0 });
            zeroLine.LegendText = "Lock Waits";
            zeroLine.Color = ScottPlot.Color.FromHex(ChartPalette.SeriesColor("LockWaits"));
            zeroLine.MarkerSize = 0;
            LockWaitTrendChart.Plot.Axes.DateTimeTicksBottomDateChange();
            LockWaitTrendChart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
            ReapplyAxisColors(LockWaitTrendChart);
            LockWaitTrendChart.Plot.YLabel("Lock Wait Time (ms/sec)");
            SetChartYLimitsWithLegendPadding(LockWaitTrendChart, 0, 1);
            ShowChartLegend(LockWaitTrendChart);
            LockWaitTrendChart.Refresh();
            return;
        }

        var grouped = data.GroupBy(d => d.WaitType).ToList();
        double globalMax = 0;

        for (int i = 0; i < grouped.Count; i++)
        {
            var group = grouped[i];
            var times = group.Select(t => ViewerDataService.ToLocalTime(t.CollectionTime).ToOADate()).ToArray();
            var values = group.Select(t => t.WaitTimeMsPerSecond).ToArray();

            var plot = LockWaitTrendChart.Plot.Add.Scatter(times, values);
            plot.LegendText = group.Key;
            plot.Color = ScottPlot.Color.FromHex(SeriesColors[i % SeriesColors.Length]);
            ChartStyle.StyleScatter(plot);
            _lockWaitTrendHover?.Add(plot, group.Key);

            if (values.Length > 0) globalMax = Math.Max(globalMax, values.Max());
        }

        LockWaitTrendChart.Plot.Axes.DateTimeTicksBottomDateChange();
        LockWaitTrendChart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
        ReapplyAxisColors(LockWaitTrendChart);
        LockWaitTrendChart.Plot.YLabel("Lock Wait Time (ms/sec)");
        SetChartYLimitsWithLegendPadding(LockWaitTrendChart, 0, globalMax > 0 ? globalMax : 1);
        ShowChartLegend(LockWaitTrendChart);
        LockWaitTrendChart.Refresh();
    }

    private void RenderBlockingTrendChart(List<BlockingTrendPoint> data)
    {
        ClearChart(BlockingTrendChart);
        ApplyTheme(BlockingTrendChart);

        var rangeStart = ViewerDataService.ToLocalTime(DateTime.UtcNow - s_dataWindow);
        var rangeEnd = ViewerDataService.ToLocalTime(DateTime.UtcNow);

        _blockingTrendHover?.Clear();
        if (data.Count == 0)
        {
            /* No blocking events — show a flat line at zero so the chart looks active */
            var zeroLine = BlockingTrendChart.Plot.Add.Scatter(
                new[] { rangeStart.ToOADate(), rangeEnd.ToOADate() },
                new[] { 0.0, 0.0 });
            zeroLine.LegendText = "Blocking Incidents";
            zeroLine.Color = ScottPlot.Color.FromHex(ChartPalette.SeriesColor("Blocking"));
            zeroLine.MarkerSize = 0;
            BlockingTrendChart.Plot.Axes.DateTimeTicksBottomDateChange();
            BlockingTrendChart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
            ReapplyAxisColors(BlockingTrendChart);
            BlockingTrendChart.Plot.YLabel("Blocking Incidents");
            SetChartYLimitsWithLegendPadding(BlockingTrendChart, 0, 1);
            ShowChartLegend(BlockingTrendChart);
            BlockingTrendChart.Refresh();
            return;
        }

        /* Build arrays with zero baseline between data points for spike effect */
        var expandedTimes = new List<double>();
        var expandedCounts = new List<double>();

        /* Add zero at start */
        expandedTimes.Add(rangeStart.ToOADate());
        expandedCounts.Add(0);

        foreach (var point in data.OrderBy(d => d.Time))
        {
            var time = ViewerDataService.ToLocalTime(point.Time).ToOADate();
            /* Go to zero just before the spike */
            expandedTimes.Add(time - 0.0001);
            expandedCounts.Add(0);
            /* Spike up */
            expandedTimes.Add(time);
            expandedCounts.Add(point.Count);
            /* Back to zero just after */
            expandedTimes.Add(time + 0.0001);
            expandedCounts.Add(0);
        }

        /* Add zero at end */
        expandedTimes.Add(rangeEnd.ToOADate());
        expandedCounts.Add(0);

        var plot = BlockingTrendChart.Plot.Add.Scatter(expandedTimes.ToArray(), expandedCounts.ToArray());
        plot.LegendText = "Blocking Incidents";
        plot.Color = ScottPlot.Color.FromHex(ChartPalette.SeriesColor("Blocking"));
        plot.MarkerSize = 0; /* No markers, just lines */
        _blockingTrendHover?.Add(plot, "Blocking Incidents");

        BlockingTrendChart.Plot.Axes.DateTimeTicksBottomDateChange();
        BlockingTrendChart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
        ReapplyAxisColors(BlockingTrendChart);
        BlockingTrendChart.Plot.YLabel("Blocking Incidents");
        SetChartYLimitsWithLegendPadding(BlockingTrendChart, 0, data.Max(d => d.Count));
        ShowChartLegend(BlockingTrendChart);
        BlockingTrendChart.Refresh();
    }

    private void RenderDeadlockTrendChart(List<BlockingTrendPoint> data)
    {
        ClearChart(DeadlockTrendChart);
        ApplyTheme(DeadlockTrendChart);

        var rangeStart = ViewerDataService.ToLocalTime(DateTime.UtcNow - s_dataWindow);
        var rangeEnd = ViewerDataService.ToLocalTime(DateTime.UtcNow);

        _deadlockTrendHover?.Clear();
        if (data.Count == 0)
        {
            /* No deadlocks — show a flat line at zero so the chart looks active */
            var zeroLine = DeadlockTrendChart.Plot.Add.Scatter(
                new[] { rangeStart.ToOADate(), rangeEnd.ToOADate() },
                new[] { 0.0, 0.0 });
            zeroLine.LegendText = "Deadlocks";
            zeroLine.Color = ScottPlot.Color.FromHex(ChartPalette.SeriesColor("Deadlocks"));
            zeroLine.MarkerSize = 0;
            DeadlockTrendChart.Plot.Axes.DateTimeTicksBottomDateChange();
            DeadlockTrendChart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
            ReapplyAxisColors(DeadlockTrendChart);
            DeadlockTrendChart.Plot.YLabel("Deadlocks");
            SetChartYLimitsWithLegendPadding(DeadlockTrendChart, 0, 1);
            ShowChartLegend(DeadlockTrendChart);
            DeadlockTrendChart.Refresh();
            return;
        }

        /* Build arrays with zero baseline between data points for spike effect */
        var expandedTimes = new List<double>();
        var expandedCounts = new List<double>();

        /* Add zero at start */
        expandedTimes.Add(rangeStart.ToOADate());
        expandedCounts.Add(0);

        foreach (var point in data.OrderBy(d => d.Time))
        {
            var time = ViewerDataService.ToLocalTime(point.Time).ToOADate();
            /* Go to zero just before the spike */
            expandedTimes.Add(time - 0.0001);
            expandedCounts.Add(0);
            /* Spike up */
            expandedTimes.Add(time);
            expandedCounts.Add(point.Count);
            /* Back to zero just after */
            expandedTimes.Add(time + 0.0001);
            expandedCounts.Add(0);
        }

        /* Add zero at end */
        expandedTimes.Add(rangeEnd.ToOADate());
        expandedCounts.Add(0);

        var plot = DeadlockTrendChart.Plot.Add.Scatter(expandedTimes.ToArray(), expandedCounts.ToArray());
        plot.LegendText = "Deadlocks";
        plot.Color = ScottPlot.Color.FromHex(ChartPalette.SeriesColor("Deadlocks"));
        plot.MarkerSize = 0; /* No markers, just lines */
        _deadlockTrendHover?.Add(plot, "Deadlocks");

        DeadlockTrendChart.Plot.Axes.DateTimeTicksBottomDateChange();
        DeadlockTrendChart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
        ReapplyAxisColors(DeadlockTrendChart);
        DeadlockTrendChart.Plot.YLabel("Deadlocks");
        SetChartYLimitsWithLegendPadding(DeadlockTrendChart, 0, data.Max(d => d.Count));
        ShowChartLegend(DeadlockTrendChart);
        DeadlockTrendChart.Refresh();
    }

    private void RenderCurrentWaitsDurationChart(List<WaitingTaskTrendPoint> data)
    {
        ClearChart(CurrentWaitsDurationChart);
        ApplyTheme(CurrentWaitsDurationChart);

        var rangeStart = ViewerDataService.ToLocalTime(DateTime.UtcNow - s_dataWindow);
        var rangeEnd = ViewerDataService.ToLocalTime(DateTime.UtcNow);

        _currentWaitsDurationHover?.Clear();
        if (data.Count == 0)
        {
            var zeroLine = CurrentWaitsDurationChart.Plot.Add.Scatter(
                new[] { rangeStart.ToOADate(), rangeEnd.ToOADate() },
                new[] { 0.0, 0.0 });
            zeroLine.LegendText = "Current Waits";
            zeroLine.Color = ScottPlot.Color.FromHex(ChartPalette.SeriesColor("CurrentWaits"));
            zeroLine.MarkerSize = 0;
            CurrentWaitsDurationChart.Plot.Axes.DateTimeTicksBottomDateChange();
            CurrentWaitsDurationChart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
            ReapplyAxisColors(CurrentWaitsDurationChart);
            CurrentWaitsDurationChart.Plot.YLabel("Total Wait Duration (ms)");
            SetChartYLimitsWithLegendPadding(CurrentWaitsDurationChart, 0, 1);
            ShowChartLegend(CurrentWaitsDurationChart);
            CurrentWaitsDurationChart.Refresh();
            return;
        }

        var grouped = data.GroupBy(d => d.WaitType).OrderBy(g => g.Key).ToList();
        double globalMax = 0;

        for (int i = 0; i < grouped.Count; i++)
        {
            var group = grouped[i];
            var ordered = group.OrderBy(t => t.CollectionTime).ToList();
            var times = ordered.Select(t => ViewerDataService.ToLocalTime(t.CollectionTime).ToOADate()).ToArray();
            var values = ordered.Select(t => (double)t.TotalWaitMs).ToArray();

            var plot = CurrentWaitsDurationChart.Plot.Add.Scatter(times, values);
            plot.LegendText = group.Key;
            plot.Color = ScottPlot.Color.FromHex(SeriesColors[i % SeriesColors.Length]);
            ChartStyle.StyleScatter(plot);
            _currentWaitsDurationHover?.Add(plot, group.Key);

            if (values.Length > 0) globalMax = Math.Max(globalMax, values.Max());
        }

        CurrentWaitsDurationChart.Plot.Axes.DateTimeTicksBottomDateChange();
        CurrentWaitsDurationChart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
        ReapplyAxisColors(CurrentWaitsDurationChart);
        CurrentWaitsDurationChart.Plot.YLabel("Total Wait Duration (ms)");
        SetChartYLimitsWithLegendPadding(CurrentWaitsDurationChart, 0, globalMax > 0 ? globalMax : 1);
        ShowChartLegend(CurrentWaitsDurationChart);
        CurrentWaitsDurationChart.Refresh();
    }

    private void RenderCurrentWaitsBlockedChart(List<BlockedSessionTrendPoint> data)
    {
        ClearChart(CurrentWaitsBlockedChart);
        ApplyTheme(CurrentWaitsBlockedChart);

        var rangeStart = ViewerDataService.ToLocalTime(DateTime.UtcNow - s_dataWindow);
        var rangeEnd = ViewerDataService.ToLocalTime(DateTime.UtcNow);

        _currentWaitsBlockedHover?.Clear();
        if (data.Count == 0)
        {
            var zeroLine = CurrentWaitsBlockedChart.Plot.Add.Scatter(
                new[] { rangeStart.ToOADate(), rangeEnd.ToOADate() },
                new[] { 0.0, 0.0 });
            zeroLine.LegendText = "Blocked Sessions";
            zeroLine.Color = ScottPlot.Color.FromHex(ChartPalette.SeriesColor("BlockedSessions"));
            zeroLine.MarkerSize = 0;
            CurrentWaitsBlockedChart.Plot.Axes.DateTimeTicksBottomDateChange();
            CurrentWaitsBlockedChart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
            ReapplyAxisColors(CurrentWaitsBlockedChart);
            CurrentWaitsBlockedChart.Plot.YLabel("Blocked Sessions");
            SetChartYLimitsWithLegendPadding(CurrentWaitsBlockedChart, 0, 1);
            ShowChartLegend(CurrentWaitsBlockedChart);
            CurrentWaitsBlockedChart.Refresh();
            return;
        }

        var grouped = data.GroupBy(d => d.DatabaseName).OrderBy(g => g.Key).ToList();
        double globalMax = 0;

        for (int i = 0; i < grouped.Count; i++)
        {
            var group = grouped[i];
            var ordered = group.OrderBy(t => t.CollectionTime).ToList();
            var times = ordered.Select(t => ViewerDataService.ToLocalTime(t.CollectionTime).ToOADate()).ToArray();
            var values = ordered.Select(t => (double)t.BlockedCount).ToArray();

            var plot = CurrentWaitsBlockedChart.Plot.Add.Scatter(times, values);
            plot.LegendText = group.Key;
            plot.Color = ScottPlot.Color.FromHex(SeriesColors[i % SeriesColors.Length]);
            ChartStyle.StyleScatter(plot);
            _currentWaitsBlockedHover?.Add(plot, group.Key);

            if (values.Length > 0) globalMax = Math.Max(globalMax, values.Max());
        }

        CurrentWaitsBlockedChart.Plot.Axes.DateTimeTicksBottomDateChange();
        CurrentWaitsBlockedChart.Plot.Axes.SetLimitsX(rangeStart.ToOADate(), rangeEnd.ToOADate());
        ReapplyAxisColors(CurrentWaitsBlockedChart);
        CurrentWaitsBlockedChart.Plot.YLabel("Blocked Sessions");
        SetChartYLimitsWithLegendPadding(CurrentWaitsBlockedChart, 0, globalMax > 0 ? globalMax : 1);
        ShowChartLegend(CurrentWaitsBlockedChart);
        CurrentWaitsBlockedChart.Refresh();
    }

    /// <summary>Tears down the Blocking trend hover helpers; called from the single Dispose() in Charts.cs.</summary>
    private void DisposeBlockingHelpers()
    {
        _lockWaitTrendHover?.Dispose();
        _blockingTrendHover?.Dispose();
        _deadlockTrendHover?.Dispose();
        _currentWaitsDurationHover?.Dispose();
        _currentWaitsBlockedHover?.Dispose();
    }
}
