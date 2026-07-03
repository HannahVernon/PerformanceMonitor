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

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Darling viewer shell (headless plan M3 + viewer waves 2-4, W0 IA inversion): the server list
/// from the central store on the left, and — mirroring Lite's navigation shape — ONE top tab strip on
/// the right holding the fixed aggregate tabs (Recommendations and Alerts, server-scoped via the
/// sidebar selection) plus dynamically-added, closable per-server tabs. Single-clicking a server
/// drives the aggregate tabs; double-clicking opens (or focuses) that server's <see cref="ViewerServerTab"/>,
/// whose inner tabs (Overview charts, Queries, Blocking, Collection Health) hold the per-server surfaces.
/// All reads go straight to Postgres via <see cref="ViewerDataService"/>. Loads are lazy per visible
/// tab (Lite's visible-only rule): the 60-second timer refreshes only the visible tab — an aggregate
/// tab for the selected server, or the visible server tab's active inner tab. Every data load is async.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF windows are never disposed by the framework; the data service is disposed in OnClosed.")]
public partial class MainWindow : Window
{
    private static readonly TimeSpan s_refreshInterval = TimeSpan.FromSeconds(60);

    /// <summary>The detail pane's no-selection state (also its initial text in the XAML).</summary>
    private const string FindingDetailPlaceholder = "select a finding to see its story, advice, and stored remediation";

    /// <summary>The Alerts detail pane's no-selection state (also its initial text in the XAML).</summary>
    private const string AlertDetailPlaceholder = "select an alert to see its detail and dedup fingerprint";

    private ViewerDataService? _dataService;
    private DispatcherTimer? _refreshTimer;
    private bool _refreshInFlight;
    private bool _refreshRequested;

    /// <summary>
    /// Open per-server tabs keyed by server id, so a double-click on an already-open server focuses its
    /// existing tab instead of opening a duplicate (Lite's dedupe-by-id rule).
    /// </summary>
    private readonly OpenServerTabRegistry<TabItem> _openServerTabs = new();

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

        await LoadServersAsync();

        /* --open-server <name>: deep-link straight into a server's per-server tab on startup —
           the same tab a double-click opens. Case-insensitive on the registered server name;
           an unknown name is ignored (the window still opens normally). */
        var openServer = OpenServerNameFromArgs();
        if (openServer is not null && ServerList.ItemsSource is IEnumerable<DarlingServer> loaded)
        {
            var match = loaded.FirstOrDefault(s =>
                string.Equals(s.ServerName, openServer, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                OpenServerTab(match);
            }
        }

        _refreshTimer = new DispatcherTimer { Interval = s_refreshInterval };
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();
    }

    /// <summary>
    /// First non-option command-line argument = explicit config path, mirroring the service
    /// (option pairs like --open-server &lt;name&gt; are skipped).
    /// </summary>
    private static string? ExplicitConfigPathFromArgs()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--open-server", StringComparison.OrdinalIgnoreCase))
            {
                i++; /* Skip the option's value too. */
                continue;
            }

