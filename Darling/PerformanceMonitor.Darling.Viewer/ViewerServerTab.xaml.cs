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
/// One monitored server's detail surface (headless-plan W0 IA inversion): a closable per-server tab
/// hosting an inner tab strip — Overview (CPU-utilization + wait-time-by-category trend charts),
/// Queries (top-50 query stats), Blocking (XE blocked-process reports with the DMV-snapshot fallback),
/// and Collection Health (per-collector last-run status). This is the per-server content relocated
/// out of MainWindow's old flat tab list; the load/render bodies are unchanged (pure re-hosting).
/// Loads are lazy per inner tab (Lite's visible-only rule): switching inner tabs loads the newly
/// visible one, and <see cref="RefreshActiveInnerTabAsync"/> — driven by MainWindow's 60-second timer
/// only when this server tab is the visible top-level tab — reloads just the active inner tab.
/// </summary>
public partial class ViewerServerTab : UserControl
{
    /// <summary>One window for every windowed surface: charts, queries, and blocking all read 24 hours.</summary>
    private static readonly TimeSpan s_dataWindow = TimeSpan.FromHours(24);

    private const int OverviewInnerTabIndex = 0;
    private const int QueriesInnerTabIndex = 1;
    private const int BlockingInnerTabIndex = 2;
    private const int HealthInnerTabIndex = 3;
    private const int CpuInnerTabIndex = 4;
    private const int TempDbInnerTabIndex = 5;

    private readonly ViewerDataService _dataService;
    private readonly DarlingServer _server;

    private bool _refreshInFlight;
    private bool _refreshRequested;

    /// <summary>Raised after a load so MainWindow can surface progress/errors in its status bar.</summary>
    public event Action<string>? StatusChanged;

    public ViewerServerTab(ViewerDataService dataService, DarlingServer server)
    {
        _dataService = dataService;
        _server = server;
        InitializeComponent();

        /* ThemeManager is fixed to Dark, matching this control's hardcoded palette — so the shared
           chart chrome applies without any per-control theme plumbing. */
        ChartStyle.ApplyThemeToChart(CpuTrendChart);
        CpuTrendChart.Refresh();
        ChartStyle.ApplyThemeToChart(WaitCategoryChart);
        WaitCategoryChart.Refresh();

        /* CPU + tempdb inner-tab charts (copied from Lite): theme up front + wire hover. */
        InitializeCpuTempDbCharts();
    }

    /// <summary>The server this tab is bound to; MainWindow keys open tabs by this for dedupe/close.</summary>
    public int ServerId => _server.ServerId;

    /// <summary>The server record, for the tab header label and status text.</summary>
    public DarlingServer Server => _server;

