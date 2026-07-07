/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The per-chart right-click drill-downs — a faithful port of Lite's <c>ServerTab.DrillDown.cs</c> +
/// <c>AddChartDrillDownMenuItem</c> / <c>AddWaitDrillDownMenuItem</c> (ServerTab.xaml.cs:319-365), adapted to
/// the viewer's already-shipped drill-down surfaces (the query heatmap already navigates through
/// <see cref="NavigateToActiveQueriesForWindowAsync"/>). Every resource / trend chart gets a
/// "Show Active Queries at This Time"; the Blocking / Deadlocks trend charts get
/// "Show Blocking / Deadlocks at This Time"; the Wait Stats chart gets a wait-specific
/// "Show Queries With &lt;wait&gt;" that opens the <see cref="WaitDrillDownWindow"/>.
///
/// <para>Two adaptations from Lite, both inherent to the viewer: (1) Lite builds these on its
/// <c>ContextMenuHelper</c> chart menu (Copy/Save Image, CSV, …), which is Lite-only and never ported, so the
/// viewer attaches a single-item WPF <see cref="ContextMenu"/> per chart the same way the ported heatmap
/// drill-down does (<see cref="RemoveScottPlotContextMenuResponses"/> takes over right-click from ScottPlot).
/// (2) The chart X axis is display-time (every viewer chart plots through
/// <see cref="ViewerTimeHelper.ForDisplay"/>), so the nearest-series time the <see cref="ChartHoverHelper"/>
/// returns is converted back to the store's naive UTC via <see cref="ViewerTimeHelper.DisplayToNaiveUtc"/>
/// before the +/-30-minute window is read.</para>
/// </summary>
public partial class ViewerServerTab
{
    /// <summary>The drill-down half-window: a click resolves to a +/-30-minute read around the point
    /// (Lite's OnActiveQueriesDrillDown / OnBlockingDrillDown / OnDeadlockDrillDown all use 30).</summary>
    internal const int DrillDownHalfWindowMinutes = 30;

