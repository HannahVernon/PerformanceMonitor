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
using System.Windows.Input;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Collection Health inner tab — a COPY of Lite's 3-sub-tab Collection Health surface
/// (ServerTab.xaml + the <c>RefreshCollectionHealthAsync</c> load, the
/// <c>CollectionHealthGrid_MouseDoubleClick</c> drill, and <c>UpdateCollectorDurationChart</c>), reads
/// rewired to Postgres. It REPLACES the shell's single latest-run-per-collector grid. Health Summary =
/// the 7-day per-collector aggregate (double-click opens the per-collector CollectionLogWindow drill);
/// Collection Log = the recent run log; Duration Trends = the per-collector success-duration scatter.
/// The chart's only render-body change from Lite is the time axis: where Lite shifts the raw stored
/// time by its per-server <c>UtcOffsetMinutes</c>, the viewer runs every point through
/// <see cref="ViewerTimeHelper.ForDisplay"/> (the naive-UTC-to-viewer-local convention every Darling
/// chart uses), and line polish flows through the shared <see cref="ChartStyle"/> like the other viewer
/// charts. Lite's per-chart context menu / "Open Log File" button are intentionally not ported.
/// </summary>
public partial class ViewerServerTab
{
    private ChartHoverHelper? _collectorDurationHover;

    /// <summary>
    /// Applies the shared chrome to the Duration Trends chart and wires its hover tooltip. Called from
    /// the constructor after <c>InitializeComponent</c> so it doesn't flash white before its first load,
    /// matching Lite's ServerTab (and the viewer's other chart inits).
    /// </summary>
    private void InitializeCollectionHealthChart()
    {
        ApplyTheme(CollectorDurationChart);
        CollectorDurationChart.Refresh();
        _collectorDurationHover = new ChartHoverHelper(CollectorDurationChart, "ms");
    }

    /// <summary>
    /// Collection Health tab load: the 7-day per-collector health aggregate and the recent collection
    /// log read concurrently (NpgsqlDataSource pools a connection for each), then each grid goes through
    /// its filter manager's UpdateData so active column filters survive the refresh, and the log also
    /// feeds the Duration Trends chart. Mirrors Lite's <c>RefreshCollectionHealthAsync</c> — but the
    /// reads are genuinely async, so there is no Task.Run wrap. LoadInnerTabAsync owns the try/catch that
    /// surfaces failures on the status bar.
    /// </summary>
    private async Task LoadHealthAsync()
    {
        /* Health Summary stays Lite's fixed 7-day per-collector rollup (its staleness banding needs a
           stable horizon regardless of the toolbar window). The Collection Log + Duration Trends honor the
           settable window EXACTLY — a preset or a custom From/To — via GetWindowUtc(), matching the Wait
           Stats / Blocking tabs (the old GetWindowHoursBack() rounded a custom range to a now-relative span). */
        var (startUtc, endUtc) = GetWindowUtc();
        var healthTask = _dataService.GetCollectionHealthAsync(_server.ServerId);
        var logTask = _dataService.GetRecentCollectionLogAsync(_server.ServerId, startUtc, endUtc);
        await Task.WhenAll(healthTask, logTask);

        _collectionHealthFilterMgr!.UpdateData(healthTask.Result);
        _collectionLogFilterMgr!.UpdateData(logTask.Result);
        RenderCollectorDurationChart(logTask.Result);
    }

    /// <summary>
    /// Double-click a Health Summary row to open that collector's full collection history. Copied from
    /// Lite's <c>CollectionHealthGrid_MouseDoubleClick</c>, repointed to the viewer's CollectionLogWindow.
    /// </summary>
    private void CollectionHealthGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CollectionHealthGrid.SelectedItem is not CollectorHealthRow item) return;

        var window = new CollectionLogWindow(_dataService, _server.ServerId, item.CollectorName)
        {
            Owner = Window.GetWindow(this)
        };
        window.ShowDialog();
    }

    /// <summary>
    /// Per-collector success-duration scatter over the window. Copied from Lite's
    /// <c>UpdateCollectorDurationChart</c>: one line per collector (SUCCESS runs with a duration, needing
    /// at least two points), cycling the shared palette. The one change is the time axis — every point
    /// runs through <see cref="ViewerTimeHelper.ForDisplay"/> (Lite shifts by its per-server
    /// UtcOffsetMinutes) — and line polish uses the shared <see cref="ChartStyle.StyleScatter"/>.
    /// </summary>
    private void RenderCollectorDurationChart(List<CollectionLogRow> data)
    {
        ClearChart(CollectorDurationChart);
        ApplyTheme(CollectorDurationChart);

        if (data.Count == 0) { CollectorDurationChart.Refresh(); return; }

        /* Group by collector, plot each as a separate series */
        var groups = data
            .Where(d => d.DurationMs.HasValue && d.Status == "SUCCESS")
            .GroupBy(d => d.CollectorName)
            .OrderBy(g => g.Key)
            .ToList();

        _collectorDurationHover?.Clear();
        int colorIdx = 0;
        foreach (var group in groups)
        {
            var points = group.OrderBy(d => d.CollectionTime).ToList();
            if (points.Count < 2) continue;

            var times = points.Select(d => ViewerTimeHelper.ForDisplay(d.CollectionTime).ToOADate()).ToArray();
            var durations = points.Select(d => (double)d.DurationMs!.Value).ToArray();

            var scatter = CollectorDurationChart.Plot.Add.Scatter(times, durations);
            scatter.LegendText = group.Key;
            scatter.Color = ScottPlot.Color.FromHex(SeriesColors[colorIdx % SeriesColors.Length]);
            ChartStyle.StyleScatter(scatter);
            _collectorDurationHover?.Add(scatter, group.Key);
            colorIdx++;
        }

        CollectorDurationChart.Plot.Axes.DateTimeTicksBottomDateChange();
        ReapplyAxisColors(CollectorDurationChart);
        CollectorDurationChart.Plot.YLabel("Duration (ms)");
        CollectorDurationChart.Plot.Axes.AutoScale();
        ShowChartLegend(CollectorDurationChart);
        CollectorDurationChart.Refresh();
    }

    /// <summary>Tears down the Duration Trends hover helper. Forwarded to from the tab's single Dispose().</summary>
    private void DisposeCollectionHealthHelpers()
    {
        _collectorDurationHover?.Dispose();
    }
}
