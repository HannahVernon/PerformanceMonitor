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
using System.Windows.Threading;
using PerformanceMonitor.Common;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Darling viewer shell (headless plan M3 + viewer wave 2): the server list from the
/// central store on the left, and four surface tabs on the right — Overview (per-collector
/// Collection Health + the top-8 wait-types trend), Queries (top-50 query-stats groups),
/// Blocking (XE blocked-process reports with the DMV-snapshot fallback merged in), and
/// Recommendations (the latest analysis findings with an advice/remediation detail pane) —
/// all read straight from Postgres via <see cref="ViewerDataService"/>. Loads are lazy per
/// tab (Lite's visible-only rule): selecting a server or a tab loads ONLY the active tab,
/// and the 60-second timer refreshes only the active tab. Every data load is async — the
/// Npgsql calls are awaited so the UI thread never blocks on a query — with results marshaled
/// back by the dispatcher's await continuation.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF windows are never disposed by the framework; the data service is disposed in OnClosed.")]
public partial class MainWindow : Window
{
    private static readonly TimeSpan s_refreshInterval = TimeSpan.FromSeconds(60);

    /// <summary>One window for every windowed surface: waits, queries, and blocking all read 24 hours.</summary>
    private static readonly TimeSpan s_dataWindow = TimeSpan.FromHours(24);

    private const int OverviewTabIndex = 0;
    private const int QueriesTabIndex = 1;
    private const int BlockingTabIndex = 2;
    private const int RecommendationsTabIndex = 3;

    /// <summary>The detail pane's no-selection state (also its initial text in the XAML).</summary>
    private const string FindingDetailPlaceholder = "select a finding to see its story, advice, and stored remediation";

    private ViewerDataService? _dataService;
    private DispatcherTimer? _refreshTimer;
    private bool _refreshInFlight;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        ViewerSettings? settings;
        try
        {
            settings = await Task.Run(() => ViewerSettings.TryLoad(ExplicitConfigPathFromArgs()));
        }
        catch (Exception ex)
        {
            ShowMessage($"darling.json could not be read: {ex.Message}");
            return;
        }

        if (settings is null)
        {
            ShowMessage("darling.json not found — copy darling.sample.json from the service and point DARLING_CONFIG at it.");
            return;
        }

        _dataService = new ViewerDataService(settings.ConnectionString);

        /* ThemeManager defaults to Dark, which is exactly this window's hardcoded palette —
           so the shared chart chrome applies without any theme wiring. */
        ChartStyle.ApplyThemeToChart(WaitTrendChart);
        WaitTrendChart.Refresh();

        await LoadServersAsync();