    /// <summary>
    /// Wires the right-click drill-down menus onto every chart that has one (Items 2-4). Called from the
    /// constructor after the Initialize*Charts methods, so each chart's <see cref="ChartHoverHelper"/> exists.
    /// </summary>
    private void WireChartDrillDowns()
    {
        /* The hover accessors are lambdas, not captured values: WaitStats + Perfmon create their
           ChartHoverHelper LAZILY on the tab's first load (not in an Initialize*Charts call), so their fields
           are still null here in the constructor. Reading the field at menu-Opened time (by which point the
           tab has loaded and the hover exists) is correct for both the eager and the lazy charts. */

        /* Wait Stats — the specialized "Show Queries With <wait>" drill-down (opens WaitDrillDownWindow). */
        AddWaitDrillDownMenuItem(WaitStatsChart, () => _waitStatsHover);

        /* CPU / Memory / tempdb / Perfmon — "Show Active Queries at This Time" (Item 2, 10 charts). */
        AddChartDrillDownMenuItem(CpuChart, () => _cpuHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(MemoryChart, () => _memoryHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(MemoryClerksChart, () => _memoryClerksHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(MemoryGrantSizingChart, () => _memoryGrantSizingHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(MemoryGrantActivityChart, () => _memoryGrantActivityHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(MemoryPressureEventsChart, () => _memoryPressureEventsHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(TempDbChart, () => _tempDbHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(TempDbSizeChart, () => _tempDbSizeHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(TempDbFileIoChart, () => _tempDbFileIoHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(PerfmonChart, () => _perfmonHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);

        /* Performance Trends — "Show Active Queries at This Time" (Item 3, 4 charts). */
        AddChartDrillDownMenuItem(QueryDurationTrendChart, () => _queryDurationTrendHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(ProcDurationTrendChart, () => _procDurationTrendHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(QueryStoreDurationTrendChart, () => _queryStoreDurationTrendHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(ExecutionCountTrendChart, () => _executionCountTrendHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);

        /* Blocking Trends — Lock Wait + Blocking -> "Show Blocking at This Time"; Deadlock ->
           "Show Deadlocks at This Time" (Item 3, 3 charts). */
        AddChartDrillDownMenuItem(LockWaitTrendChart, () => _lockWaitTrendHover, "Show Blocking at This Time", OnBlockingDrillDown);
        AddChartDrillDownMenuItem(BlockingTrendChart, () => _blockingTrendHover, "Show Blocking at This Time", OnBlockingDrillDown);
        AddChartDrillDownMenuItem(DeadlockTrendChart, () => _deadlockTrendHover, "Show Deadlocks at This Time", OnDeadlockDrillDown);

        /* File I/O (4) + Blocking > Current Waits (2) — the rest of Lite's "Show Active Queries at This Time"
           set (ServerTab.xaml.cs:340-363, in the same referenced block). Charts in this same viewer surface,
           same drill target; wired for faithful parity so no sibling chart is left without the drill-down. */
        AddChartDrillDownMenuItem(FileIoReadChart, () => _fileIoReadHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(FileIoWriteChart, () => _fileIoWriteHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(FileIoReadThroughputChart, () => _fileIoReadThroughputHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(FileIoWriteThroughputChart, () => _fileIoWriteThroughputHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(CurrentWaitsDurationChart, () => _currentWaitsDurationHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
        AddChartDrillDownMenuItem(CurrentWaitsBlockedChart, () => _currentWaitsBlockedHover, "Show Active Queries at This Time", OnActiveQueriesDrillDown);
    }

    /// <summary>
    /// Attaches a single-item right-click drill-down menu to a chart (Lite's
    /// <c>AddChartDrillDownMenuItem</c>). The Opened handler resolves the nearest-series display-time under
    /// the cursor via the chart's hover helper (disabling the item when the cursor is off any series); the
    /// Click routes that time to <paramref name="handler"/>, which converts it back to UTC and reads the window.
    /// <paramref name="hoverAccessor"/> is read at Opened time (not captured) so lazily-created hovers resolve.
    /// </summary>
    private void AddChartDrillDownMenuItem(
        ScottPlot.WPF.WpfPlot chart, Func<ChartHoverHelper?> hoverAccessor, string label, Action<DateTime> handler)
    {
        var menu = new ContextMenu();
        var item = new MenuItem { Header = label };
        menu.Items.Add(item);

        menu.Opened += (_, _) =>
        {
            var nearest = hoverAccessor()?.GetNearestSeries(Mouse.GetPosition(chart));
            if (nearest.HasValue)
            {
                item.Tag = nearest.Value.Time;   /* display-time (the chart X runs through ForDisplay) */
                item.IsEnabled = true;
            }
            else
            {
                item.Tag = null;
                item.IsEnabled = false;
            }
        };

        item.Click += (_, _) =>
        {
            if (item.Tag is DateTime displayTime)
                handler(displayTime);
        };

        AttachChartContextMenu(chart, menu);
    }

    /// <summary>
    /// The Wait Stats chart's specialized right-click "Show Queries With &lt;wait&gt;" (Lite's
    /// <c>AddWaitDrillDownMenuItem</c>): the hover's nearest series is a wait TYPE (the series label), so the
    /// item header names it and the Click opens the <see cref="WaitDrillDownWindow"/> for that wait over the
    /// drill window. Underscores in the wait type are doubled so WPF doesn't treat them as an access key.
    /// </summary>
    private void AddWaitDrillDownMenuItem(ScottPlot.WPF.WpfPlot chart, Func<ChartHoverHelper?> hoverAccessor)
    {
        var menu = new ContextMenu();
        var item = new MenuItem { Header = "Show Queries With This Wait" };
        menu.Items.Add(item);

        menu.Opened += (_, _) =>
        {
            var nearest = hoverAccessor()?.GetNearestSeries(Mouse.GetPosition(chart));
            if (nearest.HasValue)
            {
                item.Tag = (nearest.Value.Label, nearest.Value.Time);
                item.Header = $"Show Queries With {nearest.Value.Label.Replace("_", "__")}";
                item.IsEnabled = true;
            }
            else
            {
                item.Tag = null;
                item.Header = "Show Queries With This Wait";
                item.IsEnabled = false;
            }
        };

        item.Click += (_, _) =>
        {
            if (item.Tag is ValueTuple<string, DateTime> tag)
                ShowQueriesForWaitType(tag.Item1, tag.Item2);
        };

        AttachChartContextMenu(chart, menu);
    }

    /// <summary>
    /// Takes right-click over from ScottPlot and shows <paramref name="menu"/> at the cursor — the ported
    /// heatmap drill-down's exact approach (<see cref="RemoveScottPlotContextMenuResponses"/> +
    /// PreviewMouseRightButtonDown), factored so every drill-down chart and the heatmap share one path.
    /// </summary>
    private static void AttachChartContextMenu(ScottPlot.WPF.WpfPlot chart, ContextMenu menu)
    {
        RemoveScottPlotContextMenuResponses(chart);
        chart.PreviewMouseRightButtonDown += (_, e) =>
        {
            e.Handled = true;
            menu.PlacementTarget = chart;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
        };
    }

    /// <summary>
    /// Removes ScottPlot's built-in right-click responses (its default context menu / pan) so a WPF
    /// <see cref="ContextMenu"/> can own right-click. Shared by every drill-down chart and the query heatmap.
    /// </summary>
    internal static void RemoveScottPlotContextMenuResponses(ScottPlot.WPF.WpfPlot chart)
    {
        chart.UserInputProcessor.UserActionResponses.RemoveAll(r =>
            r.GetType().Name.Contains("Context", StringComparison.Ordinal) ||
            r.GetType().Name.Contains("RightClick", StringComparison.Ordinal) ||
            r.GetType().Name.Contains("Menu", StringComparison.Ordinal));
    }

    /// <summary>The store's naive-UTC +/-30-minute window around a chart display-time (pure, unit-tested).</summary>
    internal static (DateTime FromUtc, DateTime ToUtc) DrillWindowUtc(DateTime displayTime)
    {
        var utc = ViewerTimeHelper.DisplayToNaiveUtc(displayTime);
        return (utc.AddMinutes(-DrillDownHalfWindowMinutes), utc.AddMinutes(DrillDownHalfWindowMinutes));
    }

    /// <summary>
    /// "Show Active Queries at This Time" — navigates to Queries -> Active Queries filtered to the +/-30-minute
    /// window around the clicked point (Lite's OnActiveQueriesDrillDown). Also the target of the Overview
    /// lanes' <c>ShowActiveQueriesRequested</c> event.
    /// </summary>
    private async void OnActiveQueriesDrillDown(DateTime displayTime)
    {
        try
        {
            var (fromUtc, toUtc) = DrillWindowUtc(displayTime);
            var indicator = $"Drill-down: {ViewerTimeHelper.ForDisplay(fromUtc):HH:mm} → {ViewerTimeHelper.ForDisplay(toUtc):HH:mm}";
            await NavigateToActiveQueriesForWindowAsync(fromUtc, toUtc, indicator);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"active-queries drill-down failed: {ex.Message}");
        }
    }

    /// <summary>
    /// "Show Blocking at This Time" — navigates to Blocking -> Blocked Process Reports filtered to the
    /// +/-30-minute window (Lite's OnBlockingDrillDown, targeting Darling's existing
    /// <see cref="LoadBlockedProcessReportsAsync"/> loader).
    /// </summary>
    private async void OnBlockingDrillDown(DateTime displayTime)
    {
        var (fromUtc, toUtc) = DrillWindowUtc(displayTime);
        _suppressDrillDownAutoRefresh = true;
        try
        {
            InnerTabs.SelectedIndex = BlockingInnerTabIndex;
            BlockingSubTabs.SelectedIndex = BlockedProcessReportsSubTabIndex;
        }
        finally
        {
            _suppressDrillDownAutoRefresh = false;
        }

        try
        {
            await LoadBlockedProcessReportsAsync(fromUtc, toUtc);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"blocking drill-down failed: {ex.Message}");
        }
    }

    /// <summary>
    /// "Show Deadlocks at This Time" — navigates to Blocking -> Deadlocks filtered to the +/-30-minute window
    /// (Lite's OnDeadlockDrillDown, targeting Darling's existing <see cref="LoadDeadlocksAsync"/> loader).
    /// </summary>
    private async void OnDeadlockDrillDown(DateTime displayTime)
    {
        var (fromUtc, toUtc) = DrillWindowUtc(displayTime);
        _suppressDrillDownAutoRefresh = true;
        try
        {
            InnerTabs.SelectedIndex = BlockingInnerTabIndex;
            BlockingSubTabs.SelectedIndex = DeadlocksSubTabIndex;
        }
        finally
        {
            _suppressDrillDownAutoRefresh = false;
        }

        try
        {
            await LoadDeadlocksAsync(fromUtc, toUtc);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"deadlock drill-down failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the <see cref="WaitDrillDownWindow"/> for a wait type over the +/-30-minute drill window (Lite's
    /// ShowQueriesForWaitType_Click). Owned by this tab's window so the shared plan window it spawns floats
    /// above it; the drill window reads correlated snapshots from Postgres (no live SQL).
    /// </summary>
    private void ShowQueriesForWaitType(string waitType, DateTime displayTime)
    {
        var (fromUtc, toUtc) = DrillWindowUtc(displayTime);
        var window = new WaitDrillDownWindow(_dataService, _server.ServerId, waitType, fromUtc, toUtc)
        {
            Owner = Window.GetWindow(this),
        };
        window.ShowDialog();
    }
}