    /// <summary>
    /// Lazy per-inner-tab load: switching inner tabs loads the newly visible one. SelectionChanged is
    /// a bubbling routed event, so selections inside the tab content (a grid) reach here too — only
    /// react to the inner TabControl's own. Gated on <see cref="System.Windows.FrameworkElement.IsLoaded"/>
    /// so the selection raised while the TabControl is first built (before this tab is shown) is ignored:
    /// MainWindow drives the initial load when it makes this server tab visible, and the 60-second timer
    /// keeps it fresh — so the control loads exactly once per event, not on construction too.
    /// </summary>
    private async void InnerTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, InnerTabs) || !IsLoaded)
        {
            return;
        }

        await RefreshActiveInnerTabAsync();
    }

    /// <summary>
    /// Loads the ACTIVE inner tab, with the overlap guard mirroring MainWindow's aggregate-tab loop.
    /// If the inner tab switches while a load is in flight, the triggering event bounces off the guard,
    /// so after each load we loop when the selection moved on and load again — leaving no inner tab
    /// stranded. A trigger that arrives mid-load (the 60-second tick, an inner-tab switch) sets
    /// <see cref="_refreshRequested"/> instead of being dropped, so the running loop reloads once more.
    /// </summary>
    public async Task RefreshActiveInnerTabAsync()
    {
        if (_refreshInFlight)
        {
            _refreshRequested = true;
            return;
        }

        _refreshInFlight = true;
        try
        {
            do
            {
                _refreshRequested = false;

                int loadedTab;
                do
                {
                    loadedTab = InnerTabs.SelectedIndex;
                    await LoadInnerTabAsync(loadedTab);
                }
                while (InnerTabs.SelectedIndex != loadedTab);
            }
            while (_refreshRequested);
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    private async Task LoadInnerTabAsync(int tabIndex)
    {
        try
        {
            switch (tabIndex)
            {
                case QueriesInnerTabIndex:
                    await LoadQueriesAsync();
                    break;
                case BlockingInnerTabIndex:
                    await LoadBlockingAsync();
                    break;
                case HealthInnerTabIndex:
                    await LoadHealthAsync();
                    break;
                case CpuInnerTabIndex:
                    await LoadCpuAsync();
                    break;
                case TempDbInnerTabIndex:
                    await LoadTempDbAsync();
                    break;
                case OverviewInnerTabIndex:
                default:
                    await LoadOverviewChartsAsync();
                    break;
            }

            StatusChanged?.Invoke($"{_server.DisplayName} — refreshed {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"refresh failed: {ex.Message}");
        }
    }

    private async Task LoadOverviewChartsAsync()
    {
        var sinceUtc = DateTime.UtcNow - s_dataWindow;

        /* Both reads run concurrently — NpgsqlDataSource pools a connection for each. */
        var cpuTask = _dataService.GetCpuTrendAsync(_server.ServerId, sinceUtc);
        var waitTask = _dataService.GetWaitCategoryTrendAsync(_server.ServerId, sinceUtc);
        var cpu = await cpuTask;
        var waits = await waitTask;

        RenderCpuTrend(cpu);
        RenderWaitCategoryTrend(waits);
    }

    private async Task LoadHealthAsync()
    {
        var health = await _dataService.GetCollectionHealthAsync(_server.ServerId);
        HealthGrid.ItemsSource = health;
    }

    private async Task LoadQueriesAsync()
    {
        var sinceUtc = DateTime.UtcNow - s_dataWindow;
        var rows = await _dataService.GetTopQueriesAsync(_server.ServerId, sinceUtc);

        QueriesGrid.ItemsSource = rows;
        QueriesHintText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadBlockingAsync()
    {
        var endUtc = DateTime.UtcNow;
        var rows = await _dataService.GetRecentBlockedProcessReportsAsync(
            _server.ServerId, endUtc - s_dataWindow, endUtc);

        BlockingGrid.ItemsSource = rows;
        BlockingHintText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RenderCpuTrend(List<CpuTrendPoint> points)
    {
        CpuTrendChart.Plot.Clear();
        ChartStyle.ApplyThemeToChart(CpuTrendChart);

        CpuChartHintText.Visibility = points.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CpuTrendChart.Plot.Legend.IsVisible = points.Count > 0;

        if (points.Count > 0)
        {
            var xs = points.Select(p => ViewerDataService.ToLocalTime(p.CollectionTime).ToOADate()).ToArray();

            /* Series names + colors mirror Lite's CPU chart (ServerTab.Charts.cs UpdateCpuChart):
               "SQL Server" in the SqlCpu blue, "Other" in the OtherCpu red, both via SeriesColor. */
            var sql = CpuTrendChart.Plot.Add.Scatter(xs, points.Select(p => p.SqlServerCpu).ToArray());
            sql.LegendText = "SQL Server";
            sql.Color = ScottPlot.Color.FromHex(ChartPalette.SeriesColor("SqlCpu"));
            ChartStyle.StyleScatter(sql);

            var other = CpuTrendChart.Plot.Add.Scatter(xs, points.Select(p => p.OtherProcessCpu).ToArray());
            other.LegendText = "Other";
            other.Color = ScottPlot.Color.FromHex(ChartPalette.SeriesColor("OtherCpu"));
            ChartStyle.StyleScatter(other);

            CpuTrendChart.Plot.Axes.DateTimeTicksBottom();
            ChartStyle.ReapplyAxisColors(CpuTrendChart);
            CpuTrendChart.Plot.YLabel("CPU %");
            CpuTrendChart.Plot.Axes.AutoScale();
            ChartStyle.SetChartYLimitsWithLegendPadding(CpuTrendChart);
        }

        CpuTrendChart.Refresh();
    }

    private void RenderWaitCategoryTrend(List<WaitCategoryTrendPoint> points)
    {
        WaitCategoryChart.Plot.Clear();
        ChartStyle.ApplyThemeToChart(WaitCategoryChart);

        var series = ViewerDataService.RollUpWaitCategories(points);
        WaitChartHintText.Visibility = series.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WaitCategoryChart.Plot.Legend.IsVisible = series.Count > 0;

        /* Each category is drawn in its fixed WaitColor — the same identity the plan-viewer wait
           list uses — so a category reads as the same color everywhere in the product. */
        foreach (var s in series)
        {
            var xs = s.Times.Select(t => ViewerDataService.ToLocalTime(t).ToOADate()).ToArray();
            var scatter = WaitCategoryChart.Plot.Add.Scatter(xs, s.Values);
            scatter.LegendText = s.Category;
            scatter.Color = ScottPlot.Color.FromHex(ChartPalette.WaitColor(s.Category));
            ChartStyle.StyleScatter(scatter);
        }

        if (series.Count > 0)
        {
            WaitCategoryChart.Plot.Axes.DateTimeTicksBottom();
            ChartStyle.ReapplyAxisColors(WaitCategoryChart);
            WaitCategoryChart.Plot.YLabel("delta wait time (ms)");
            WaitCategoryChart.Plot.Axes.AutoScale();
            ChartStyle.SetChartYLimitsWithLegendPadding(WaitCategoryChart);
        }

        WaitCategoryChart.Refresh();
    }
}