        _refreshTimer = new DispatcherTimer { Interval = s_refreshInterval };
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();
    }

    /// <summary>First command-line argument = explicit config path, mirroring the service.</summary>
    private static string? ExplicitConfigPathFromArgs()
    {
        var args = Environment.GetCommandLineArgs();
        return args.Length > 1 ? args[1] : null;
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs e)
        => await RefreshSelectedServerAsync();

    private async void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await RefreshSelectedServerAsync();

    /// <summary>
    /// Lazy per-tab load: switching tabs loads the newly visible tab. SelectionChanged is a
    /// bubbling routed event, so selections inside the tab content (the findings grid, the
    /// server list template) reach here too — only react to the TabControl's own.
    /// </summary>
    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs))
        {
            return;
        }

        await RefreshSelectedServerAsync();
    }

    private void FindingsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, FindingsGrid))
        {
            return;
        }

        FindingDetailText.Text = FindingsGrid.SelectedItem is ViewerFindingRow row
            ? ViewerDataService.ComposeFindingDetailText(row.Finding)
            : FindingDetailPlaceholder;
    }

    private async Task LoadServersAsync()
    {
        if (_dataService is null)
        {
            return;
        }

        try
        {
            var servers = await _dataService.GetServersAsync();
            ServerList.ItemsSource = servers;
            ServersHintText.Visibility = servers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (servers.Count > 0)
            {
                /* Triggers SelectionChanged, which loads the active tab for the first server. */
                ServerList.SelectedIndex = 0;
            }
            else
            {
                StatusText.Text = $"connected — no servers registered yet ({DateTime.Now:HH:mm:ss})";
            }
        }
        catch (Exception ex)
        {
            ShowMessage($"Cannot read the Darling store: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads the ACTIVE tab for the selected server, with the overlap guard. If the user
    /// switches server or tab while a load is in flight, the triggering event bounces off
    /// the guard — so after each load, loop when the selection moved on and load again,
    /// leaving no tab stranded with stale or empty data.
    /// </summary>
    private async Task RefreshSelectedServerAsync()
    {
        if (_dataService is null || _refreshInFlight)
        {
            return;
        }

        _refreshInFlight = true;
        try
        {
            DarlingServer? loadedServer;
            int loadedTab;
            do
            {
                loadedServer = ServerList.SelectedItem as DarlingServer;
                loadedTab = MainTabs.SelectedIndex;

                if (loadedServer is null)
                {
                    ClearAllTabs();
                    return;
                }

                await LoadTabAsync(loadedServer, loadedTab);
            }
            while (!ReferenceEquals(ServerList.SelectedItem, loadedServer) || MainTabs.SelectedIndex != loadedTab);
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    private async Task LoadTabAsync(DarlingServer server, int tabIndex)
    {
        try
        {
            switch (tabIndex)
            {
                case QueriesTabIndex:
                    await LoadQueriesAsync(server);
                    break;
                case BlockingTabIndex:
                    await LoadBlockingAsync(server);
                    break;
                case RecommendationsTabIndex:
                    await LoadRecommendationsAsync(server);
                    break;
                case OverviewTabIndex:
                default:
                    await LoadOverviewAsync(server);
                    break;
            }

            StatusText.Text = $"{server.DisplayName} — refreshed {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"refresh failed: {ex.Message}";
        }
    }

    private async Task LoadOverviewAsync(DarlingServer server)
    {
        var sinceUtc = DateTime.UtcNow - s_dataWindow;

        /* Both queries run concurrently — NpgsqlDataSource pools a connection for each. */
        var healthTask = _dataService!.GetCollectionHealthAsync(server.ServerId);
        var waitTask = _dataService.GetWaitTrendAsync(server.ServerId, sinceUtc);
        var health = await healthTask;
        var waits = await waitTask;

        HealthGrid.ItemsSource = health;
        RenderWaitTrend(waits);
    }

    private async Task LoadQueriesAsync(DarlingServer server)
    {
        var sinceUtc = DateTime.UtcNow - s_dataWindow;
        var rows = await _dataService!.GetTopQueriesAsync(server.ServerId, sinceUtc);

        QueriesGrid.ItemsSource = rows;
        QueriesHintText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadBlockingAsync(DarlingServer server)
    {
        var endUtc = DateTime.UtcNow;
        var rows = await _dataService!.GetRecentBlockedProcessReportsAsync(
            server.ServerId, endUtc - s_dataWindow, endUtc);

        BlockingGrid.ItemsSource = rows;
        BlockingHintText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadRecommendationsAsync(DarlingServer server)
    {
        /* Remember the selected finding so the 60-second refresh doesn't yank the detail
           pane out from under the reader — reselect the same story if it's still there. */
        var selectedHash = (FindingsGrid.SelectedItem as ViewerFindingRow)?.Finding.StoryPathHash;

        var rows = await _dataService!.GetLatestFindingsAsync(server.ServerId);

        FindingsGrid.ItemsSource = rows;
        FindingsHintText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FindingsHeaderText.Text = rows.Count == 0
            ? "Latest Analysis Findings"
            : $"Latest Analysis Findings — {rows[0].AnalysisTimeLocal:yyyy-MM-dd HH:mm:ss} (local)";

        var reselect = selectedHash is null
            ? null
            : rows.FirstOrDefault(r => r.Finding.StoryPathHash == selectedHash);
        if (reselect is not null)
        {
            FindingsGrid.SelectedItem = reselect;
        }
        else if (FindingsGrid.SelectedItem is null)
        {
            FindingDetailText.Text = FindingDetailPlaceholder;
        }
    }

    private void ClearAllTabs()
    {
        HealthGrid.ItemsSource = null;
        RenderWaitTrend(Array.Empty<WaitTrendPoint>());
        QueriesGrid.ItemsSource = null;
        QueriesHintText.Visibility = Visibility.Collapsed;
        BlockingGrid.ItemsSource = null;
        BlockingHintText.Visibility = Visibility.Collapsed;
        FindingsGrid.ItemsSource = null;
        FindingsHintText.Visibility = Visibility.Collapsed;
        FindingDetailText.Text = FindingDetailPlaceholder;
    }

    private void RenderWaitTrend(IReadOnlyList<WaitTrendPoint> points)
    {
        WaitTrendChart.Plot.Clear();
        ChartStyle.ApplyThemeToChart(WaitTrendChart);

        var series = ViewerDataService.PivotWaitSeries(points);
        ChartHintText.Visibility = series.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WaitTrendChart.Plot.Legend.IsVisible = series.Count > 0;

        var colorIndex = 0;
        foreach (var s in series)
        {
            var xs = s.Times.Select(t => ViewerDataService.ToLocalTime(t).ToOADate()).ToArray();
            var scatter = WaitTrendChart.Plot.Add.Scatter(xs, s.Values);
            scatter.LegendText = s.WaitType;
            scatter.Color = ScottPlot.Color.FromHex(ChartPalette.CyclingColor(colorIndex++));
            ChartStyle.StyleScatter(scatter);
        }

        if (series.Count > 0)
        {
            WaitTrendChart.Plot.Axes.DateTimeTicksBottom();
            ChartStyle.ReapplyAxisColors(WaitTrendChart);
            WaitTrendChart.Plot.YLabel("delta wait time (ms)");
            WaitTrendChart.Plot.Axes.AutoScale();
            ChartStyle.SetChartYLimitsWithLegendPadding(WaitTrendChart);
        }

        WaitTrendChart.Refresh();
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessageOverlay.Visibility = Visibility.Visible;
        StatusText.Text = "";
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer?.Stop();

        if (_dataService is not null)
        {
            await _dataService.DisposeAsync();
        }
    }
}
