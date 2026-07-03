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
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PerformanceMonitor.Common;
using PerformanceMonitor.Notifications;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Darling viewer shell (headless plan M3 + viewer waves 2-4): the server list from the
/// central store on the left, and five surface tabs on the right — Overview (per-collector
/// Collection Health plus CPU-utilization and wait-time-by-category trend charts), Queries
/// (top-50 query-stats groups), Blocking (XE blocked-process reports with the DMV-snapshot
/// fallback merged in), Recommendations (the latest analysis findings with an advice/remediation
/// detail pane), and Alerts (config_alert_log history with mute-rule write-back) —
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
    private const int AlertsTabIndex = 4;

    /// <summary>The detail pane's no-selection state (also its initial text in the XAML).</summary>
    private const string FindingDetailPlaceholder = "select a finding to see its story, advice, and stored remediation";

    /// <summary>The Alerts detail pane's no-selection state (also its initial text in the XAML).</summary>
    private const string AlertDetailPlaceholder = "select an alert to see its detail and dedup fingerprint";

    private ViewerDataService? _dataService;
    private DispatcherTimer? _refreshTimer;
    private bool _refreshInFlight;
    private bool _refreshRequested;

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
        ChartStyle.ApplyThemeToChart(CpuTrendChart);
        CpuTrendChart.Refresh();
        ChartStyle.ApplyThemeToChart(WaitCategoryChart);
        WaitCategoryChart.Refresh();

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
    /// leaving no tab stranded with stale or empty data. A trigger that arrives mid-load
    /// (the 60-second tick, an explicit Refresh, a mute/unmute reload) sets
    /// <see cref="_refreshRequested"/> instead of being dropped, so the running loop reloads
    /// once more when it finishes — a user action is never silently swallowed by the guard.
    /// </summary>
    private async Task RefreshSelectedServerAsync()
    {
        if (_dataService is null)
        {
            return;
        }

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

                DarlingServer? loadedServer;
                int loadedTab;
                do
                {
                    loadedServer = ServerList.SelectedItem as DarlingServer;
                    loadedTab = MainTabs.SelectedIndex;

                    if (loadedServer is null)
                    {
                        ClearAllTabs();
                        break;
                    }

                    await LoadTabAsync(loadedServer, loadedTab);
                }
                while (!ReferenceEquals(ServerList.SelectedItem, loadedServer) || MainTabs.SelectedIndex != loadedTab);
            }
            while (_refreshRequested);
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
                case AlertsTabIndex:
                    await LoadAlertsAsync(server);
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

        /* The three reads run concurrently — NpgsqlDataSource pools a connection for each. */
        var healthTask = _dataService!.GetCollectionHealthAsync(server.ServerId);
        var cpuTask = _dataService.GetCpuTrendAsync(server.ServerId, sinceUtc);
        var waitTask = _dataService.GetWaitCategoryTrendAsync(server.ServerId, sinceUtc);
        var health = await healthTask;
        var cpu = await cpuTask;
        var waits = await waitTask;

        HealthGrid.ItemsSource = health;
        RenderCpuTrend(cpu);
        RenderWaitCategoryTrend(waits);
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

    private async Task LoadAlertsAsync(DarlingServer server)
    {
        /* Preserve the selected alert across the 60-second refresh so the detail pane doesn't jump. */
        var selectedTime = (AlertsGrid.SelectedItem as ViewerAlertRow)?.AlertTime;

        var sinceUtc = DateTime.UtcNow.AddHours(-GetSelectedAlertHours());
        var rows = await _dataService!.GetAlertHistoryAsync(server.ServerId, sinceUtc);

        AlertsGrid.ItemsSource = rows;
        AlertsHintText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AlertsCountText.Text = rows.Count > 0 ? $"{rows.Count} alert(s)" : "";

        var reselect = selectedTime is null
            ? null
            : rows.FirstOrDefault(r => r.AlertTime == selectedTime.Value);
        if (reselect is not null)
        {
            AlertsGrid.SelectedItem = reselect;
        }
        else if (AlertsGrid.SelectedItem is null)
        {
            AlertDetailText.Text = AlertDetailPlaceholder;
        }
    }

    /// <summary>The Alerts time-range combo's hours-back tag (defaults to 24).</summary>
    private int GetSelectedAlertHours()
        => AlertsTimeRangeCombo.SelectedItem is ComboBoxItem item
            && item.Tag is string tag
            && int.TryParse(tag, out var hours)
            ? hours
            : 24;

    // ── Recommendations: finding mute / unmute (wave-3 write-back) ──────────────────

    /// <summary>Selects the row under a right-click so the context menu acts on it (both grids).</summary>
    private void Grid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null and not DataGridRow and not DataGridColumnHeader)
        {
            /* VisualTreeHelper.GetParent only walks Visuals; a ContentElement (e.g. an inline Run)
               would throw. Both grids host only TextBlocks today, but bail defensively. */
            if (source is not Visual)
            {
                return;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        if (source is DataGridRow row)
        {
            row.IsSelected = true;
        }
    }

    /// <summary>
    /// Enables Mute vs Unmute by the selected finding's state; suppresses the menu with no row.
    /// Unmute is offered only when the finding carries a per-server mute id — a globally-muted
    /// finding (MuteId null) shows as muted but is not unmutable here (that would affect all servers).
    /// </summary>
    private void FindingsGrid_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (FindingsGrid.SelectedItem is not ViewerFindingRow row)
        {
            e.Handled = true;
            return;
        }

        MuteFindingMenuItem.IsEnabled = !row.IsMuted;
        UnmuteFindingMenuItem.IsEnabled = row.MuteId is not null;
    }

    private async void MuteFinding_Click(object sender, RoutedEventArgs e)
    {
        if (_dataService is null
            || ServerList.SelectedItem is not DarlingServer server
            || FindingsGrid.SelectedItem is not ViewerFindingRow row
            || row.IsMuted)
        {
            return;
        }

        try
        {
            StatusText.Text = "Muting finding…";
            await _dataService.MuteFindingAsync(server.ServerId, row.Finding);
            await RefreshSelectedServerAsync();
            StatusText.Text = $"finding muted — {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"mute failed: {ex.Message}";
        }
    }

    private async void UnmuteFinding_Click(object sender, RoutedEventArgs e)
    {
        if (_dataService is null
            || FindingsGrid.SelectedItem is not ViewerFindingRow row
            || row.MuteId is not { } muteId)
        {
            return;
        }

        try
        {
            StatusText.Text = "Unmuting finding…";
            await _dataService.UnmuteFindingAsync(muteId);
            await RefreshSelectedServerAsync();
            StatusText.Text = $"finding unmuted — {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"unmute failed: {ex.Message}";
        }
    }

    // ── Alerts tab (wave-3 read surface + mute-rule launchers) ──────────────────────

    private void AlertsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, AlertsGrid))
        {
            return;
        }

        AlertDetailText.Text = AlertsGrid.SelectedItem is ViewerAlertRow row
            ? ViewerDataService.ComposeAlertDetailText(row)
            : AlertDetailPlaceholder;
    }

    private async void AlertsTimeRange_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            await RefreshSelectedServerAsync();
        }
    }

    private async void AlertsRefresh_Click(object sender, RoutedEventArgs e)
        => await RefreshSelectedServerAsync();

    private void ManageMuteRules_Click(object sender, RoutedEventArgs e)
    {
        if (_dataService is null)
        {
            return;
        }

        var window = new MuteRulesWindow(_dataService) { Owner = this };
        window.ShowDialog();
    }

    private async void CreateMuteRuleFromAlert_Click(object sender, RoutedEventArgs e)
    {
        if (_dataService is null
            || ServerList.SelectedItem is not DarlingServer server
            || AlertsGrid.SelectedItem is not ViewerAlertRow alert)
        {
            return;
        }

        /* Same shape as Lite's AlertsHistoryTab "Mute This Alert": seed a mute rule from the alert's
           server + metric + parsed detail-text context, let the user refine it, then persist. */
        var context = new AlertMuteContext
        {
            ServerName = server.ServerName,
            MetricName = alert.MetricName,
        };
        context.PopulateFromDetailText(alert.DetailText);

        var dialog = new MuteRuleEditDialog(context) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            await _dataService.InsertMuteRuleAsync(dialog.Rule);
            StatusText.Text = $"mute rule created — {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"mute rule save failed: {ex.Message}";
        }
    }

    private void ClearAllTabs()
    {
        HealthGrid.ItemsSource = null;
        RenderCpuTrend(Array.Empty<CpuTrendPoint>());
        RenderWaitCategoryTrend(Array.Empty<WaitCategoryTrendPoint>());
        QueriesGrid.ItemsSource = null;
        QueriesHintText.Visibility = Visibility.Collapsed;
        BlockingGrid.ItemsSource = null;
        BlockingHintText.Visibility = Visibility.Collapsed;
        FindingsGrid.ItemsSource = null;
        FindingsHintText.Visibility = Visibility.Collapsed;
        FindingDetailText.Text = FindingDetailPlaceholder;
        AlertsGrid.ItemsSource = null;
        AlertsHintText.Visibility = Visibility.Collapsed;
        AlertsCountText.Text = "";
        AlertDetailText.Text = AlertDetailPlaceholder;
    }

    private void RenderCpuTrend(IReadOnlyList<CpuTrendPoint> points)
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

    private void RenderWaitCategoryTrend(IReadOnlyList<WaitCategoryTrendPoint> points)
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
