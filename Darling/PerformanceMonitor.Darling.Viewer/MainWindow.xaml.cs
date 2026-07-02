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
/// The Darling viewer shell (headless plan M3): the server list from the central store, the
/// per-collector Collection Health grid, and a top-8 wait-types trend chart, read straight from
/// Postgres via <see cref="ViewerDataService"/>. Selecting a server loads its health + chart;
/// a timer refreshes the selected server every 60 seconds. Every data load is async — the
/// Npgsql calls are awaited so the UI thread never blocks on a query — with results marshaled
/// back by the dispatcher's await continuation.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF windows are never disposed by the framework; the data service is disposed in OnClosed.")]
public partial class MainWindow : Window
{
    private static readonly TimeSpan s_refreshInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan s_waitTrendWindow = TimeSpan.FromHours(24);

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
                /* Triggers SelectionChanged, which loads health + chart for the first server. */
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

    private async Task RefreshSelectedServerAsync()
    {
        if (_dataService is null || _refreshInFlight)
        {
            return;
        }

        if (ServerList.SelectedItem is not DarlingServer server)
        {
            HealthGrid.ItemsSource = null;
            RenderWaitTrend(Array.Empty<WaitTrendPoint>());
            return;
        }

        _refreshInFlight = true;
        try
        {
            var sinceUtc = DateTime.UtcNow - s_waitTrendWindow;

            /* Both queries run concurrently — NpgsqlDataSource pools a connection for each. */
            var healthTask = _dataService.GetCollectionHealthAsync(server.ServerId);
            var waitTask = _dataService.GetWaitTrendAsync(server.ServerId, sinceUtc);
            var health = await healthTask;
            var waits = await waitTask;

            HealthGrid.ItemsSource = health;
            RenderWaitTrend(waits);
            StatusText.Text = $"{server.DisplayName} — refreshed {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"refresh failed: {ex.Message}";
        }
        finally
        {
            _refreshInFlight = false;
        }
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