            return args[i];
        }

        return null;
    }

    /// <summary>The value following --open-server, or null when absent/dangling.</summary>
    private static string? OpenServerNameFromArgs()
    {
        var args = Environment.GetCommandLineArgs();
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--open-server", StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs e)
        => await RefreshVisibleAsync();

    /// <summary>Single-click drives the aggregate tabs (Recommendations/Alerts) for the selected server.</summary>
    private async void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await RefreshVisibleAsync();

    /// <summary>Double-click opens (or focuses) the selected server's per-server tab (Lite's rule).</summary>
    private void ServerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ServerList.SelectedItem is DarlingServer server)
        {
            OpenServerTab(server);
        }
    }

    /// <summary>
    /// Lazy per-tab load: switching top-level tabs loads the newly visible one. SelectionChanged is a
    /// bubbling routed event, so selections inside the tab content (a findings grid, an inner server
    /// tab, the server list template) reach here too — only react to the top TabControl's own.
    /// </summary>
    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs))
        {
            return;
        }

        await RefreshVisibleAsync();
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

            var hasServers = servers.Count > 0;
            ServersHintText.Visibility = hasServers ? Visibility.Collapsed : Visibility.Visible;
            EmptyStatePanel.Visibility = hasServers ? Visibility.Collapsed : Visibility.Visible;
            MainTabs.Visibility = hasServers ? Visibility.Visible : Visibility.Collapsed;

            if (hasServers)
            {
                /* Triggers SelectionChanged, which loads the active aggregate tab for the first server. */
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
    /// Refreshes whichever top-level tab is visible, with the overlap guard: a per-server tab delegates
    /// to its own inner-tab refresh; the aggregate tabs reload for the sidebar-selected server. If the
    /// user switches tab or server while a load is in flight, the triggering event bounces off the guard
    /// and sets <see cref="_refreshRequested"/>, so the running loop reloads once more when it finishes —
    /// no user action is silently swallowed.
    /// </summary>
    private async Task RefreshVisibleAsync()
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
                await LoadVisibleTabAsync();
            }
            while (_refreshRequested);
        }
        finally
        {
            _refreshInFlight = false;
        }
    }

    private async Task LoadVisibleTabAsync()
    {
        try
        {
            switch (MainTabs.SelectedItem)
            {
                case TabItem { Content: ViewerServerTab serverTab }:
                    await serverTab.RefreshActiveInnerTabAsync();
                    break;
                case TabItem tab when ReferenceEquals(tab, RecommendationsTab):
                    await LoadRecommendationsAsync(ServerList.SelectedItem as DarlingServer);
                    break;
                case TabItem tab when ReferenceEquals(tab, AlertsTab):
                    await LoadAlertsAsync(ServerList.SelectedItem as DarlingServer);
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"refresh failed: {ex.Message}";
        }
    }

    // ── Per-server tabs (open / focus / close) ──────────────────────────────────────

    /// <summary>
    /// Opens the given server's per-server tab, or focuses it if already open (dedupe by server id).
    /// The new tab's <see cref="ViewerServerTab"/> loads its active inner tab on first show.
    /// </summary>
    private void OpenServerTab(DarlingServer server)
    {
        if (_dataService is null)
        {
            return;
        }

        if (_openServerTabs.TryGet(server.ServerId, out var existing))
        {
            MainTabs.SelectedItem = existing;
            return;
        }

        var serverTab = new ViewerServerTab(_dataService, server);
        serverTab.StatusChanged += OnServerTabStatusChanged;

        var tabItem = new TabItem
        {
            Header = CreateServerTabHeader(server),
            Content = serverTab
        };

        _openServerTabs.Add(server.ServerId, tabItem);
        MainTabs.Items.Add(tabItem);
        MainTabs.SelectedItem = tabItem;
    }

    /// <summary>Removes a per-server tab and unwires it. WPF selects an adjacent tab when the closed one was active.</summary>
    private void CloseServerTab(int serverId)
    {
        if (!_openServerTabs.TryGet(serverId, out var tab))
        {
            return;
        }

        if (tab.Content is ViewerServerTab serverTab)
        {
            serverTab.StatusChanged -= OnServerTabStatusChanged;
            /* One dispose path: tears down every chart hover helper the tab's partials own. */
            serverTab.Dispose();
        }

        MainTabs.Items.Remove(tab);
        _openServerTabs.Remove(serverId);
    }

    /// <summary>
    /// The per-server tab header: the server name plus a close button. Mirrors Lite's CreateTabHeader
    /// shape (minus the alert badge, which the viewer doesn't surface on tabs yet); the close affordance
    /// uses the shared theme's TabCloseButton style.
    /// </summary>
    private StackPanel CreateServerTabHeader(DarlingServer server)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        panel.Children.Add(new TextBlock
        {
            Text = server.DisplayName,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        });

        var closeButton = new Button
        {
            Cursor = Cursors.Hand,
            ToolTip = "Close tab"
        };
        if (TryFindResource("TabCloseButton") is Style closeStyle)
        {
            closeButton.Style = closeStyle;
        }
        else
        {
            closeButton.Content = "✕";
        }
        closeButton.Click += (s, e) => CloseServerTab(server.ServerId);
        panel.Children.Add(closeButton);

        return panel;
    }

    private void OnServerTabStatusChanged(string message) => StatusText.Text = message;

    // ── Recommendations tab (aggregate, server-scoped via the sidebar for now) ───────

    private async Task LoadRecommendationsAsync(DarlingServer? server)
    {
        if (_dataService is null)
        {
            return;
        }

        if (server is null)
        {
            FindingsGrid.ItemsSource = null;
            FindingsHintText.Visibility = Visibility.Collapsed;
            FindingsHeaderText.Text = "Latest Analysis Findings";
            FindingDetailText.Text = FindingDetailPlaceholder;
            return;
        }

        /* Remember the selected finding so the 60-second refresh doesn't yank the detail
           pane out from under the reader — reselect the same story if it's still there. */
        var selectedHash = (FindingsGrid.SelectedItem as ViewerFindingRow)?.Finding.StoryPathHash;

        var rows = await _dataService.GetLatestFindingsAsync(server.ServerId);

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

        StatusText.Text = $"{server.DisplayName} — refreshed {DateTime.Now:HH:mm:ss}";
    }

    // ── Alerts tab (aggregate, server-scoped via the sidebar for now) ────────────────

    private async Task LoadAlertsAsync(DarlingServer? server)
    {
        if (_dataService is null)
        {
            return;
        }

        if (server is null)
        {
            AlertsGrid.ItemsSource = null;
            AlertsHintText.Visibility = Visibility.Collapsed;
            AlertsCountText.Text = "";
            AlertDetailText.Text = AlertDetailPlaceholder;
            return;
        }

        /* Preserve the selected alert across the 60-second refresh so the detail pane doesn't jump. */
        var selectedTime = (AlertsGrid.SelectedItem as ViewerAlertRow)?.AlertTime;

        var sinceUtc = DateTime.UtcNow.AddHours(-GetSelectedAlertHours());
        var rows = await _dataService.GetAlertHistoryAsync(server.ServerId, sinceUtc);

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

        StatusText.Text = $"{server.DisplayName} — refreshed {DateTime.Now:HH:mm:ss}";
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
            await RefreshVisibleAsync();
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
            await RefreshVisibleAsync();
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
            await RefreshVisibleAsync();
        }
    }

    private async void AlertsRefresh_Click(object sender, RoutedEventArgs e)
        => await RefreshVisibleAsync();

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
