/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Linq;
using System.Xml.Linq;
using System.Net;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PerformanceMonitor.Notifications;
using PerformanceMonitorLite.Controls;
using PerformanceMonitorLite.Database;
using PerformanceMonitorLite.Mcp;
using PerformanceMonitorLite.Models;
using PerformanceMonitorLite.Services;
using PerformanceMonitorLite.Windows;
using PerformanceMonitor.Common;
using PerformanceMonitor.Ui;

namespace PerformanceMonitorLite;

public partial class MainWindow : Window
{
    private readonly DuckDbInitializer _databaseInitializer;
    private readonly ServerManager _serverManager;
    private readonly ProfileManager _profileManager;
    private readonly ScheduleManager _scheduleManager;
    private RemoteCollectorService? _collectorService;
    private CollectionBackgroundService? _backgroundService;
    private CancellationTokenSource? _backgroundCts;
    private SystemTrayService? _trayService;
    private WindowResumeGuard? _resumeGuard;
    private readonly Dictionary<string, TabItem> _openServerTabs = new();
    private readonly Dictionary<string, (Action<int, int, DateTime?> AlertCounts, Action<int> ApplyTimeRange, Func<Task> ManualRefresh)> _tabEventHandlers = new();
    private readonly Dictionary<string, bool> _previousConnectionStates = new();
    private readonly Dictionary<string, bool> _previousCollectorErrorStates = new();
    private readonly Dictionary<string, bool> _previousXeSessionFailureStates = new();
    private readonly Dictionary<string, DateTime> _lastCpuAlert = new();
    private readonly Dictionary<string, DateTime> _lastBlockingAlert = new();
    private readonly Dictionary<string, DateTime> _lastDeadlockAlert = new();
    private readonly Dictionary<string, DateTime> _lastPoisonWaitAlert = new();
    private readonly Dictionary<string, DateTime> _lastLongRunningQueryAlert = new();
    private readonly Dictionary<string, DateTime> _lastTempDbSpaceAlert = new();
    private readonly Dictionary<string, DateTime> _lastLowDiskAlert = new();
    private readonly Dictionary<string, DateTime> _lastLongRunningJobAlert = new();
    private readonly DispatcherTimer _statusTimer;
    private LocalDataService? _dataService;
    private McpHostService? _mcpService;
    private readonly AlertStateService _alertStateService = new();
    private readonly IAlertSettings _alertSettings = new AppAlertSettings();
    private readonly MuteRuleService _muteRuleService;
    private EmailAlertService _emailAlertService;

    /* Track active alert states for resolved notifications */
    private readonly Dictionary<string, bool> _activeCpuAlert = new();
    private readonly Dictionary<string, bool> _activeBlockingAlert = new();
    private readonly Dictionary<string, bool> _activeDeadlockAlert = new();
    private readonly Dictionary<string, bool> _activePoisonWaitAlert = new();
    private readonly Dictionary<string, bool> _activeLongRunningQueryAlert = new();
    private readonly Dictionary<string, bool> _activeTempDbSpaceAlert = new();
    private readonly Dictionary<string, bool> _activeLowDiskAlert = new();
    /* Worst free-% captured at the last low-disk alert per server (#754 follow-up): see the
       Dashboard counterpart. Without it a standing full volume re-fired — and re-recorded an
       alert-history row, defeating Dismiss — every cooldown. Gated by LowDiskAlertGate; removed on resolve. */
    private readonly Dictionary<string, double> _lastAlertedLowDiskPercent = new();
    private readonly Dictionary<string, bool> _activeLongRunningJobAlert = new();
    private readonly Dictionary<string, DateTime> _lastFailedJobAlert = new();
    /* Watermark of the most-recent failed-job run time already alerted per server. A failed run
       lingers in the lookback window for the whole window, so a plain level check would re-fire
       every cooldown; we only notify when a strictly newer failure appears. Bounded by server
       count, so no pruning needed. (Server-local run times mean a fall-back DST hour / NTP step
       could let one new failure tie the watermark and be skipped — a once-a-year, one-hour edge.) */
    private readonly Dictionary<string, DateTime> _lastAlertedFailedJobTime = new();

    /* Edge-trigger watermarks (#1091): the rolling 1-hour blocking/deadlock counts stay
       above the threshold for the whole hour an event lingers in the window, so a plain
       level check re-fires the same alert every cooldown. These hold the count at the last
       fired alert; we only re-notify when the count climbs past it (a genuinely new event),
       and reset to 0 when the window empties so the next event alerts again. */
    private readonly Dictionary<string, int> _lastAlertedBlockingCount = new();
    private readonly Dictionary<string, int> _lastAlertedDeadlockCount = new();

    public MainWindow()
    {
        InitializeComponent();

        // Initialize services (with loggers wired to AppLogger)
        _databaseInitializer = new DuckDbInitializer(App.DatabasePath, new AppLoggerAdapter<DuckDbInitializer>());
        /* Webhook service is constructed first and injected into the email service
           (Plan E E3c): the shared send core fans out to it. */
        var webhookAlertService = new WebhookAlertService(
            _alertSettings, EmailAlertService.Branding, new AppLoggerAdapter<WebhookAlertService>());
        _emailAlertService = new EmailAlertService(
            _alertSettings,
            new DuckDbAlertHistoryStore(_databaseInitializer),
            webhookAlertService,
            new AppLoggerAdapter<EmailAlertService>());
        _muteRuleService = new MuteRuleService(
            new DuckDbMuteRuleStore(_databaseInitializer),
            new AppLoggerAdapter<MuteRuleService>());
        _serverManager = new ServerManager(App.SharedConfigDirectory, logger: new AppLoggerAdapter<ServerManager>());
        // Two-phase wiring (§3.1): build the ProfileManager (one-way ServerManager injection for the
        // referential-integrity query), then late-inject it back as the ServerManager's IProfileLookup
        // so CheckConnectionAsync resolves profile-backed servers through the same fail-closed logic.
        // Coupling stays acyclic: ServerManager → IProfileLookup ← ProfileManager, ProfileManager → ServerManager.
        _profileManager = new ProfileManager(_serverManager, new AppLoggerAdapter<ProfileManager>());
        _serverManager.ProfileLookup = _profileManager;
        _scheduleManager = new ScheduleManager(App.ConfigDirectory);

        // Status bar update timer
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _statusTimer.Tick += async (s, e) =>
        {
            UpdateStatusBar();
            await RefreshOverviewAsync();
            CheckConnectionsAndNotify();

            /* Auto-refresh alert history if the tab is active */
            if (ServerTabControl.SelectedItem == AlertsTab)
                AlertsHistoryContent.RefreshAlerts();
        };

        // Initialize database and UI
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        ServerTabControl.SelectionChanged += ServerTabControl_SelectionChanged;
    }

    /// <summary>
    /// The one true window-restore path. Minimize-to-tray calls <see cref="Window.Hide"/>, which
    /// sets WPF <see cref="UIElement.Visibility"/> = Hidden. Only <see cref="Window.Show"/> reconciles
    /// that state and re-runs layout/render — a raw Win32 ShowWindow leaves the HWND visible but the
    /// WPF tree un-arranged, i.e. a blank window (#1050). Every restore entry point — tray double-click,
    /// the "Show Window" menu, the sleep/unlock resume guard, and the second-instance signal — routes
    /// here so the window can never be left visible-but-blank. Must be called on the UI thread.
    /// </summary>
    public void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Initializing database...";

            // Initialize the DuckDB database
            await _databaseInitializer.InitializeAsync();

            // Initialize the collection engine (with loggers wired to AppLogger)
            _collectorService = new RemoteCollectorService(
                _databaseInitializer,
                _serverManager,
                _scheduleManager,
                new AppLoggerAdapter<RemoteCollectorService>());

            var archiveService = new ArchiveService(_databaseInitializer, App.ArchiveDirectory, new AppLoggerAdapter<ArchiveService>());
            var retentionService = new RetentionService(App.ArchiveDirectory, new AppLoggerAdapter<RetentionService>());

            // Routes high-severity analysis findings to email/Slack/Teams; the background
            // service runs scheduled analysis and hands findings to it.
            /* serverId resolver: Lite uses the finding's stable int id as a string (Plan E E3c). */
            var analysisNotificationService = new AnalysisNotificationService(
                _emailAlertService, _alertSettings, f => f.ServerId.ToString(), new AppLoggerAdapter<AnalysisNotificationService>());

            _backgroundService = new CollectionBackgroundService(
                _collectorService, _databaseInitializer, archiveService, retentionService, _serverManager,
                analysisNotificationService,
                new AppLoggerAdapter<CollectionBackgroundService>());

            // Start background collection.
            // Off the UI thread on purpose: DuckDB.NET is synchronous and Lite has no
            // ConfigureAwait(false), so starting this from the Loaded handler would run the entire
            // collection/checkpoint/archive pipeline on the WPF dispatcher (per-minute jank, and a
            // multi-second-to-minutes freeze on archive/reset). A pool thread has no
            // SynchronizationContext, so StartAsync and every subsequent continuation stay off-UI.
            // Safe: the pipeline only touches DuckDB + the email/webhook notification service; the
            // UI reads data by polling DuckDB on its own timers, fully decoupled.
            _backgroundCts = new CancellationTokenSource();
            _ = Task.Run(() => _backgroundService.StartAsync(_backgroundCts.Token));

            // Initialize system tray
            _trayService = new SystemTrayService(this, RestoreFromTray, _backgroundService);
            _trayService.Initialize();

            /* #1050: restore the window from the tray on resume/unlock if a sleep- or lock-driven
               minimize hid it. ??= so a repeated Loaded can't double-subscribe (static SystemEvents). */
            _resumeGuard ??= new WindowResumeGuard(this, RestoreFromTray);

            // Initialize data service for overview
            _dataService = new LocalDataService(_databaseInitializer);

            // Load mute rules from database
            await _muteRuleService.LoadAsync();

            // Initialize alerts history tab
            AlertsHistoryContent.Initialize(_dataService);
            AlertsHistoryContent.MuteRuleService = _muteRuleService;
            AlertsHistoryContent.AlertsDismissed += OnAlertHistoryDismissed;

            // Initialize FinOps tab
            FinOpsContent.Initialize(_dataService, _serverManager);

            // Initialize Recommendations tab (advise-only)
            RecommendationsContent.Initialize(_databaseInitializer, _serverManager);

            // Start MCP server if enabled
            await StartMcpServerAsync();

            // Load servers
            RefreshServerList();

            // Update status
            UpdateStatusBar();
            _statusTimer.Start();

            await RefreshOverviewAsync();
            StatusText.Text = "Ready - Collection active";

            _ = CheckForUpdatesOnStartupAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            MessageBox.Show(
                $"Failed to initialize the application:\n\n{ex.Message}",
                "Initialization Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        try
        {
            await Task.Delay(5000); // Don't slow down startup

            if (!App.CheckForUpdatesOnStartup) return;

            // Try Velopack first (supports download + apply)
            try
            {
                var mgr = new Velopack.UpdateManager(
                    new Velopack.Sources.GithubSource(
                        "https://github.com/erikdarlingdata/PerformanceMonitor", null, false));

                var newVersion = await mgr.CheckForUpdatesAsync();
                if (newVersion != null)
                {
                    Dispatcher.Invoke(() =>
                    {
                        Title = $"Performance Monitor Lite — Update v{newVersion.TargetFullRelease.Version} available (Help > About)";
                    });
                    return;
                }
            }
            catch
            {
                // Velopack packages may not exist yet — fall through
            }

            // Fallback: GitHub Releases API check
            var result = await UpdateCheckService.CheckForUpdateAsync();
            if (result?.IsUpdateAvailable == true)
            {
                Dispatcher.Invoke(() =>
                {
                    Title = $"Performance Monitor Lite — Update {result.LatestVersion} available (Help > About)";
                });
            }
        }
        catch
        {
            // Never crash on update check failure
        }
    }

    private bool _closingCleanupStarted;
    private bool _closingCleanupDone;

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        /* async void Closing handler: at the first await WPF would otherwise proceed to close the
           window — and since this is the last window, begin app shutdown — so the cleanup
           continuations below could be abandoned mid-flight (the graceful collector stop was
           effectively dead on the common close path). Cancel this close, run the cleanup to
           completion, then Close() again; the second pass returns early and closes for real. */
        if (_closingCleanupDone) return;
        e.Cancel = true;
        if (_closingCleanupStarted) return;
        _closingCleanupStarted = true;

        // Dispose system tray
        _resumeGuard?.Dispose();
        _trayService?.Dispose();

        // Stop background collection with timeout
        _backgroundCts?.Cancel();

        await StopMcpServerAsync();

        if (_backgroundService != null)
        {
            try
            {
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _backgroundService.StopAsync(shutdownCts.Token);
            }
            catch (OperationCanceledException)
            {
                /* Shutdown timed out, proceeding anyway */
            }
        }

        // Stop all server tab refresh timers
        foreach (var tab in _openServerTabs.Values)
        {
            if (tab.Content is ServerTab serverTab)
            {
                serverTab.StopRefresh();
            }
        }

        _statusTimer.Stop();

        _closingCleanupDone = true;

        /* Re-close on the next dispatcher cycle, not synchronously here. If the awaits above all
           completed without ever suspending (MCP off, collector already idle), we're still inside
           WPF's Closing event with Window._isClosing == true, and a synchronous Close() re-enters
           InternalClose → VerifyNotClosing() throws "Cannot ... call Close ... while a Window is
           closing" (#1050 follow-up). BeginInvoke lets this Closing event fully unwind — clearing
           _isClosing — before the real close runs; the second pass returns early on
           _closingCleanupDone and the window closes for real. */
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void ServerTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Only respond to tab selection changes, not child control selection events that bubble up
        if (e.OriginalSource != ServerTabControl) return;

        /* Restore the selected tab's UTC offset so charts use the correct server timezone */
        if (ServerTabControl.SelectedItem is TabItem { Content: ServerTab serverTab })
        {
            ServerTimeHelper.UtcOffsetMinutes = serverTab.UtcOffsetMinutes;
            StatusText.Text = $"Connected to {serverTab.Server.DisplayNameWithIntent}";
        }

        /* Refresh alerts tab when selected */
        if (ServerTabControl.SelectedItem == AlertsTab)
        {
            AlertsHistoryContent.RefreshAlerts();
        }

        /* Refresh recommendations tab when selected (picks up newly-collected findings) */
        if (ServerTabControl.SelectedItem == RecommendationsTab)
        {
            _ = RecommendationsContent.RefreshDataAsync();
        }

        UpdateCollectorHealth();
    }

    private async Task StartMcpServerAsync()
    {
        var mcpSettings = McpSettings.Load(App.ConfigDirectory);
        if (!mcpSettings.Enabled) return;

        try
        {
            bool portInUse = await PortUtilityService.IsTcpPortListeningAsync(mcpSettings.Port, IPAddress.Loopback);
            if (portInUse)
            {
                AppLogger.Error("MCP", $"Port {mcpSettings.Port} is already in use — MCP server not started");
                return;
            }

            _mcpService = new McpHostService(_dataService!, _serverManager, _muteRuleService, _databaseInitializer, mcpSettings.Port);
            _ = _mcpService.StartAsync(_backgroundCts!.Token);
        }
        catch (Exception ex)
        {
            AppLogger.Error("MCP", $"Failed to start MCP server: {ex.Message}");
        }
    }

    private async Task StopMcpServerAsync()
    {
        if (_mcpService != null)
        {
            try
            {
                using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _mcpService.StopAsync(shutdownCts.Token);
            }
            catch (OperationCanceledException)
            {
                /* MCP shutdown timed out */
            }
            _mcpService = null;
        }
    }

    private void RefreshServerList()
    {
        var servers = _serverManager.GetAllServers();
        foreach (var server in servers)
        {
            server.IsOnline = _serverManager.GetConnectionStatus(server.Id).IsOnline;
            server.HasCollectorErrors = _collectorService != null
                && server.IsOnline == true
                && _collectorService.GetHealthSummary(server).ErroringCollectors > 0;
        }
        ServerListView.ItemsSource = servers;

        // Update UI based on server count
        if (servers.Count == 0 && _openServerTabs.Count == 0)
        {
            EmptyStatePanel.Visibility = Visibility.Visible;
            ServerTabControl.Visibility = Visibility.Collapsed;
        }
        else
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            ServerTabControl.Visibility = Visibility.Visible;
        }

        ServerCountText.Text = $"Servers: {servers.Count}";

        // Refresh FinOps server dropdown when server list changes
        FinOpsContent.RefreshServerList();
        RecommendationsContent.RefreshServerList();

        // Refresh overview when server list changes
        _ = RefreshOverviewAsync();
    }

    private void UpdateStatusBar()
    {
        // Update database size
        var fileSizeMb = _databaseInitializer.GetDatabaseSizeMb();
        var usedSizeMb = _databaseInitializer.GetUsedDataSizeMb();
        if (fileSizeMb > 0)
        {
            DatabaseSizeText.Text = usedSizeMb.HasValue
                ? $"Database: {usedSizeMb.Value:F1} / {fileSizeMb:F1} MB"
                : $"Database: {fileSizeMb:F1} MB";
        }
        else
        {
            DatabaseSizeText.Text = "Database: New";
        }

        // Update collection status
        if (_backgroundService != null)
        {
            if (_backgroundService.IsCollecting)
            {
                CollectionStatusText.Text = "Collection: Running";
            }
            else if (_backgroundService.IsPaused)
            {
                CollectionStatusText.Text = "Collection: Paused";
            }
            else if (_backgroundService.LastCollectionTime.HasValue)
            {
                var ago = DateTime.UtcNow - _backgroundService.LastCollectionTime.Value;
                CollectionStatusText.Text = $"Collection: {ago.TotalSeconds:F0}s ago";
            }
            else
            {
                CollectionStatusText.Text = "Collection: Starting...";
            }
        }
        else
        {
            CollectionStatusText.Text = "Collection: Stopped";
        }

        // Update collector health
        UpdateCollectorHealth();
    }

    private void UpdateCollectorHealth()
    {
        if (_collectorService == null)
        {
            CollectorHealthText.Text = "";
            return;
        }

        int? selectedServerId = null;
        if (ServerTabControl.SelectedItem is TabItem { Content: ServerTab serverTab })
        {
            selectedServerId = serverTab.ServerId;
        }

        var health = _collectorService.GetHealthSummary(selectedServerId);

        if (health.TotalCollectors == 0)
        {
            CollectorHealthText.Text = "";
            return;
        }

        if (health.LoggingFailures > 0)
        {
            CollectorHealthText.Text = $"Logging: BROKEN ({health.LoggingFailures} failures)";
            CollectorHealthText.Foreground = System.Windows.Media.Brushes.Red;
            CollectorHealthText.ToolTip = $"collection_log INSERT is failing.\nThis means collector errors are invisible.\nCheck the log file for details.";
        }
        else if (health.ErroringCollectors > 0)
        {
            var names = string.Join(", ", health.Errors.Select(e => e.CollectorName));
            CollectorHealthText.Text = $"Collectors: {health.ErroringCollectors} erroring";
            CollectorHealthText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            CollectorHealthText.ToolTip = $"Failing: {names}\n\n" +
                string.Join("\n", health.Errors.Select(e =>
                    $"{e.CollectorName}: {e.ConsecutiveErrors}x consecutive - {e.LastErrorMessage}"));
        }
        else if (health.XeSessionFailures.Count > 0)
        {
            /* XE session couldn't be created (#1086). Permission failures don't
               increment ConsecutiveErrors, so without this branch the status bar
               would show OK while blocking/deadlock capture is dead. */
            var names = string.Join(", ", health.XeSessionFailures.Select(e => e.CollectorName));
            CollectorHealthText.Text = $"Capture down: {names}";
            CollectorHealthText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            CollectorHealthText.ToolTip = string.Join("\n", health.XeSessionFailures.Select(e =>
                $"{e.CollectorName}: {e.XeSessionMessage}"));
        }
        else
        {
            CollectorHealthText.Text = $"Collectors: {health.TotalCollectors} OK";
            CollectorHealthText.Foreground = (System.Windows.Media.Brush)FindResource("ForegroundBrush");
            CollectorHealthText.ToolTip = null;
        }
    }

    private async Task RefreshOverviewAsync()
    {
        if (_dataService == null) return;

        var servers = _serverManager.GetAllServers();
        if (servers.Count == 0) return;

        try
        {
            var summaries = new List<ServerSummaryItem>();
            foreach (var server in servers)
            {
                try
                {
                    var serverId = RemoteCollectorService.GetDeterministicHashCode(RemoteCollectorService.GetServerNameForStorage(server));
                    var summary = await Task.Run(() => _dataService.GetServerSummaryAsync(serverId, server.DisplayNameWithIntent));
                    if (summary != null)
                    {
                        summary.ServerName = server.ServerName;
                        var connStatus = _serverManager.GetConnectionStatus(server.Id);
                        summary.IsOnline = connStatus.IsOnline;
                        if (_collectorService != null && connStatus.IsOnline == true)
                            summary.HasCollectorErrors = _collectorService.GetHealthSummary(server).ErroringCollectors > 0;
                        summaries.Add(summary);
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Info("Overview", $"Failed to get summary for {server.DisplayName}: {ex.Message}");
                }
            }

            OverviewItemsControl.ItemsSource = summaries;

            foreach (var summary in summaries)
            {
                CheckPerformanceAlerts(summary);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Info("Overview", $"RefreshOverviewAsync failed: {ex.Message}");
        }
    }

    private void ServerListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ServerListView.SelectedItem is ServerConnection server)
        {
            ConnectToServer(server);
        }
    }

    private void OverviewCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement fe && fe.DataContext is ServerSummaryItem summary)
        {
            var server = _serverManager.GetAllServers()
                .FirstOrDefault(s => s.ServerName == summary.ServerName);
            if (server != null)
            {
                ConnectToServer(server);
            }
        }
    }

    private async void ConnectToServer(ServerConnection server)
    {
        // Check if tab already open
        if (_openServerTabs.TryGetValue(server.Id, out var existingTab))
        {
            ServerTabControl.SelectedItem = existingTab;
            return;
        }

        // Clear MFA cancellation flag when user explicitly connects
        // This gives them a fresh attempt at authentication
        var currentStatus = _serverManager.GetConnectionStatus(server.Id);
        if (server.AuthenticationType == AuthenticationTypes.EntraMFA && currentStatus.UserCancelledMfa)
        {
            currentStatus.UserCancelledMfa = false;
            StatusText.Text = "Retrying MFA authentication...";
        }

        // Ensure connection status is populated with UTC offset before opening tab
        // This is critical for timezone-correct chart display
        var status = _serverManager.GetConnectionStatus(server.Id);
        if (!status.UtcOffsetMinutes.HasValue)
        {
            StatusText.Text = "Checking server connection...";
            // Allow interactive auth (MFA) when user explicitly opens a server
            status = await _serverManager.CheckConnectionAsync(server.Id, allowInteractiveAuth: true);
        }

        var utcOffset = status.UtcOffsetMinutes ?? 0;
        var serverTab = new ServerTab(server, _databaseInitializer, _serverManager.CredentialResolver, utcOffset, status.HasMsdbAccess, status.SqlEngineEdition == 5);
        var tabHeader = CreateTabHeader(server);
        var tabItem = new TabItem
        {
            Header = tabHeader,
            Content = serverTab
        };

        /* Subscribe to events — store handlers so we can unsubscribe on tab close */
        var serverId = server.Id;
        Action<int, int, DateTime?> alertHandler = (blockingCount, deadlockCount, latestEventTime) =>
        {
            Dispatcher.Invoke(() => UpdateTabBadge(tabHeader, serverId, blockingCount, deadlockCount, latestEventTime));
        };
        Action<int> timeRangeHandler = (selectedIndex) =>
        {
            Dispatcher.Invoke(() =>
            {
                foreach (var tab in _openServerTabs.Values)
                {
                    if (tab.Content is ServerTab st && st != serverTab)
                    {
                        st.SetTimeRangeIndex(selectedIndex);
                    }
                }
            });
        };
        Func<Task> refreshHandler = async () =>
        {
            if (_collectorService != null)
            {
                var onLoadCollectors = _scheduleManager.GetOnLoadCollectorsForServer(server.Id);
                foreach (var collector in onLoadCollectors)
                {
                    try
                    {
                        await _collectorService.RunCollectorAsync(server, collector.Name);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Info("MainWindow", $"Re-collection of {collector.Name} failed: {ex.Message}");
                    }
                }
            }
        };

        serverTab.AlertCountsChanged += alertHandler;
        serverTab.ApplyTimeRangeRequested += timeRangeHandler;
        serverTab.ManualRefreshRequested += refreshHandler;
        _tabEventHandlers[server.Id] = (alertHandler, timeRangeHandler, refreshHandler);

        _openServerTabs[server.Id] = tabItem;
        ServerTabControl.Items.Add(tabItem);
        ServerTabControl.SelectedItem = tabItem;

        // Show the tab control, hide empty state
        EmptyStatePanel.Visibility = Visibility.Collapsed;
        ServerTabControl.Visibility = Visibility.Visible;

        _serverManager.UpdateLastConnected(server.Id);

        // Show existing historical data immediately
        serverTab.RefreshData();

        // Then collect fresh data and refresh again
        if (_collectorService != null)
        {
            StatusText.Text = $"Collecting data from {server.DisplayNameWithIntent}...";
            try
            {
                await Task.Run(() => _collectorService.RunAllCollectorsForServerAsync(server));
                StatusText.Text = $"Connected to {server.DisplayNameWithIntent} - Data loaded";
                serverTab.RefreshData();
                UpdateCollectorHealth();
                _ = RefreshOverviewAsync();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Connected to {server.DisplayNameWithIntent} - Collection error: {ex.Message}";
            }
        }
        else
        {
            StatusText.Text = $"Connected to {server.DisplayNameWithIntent}";
        }
    }

    private StackPanel CreateTabHeader(ServerConnection server)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        var tabLabel = server.ReadOnlyIntent ? $"{server.DisplayName} (RO)" : server.DisplayName;
        panel.Children.Add(new TextBlock
        {
            Text = tabLabel,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0)
        });

        /* Alert badge - hidden by default, shown when blocking/deadlocks detected */
        var badge = new System.Windows.Controls.Border
        {
            Background = System.Windows.Media.Brushes.OrangeRed,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        badge.Tag = "AlertBadge";

        /* Add context menu to badge for acknowledge/silence functionality */
        var serverId = server.Id;
        var contextMenu = new ContextMenu();

        var acknowledgeItem = new MenuItem
        {
            Header = "Acknowledge Alert",
            Tag = serverId,
            Icon = new TextBlock { Text = "✓", FontWeight = FontWeights.Bold }
        };
        acknowledgeItem.Click += AcknowledgeServerAlert_Click;

        var silenceItem = new MenuItem
        {
            Header = "Silence This Server",
            Tag = serverId,
            Icon = new TextBlock { Text = "🔇" }
        };
        silenceItem.Click += SilenceServer_Click;

        var unsilenceItem = new MenuItem
        {
            Header = "Unsilence",
            Tag = serverId,
            Icon = new TextBlock { Text = "🔔" }
        };
        unsilenceItem.Click += UnsilenceServer_Click;

        contextMenu.Items.Add(acknowledgeItem);
        contextMenu.Items.Add(silenceItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(unsilenceItem);

        /* Update menu items based on state when opened */
        contextMenu.Opened += (s, args) =>
        {
            var isSilenced = _alertStateService.IsServerSilenced(serverId);
            var hasAlert = badge.Visibility == Visibility.Visible;

            acknowledgeItem.IsEnabled = hasAlert;
            silenceItem.IsEnabled = !isSilenced;
            unsilenceItem.IsEnabled = isSilenced;
        };

        badge.ContextMenu = contextMenu;

        /* Left-click the badge to acknowledge/clear it — the right-click menu was
           undiscoverable, so a plain click is the obvious affordance (issue #1092). */
        badge.MouseLeftButtonUp += (s, e) =>
        {
            AcknowledgeServerBadge(serverId);
            e.Handled = true;
        };

        panel.Children.Add(badge);

        var closeButton = new Button
        {
            Content = "x",
            FontSize = 10,
            Padding = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand
        };
        closeButton.Click += (s, e) => CloseServerTab(server.Id);
        panel.Children.Add(closeButton);

        return panel;
    }

    private void UpdateTabBadge(StackPanel tabHeader, string serverId, int blockingCount, int deadlockCount, DateTime? latestEventTime)
    {
        var totalAlerts = blockingCount + deadlockCount;

        /* Delegate count tracking and acknowledgement clearing to AlertStateService.
           Uses latestEventTime to only clear ack when genuinely new events arrive,
           not when the user just switches time ranges. */
        bool shouldShow = _alertStateService.UpdateAlertCounts(serverId, blockingCount, deadlockCount, latestEventTime);

        foreach (var child in tabHeader.Children)
        {
            if (child is System.Windows.Controls.Border border && border.Tag as string == "AlertBadge")
            {
                if (shouldShow)
                {
                    border.Visibility = Visibility.Visible;
                    border.Background = deadlockCount > 0
                        ? System.Windows.Media.Brushes.Red
                        : System.Windows.Media.Brushes.OrangeRed;

                    if (border.Child is TextBlock text)
                    {
                        text.Text = totalAlerts > 99 ? "99+" : totalAlerts.ToString();
                        text.ToolTip = $"Blocking: {blockingCount}, Deadlocks: {deadlockCount}\nClick to dismiss · Right-click for options";
                    }
                }
                else
                {
                    border.Visibility = Visibility.Collapsed;
                }
                break;
            }
        }
    }

    private void AcknowledgeServerAlert_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string serverId)
        {
            AcknowledgeServerBadge(serverId);
        }
    }

    /// <summary>
    /// Acknowledges a server's alerts and immediately hides its tab badge.
    /// Shared by the badge left-click, the right-click "Acknowledge" menu, and
    /// Alert History "Dismiss All" so every path clears the badge consistently (issue #1092).
    /// </summary>
    private void AcknowledgeServerBadge(string serverId)
    {
        _alertStateService.AcknowledgeAlert(serverId);
        HideServerBadge(serverId);
    }

    /// <summary>
    /// Collapses the alert badge on a server's tab header, if one is present.
    /// </summary>
    private void HideServerBadge(string serverId)
    {
        if (_openServerTabs.TryGetValue(serverId, out var tab) && tab.Header is StackPanel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is System.Windows.Controls.Border border && border.Tag as string == "AlertBadge")
                {
                    border.Visibility = Visibility.Collapsed;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// When alerts are cleared from Alert History via "Dismiss All", acknowledge the matching
    /// server tab badge(s) so the at-a-glance indicator stays consistent with the cleared list
    /// (issue #1092). The argument is the DB server_id filter that was in effect; null means the
    /// list spanned all servers, so every open tab is acknowledged. The badge tracks blocking/
    /// deadlock counts (a separate system from the notification alerts the list shows), so this
    /// uses the same acknowledge-until-new-event semantics as the badge's own context menu.
    /// </summary>
    private void OnAlertHistoryDismissed(int? dbServerId)
    {
        foreach (var kvp in _openServerTabs)
        {
            if (kvp.Value.Content is ServerTab st && (dbServerId == null || st.ServerId == dbServerId.Value))
            {
                AcknowledgeServerBadge(kvp.Key);
            }
        }
    }

    private void SilenceServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string serverId)
        {
            _alertStateService.SilenceServer(serverId);

            /* Find and hide the badge for this server */
            if (_openServerTabs.TryGetValue(serverId, out var tab) && tab.Header is StackPanel panel)
            {
                foreach (var child in panel.Children)
                {
                    if (child is System.Windows.Controls.Border border && border.Tag as string == "AlertBadge")
                    {
                        border.Visibility = Visibility.Collapsed;
                        break;
                    }
                }
            }
        }
    }

    private void UnsilenceServer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.Tag is string serverId)
        {
            _alertStateService.UnsilenceServer(serverId);

            /* The next refresh cycle will show the badge if there are alerts */
        }
    }

    private void CloseServerTab(string serverId)
    {
        if (_openServerTabs.TryGetValue(serverId, out var tab))
        {
            if (tab.Content is ServerTab serverTab)
            {
                /* Unsubscribe event handlers to prevent memory leaks */
                if (_tabEventHandlers.TryGetValue(serverId, out var handlers))
                {
                    serverTab.AlertCountsChanged -= handlers.AlertCounts;
                    serverTab.ApplyTimeRangeRequested -= handlers.ApplyTimeRange;
                    serverTab.ManualRefreshRequested -= handlers.ManualRefresh;
                    _tabEventHandlers.Remove(serverId);
                }

                serverTab.StopRefresh();
                serverTab.DisposeChartHelpers();

                /* Clear delta cache for this server to free memory */
                _collectorService?.DeltaCalculator?.ClearServer(serverTab.ServerId);
            }

            ServerTabControl.Items.Remove(tab);
            _openServerTabs.Remove(serverId);

            /* Clean up alert state for this server */
            _alertStateService.RemoveServerState(serverId);

            // Show empty state if no tabs open
            if (_openServerTabs.Count == 0)
            {
                var servers = _serverManager.GetAllServers();
                if (servers.Count == 0)
                {
                    EmptyStatePanel.Visibility = Visibility.Visible;
                    ServerTabControl.Visibility = Visibility.Collapsed;
                }
            }
        }
    }

    private void AddServerButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AddServerDialog(_serverManager, _profileManager) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.AddedServer != null)
        {
            RefreshServerList();
            StatusText.Text = $"Added server: {dialog.AddedServer.DisplayNameWithIntent}";
        }
    }

    private void ManageServersButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new ManageServersWindow(_serverManager, _profileManager) { Owner = this };
        window.ShowDialog();

        if (window.ServersChanged)
        {
            // Purge collector health for servers that were removed
            if (_collectorService != null)
            {
                var currentServerIds = new HashSet<int>(
                    _serverManager.GetAllServers().Select(s =>
                        RemoteCollectorService.GetDeterministicHashCode(
                            RemoteCollectorService.GetServerNameForStorage(s))));
                _collectorService.ClearHealthExcept(currentServerIds);
            }

            RefreshServerList();
        }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_scheduleManager, _serverManager, _backgroundService, _mcpService, _muteRuleService) { Owner = this };
        window.ShowDialog();
        UpdateStatusBar();

        if (window.McpSettingsChanged)
        {
            await StopMcpServerAsync();
            await StartMcpServerAsync();
        }
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var window = new Windows.AboutWindow { Owner = this };
        window.ShowDialog();
    }

    private void ViewLogButton_Click(object sender, RoutedEventArgs e)
    {
        var logFile = AppLogger.GetCurrentLogFile();
        try
        {
            if (System.IO.File.Exists(logFile))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = logFile,
                    UseShellExecute = true
                });
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = AppLogger.GetLogDirectory(),
                    UseShellExecute = true
                });
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open log file: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var logDir = AppLogger.GetLogDirectory();
        try
        {
            System.IO.Directory.CreateDirectory(logDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = logDir,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open log folder: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Previous Lite Install Folder"
        };

        if (dialog.ShowDialog() != true) return;

        var oldConfigDir = System.IO.Path.Combine(dialog.FolderName, "config");
        var serversJsonPath = System.IO.Path.Combine(oldConfigDir, "servers.json");
        if (!System.IO.File.Exists(serversJsonPath))
        {
            MessageBox.Show(
                "No config\\servers.json found in the selected folder.\n\nSelect the root folder of a previous Lite installation.",
                "Import Settings",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            // Import server connections (upsert by server name)
            var (imported, skipped) = _serverManager.ImportServersFromFile(serversJsonPath);

            // Import credential profiles from the SHARED config dir (M-1: NOT the per-user copy loop
            // below — profiles.json, like servers.json, lives in App.SharedConfigDirectory, so it must
            // be imported via ProfileManager which is backed by that dir). Source path is built from
            // the SAME oldConfigDir variable serversJsonPath uses (M1-R2).
            int profilesImported = 0;
            var profilesJsonPath = System.IO.Path.Combine(oldConfigDir, "profiles.json");
            if (System.IO.File.Exists(profilesJsonPath))
            {
                try
                {
                    (profilesImported, _) = _profileManager.ImportProfilesFromFile(profilesJsonPath);
                }
                catch (Exception pex)
                {
                    AppLogger.Warn("Import", $"Failed to import profiles.json: {pex.Message}");
                }
            }

            // Copy config files that don't already exist in the current install
            var settingsFiles = new[] { "settings.json", "collection_schedule.json", "ignored_wait_types.json" };
            int settingsCopied = 0;

            foreach (var fileName in settingsFiles)
            {
                var source = System.IO.Path.Combine(oldConfigDir, fileName);
                var target = System.IO.Path.Combine(App.ConfigDirectory, fileName);

                if (System.IO.File.Exists(source) && !System.IO.File.Exists(target))
                {
                    System.IO.File.Copy(source, target);
                    settingsCopied++;
                }
            }

            // Copy alert_state.json from old root directory
            var oldAlertState = System.IO.Path.Combine(dialog.FolderName, "alert_state.json");
            var currentAlertState = System.IO.Path.Combine(App.DataDirectory, "alert_state.json");
            if (System.IO.File.Exists(oldAlertState) && !System.IO.File.Exists(currentAlertState))
            {
                System.IO.File.Copy(oldAlertState, currentAlertState);
                settingsCopied++;
            }

            var message = $"Imported {imported} server connection(s).";
            if (skipped > 0)
                message += $"\nSkipped {skipped} duplicate(s) (already configured).";
            if (profilesImported > 0)
                message += $"\nImported {profilesImported} credential profile(s).";
            if (settingsCopied > 0)
                message += $"\nCopied {settingsCopied} settings file(s).";
            if (imported > 0)
                message += "\n\nCredentials from the previous install are preserved.\nIf any connections fail to authenticate, re-enter the password in Manage Servers.";
            if (profilesImported > 0)
                message += "\n\nCredential profile secrets are NOT importable (they live only in Windows Credential Manager, per user).\nEdit each imported profile once in Manage Servers → Credential Profiles to re-enter its secret.";
            if (settingsCopied > 0)
                message += "\n\nRestart the application to apply imported settings.";

            MessageBox.Show(message, "Import Settings", MessageBoxButton.OK, MessageBoxImage.Information);

            if (imported > 0)
                RefreshServerList();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to import settings: {ex.Message}", "Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ImportDataButton_Click(object sender, RoutedEventArgs e)
    {
        /* Open folder browser to select the old Lite install directory */
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select Previous Lite Install Folder",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
        {
            return;
        }

        var sourceFolder = dialog.FolderName;

        /* Validate that monitor.duckdb exists in the selected folder */
        if (!DataImportService.ValidateSourceFolder(sourceFolder))
        {
            MessageBox.Show(
                "The selected folder does not contain a monitor.duckdb file.\n\n" +
                "Please select the folder where the previous Lite application was installed.",
                "Invalid Folder",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        /* Prevent double-clicks */
        ImportDataButton.IsEnabled = false;
        ImportDataButtonText.Text = "Importing...";
        StatusText.Text = "Importing data from previous install...";

        try
        {
            var importService = new DataImportService(_databaseInitializer, App.ArchiveDirectory);

            /* The tryLockOldDb callback runs on the UI thread to show the retry dialog */
            var result = await Task.Run(async () =>
                await importService.RunImportAsync(sourceFolder, async _ =>
                {
                    var answer = MessageBoxResult.Cancel;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        answer = MessageBox.Show(
                            "Could not lock the database to flush current data.\n\n" +
                            "Close the previous Lite application and click OK to try again.",
                            "Database Locked",
                            MessageBoxButton.OKCancel,
                            MessageBoxImage.Warning);
                    });
                    return answer == MessageBoxResult.OK;
                }));

            if (result.Success)
            {
                StatusText.Text = "Import complete — refreshing views...";
                await _serverManager.CheckAllConnectionsAsync();
                RefreshServerList();
                UpdateStatusBar();
                StatusText.Text = "Import complete";

                MessageBox.Show(
                    $"Import completed successfully.\n\n" +
                    $"Tables flushed from old database: {result.TablesFlushed}\n" +
                    $"Parquet files imported: {result.FilesImported}\n\n" +
                    "Historical data is now available in all views.",
                    "Import Complete",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            else
            {
                StatusText.Text = "Import cancelled or failed";
                if (!string.IsNullOrEmpty(result.ErrorMessage))
                {
                    MessageBox.Show(
                        result.ErrorMessage,
                        "Import Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("DataImport", "Unhandled import error", ex);
            StatusText.Text = "Import failed";
            MessageBox.Show(
                $"An unexpected error occurred during import:\n\n{ex.Message}",
                "Import Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            ImportDataButton.IsEnabled = true;
            ImportDataButtonText.Text = "Import Data";
        }
    }

    /// <summary>
    /// Gets the ServerConnection from a context menu click on a server list item.
    /// </summary>
    private ServerConnection? GetServerFromContextMenu(object sender)
    {
        if (sender is not MenuItem menuItem) return null;
        var contextMenu = menuItem.Parent as ContextMenu;
        var border = contextMenu?.PlacementTarget as FrameworkElement;
        return border?.DataContext as ServerConnection;
    }

    private void ServerContextMenu_Connect_Click(object sender, RoutedEventArgs e)
    {
        var server = GetServerFromContextMenu(sender);
        if (server != null) ConnectToServer(server);
    }

    private void ServerContextMenu_Disconnect_Click(object sender, RoutedEventArgs e)
    {
        var server = GetServerFromContextMenu(sender);
        if (server != null) CloseServerTab(server.Id);
    }

    private void ServerContextMenu_ToggleFavorite_Click(object sender, RoutedEventArgs e)
    {
        var server = GetServerFromContextMenu(sender);
        if (server != null)
        {
            _serverManager.ToggleFavorite(server.Id);
            RefreshServerList();
        }
    }

    private void ServerContextMenu_Edit_Click(object sender, RoutedEventArgs e)
    {
        var server = GetServerFromContextMenu(sender);
        if (server == null) return;

        var dialog = new AddServerDialog(_serverManager, _profileManager, server) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            RefreshServerList();
        }
    }

    private void ServerContextMenu_Remove_Click(object sender, RoutedEventArgs e)
    {
        var server = GetServerFromContextMenu(sender);
        if (server == null) return;

        var result = MessageBox.Show(
            $"Remove server '{server.DisplayNameWithIntent}'?",
            "Remove Server",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            CloseServerTab(server.Id);
            _collectorService?.ClearHealthForServer(
                RemoteCollectorService.GetDeterministicHashCode(
                    RemoteCollectorService.GetServerNameForStorage(server)));
            _serverManager.DeleteServer(server.Id);
            RefreshServerList();
            StatusText.Text = $"Removed server: {server.DisplayNameWithIntent}";
        }
    }

    private bool _sidebarCollapsed;

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;

        if (_sidebarCollapsed)
        {
            SidebarColumn.Width = new GridLength(40);
            SidebarTitle.Visibility = Visibility.Collapsed;
            SidebarSubtitle.Visibility = Visibility.Collapsed;
            if (sender is System.Windows.Controls.Button btn) btn.Content = "»";
        }
        else
        {
            SidebarColumn.Width = new GridLength(280);
            SidebarTitle.Visibility = Visibility.Visible;
            SidebarSubtitle.Visibility = Visibility.Visible;
            if (sender is System.Windows.Controls.Button btn) btn.Content = "«";
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusText.Text = "Refreshing...";

            // Check all server connections
            await _serverManager.CheckAllConnectionsAsync();

            RefreshServerList();
            UpdateStatusBar();

            StatusText.Text = "Refresh complete";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Refresh failed: {ex.Message}";
        }
    }

    private void CheckConnectionsAndNotify()
    {
        try
        {
            var servers = _serverManager.GetAllServers();
            bool needsRefresh = false;
            foreach (var server in servers)
            {
                var status = _serverManager.GetConnectionStatus(server.Id);
                server.IsOnline = status?.IsOnline;
                if (status?.IsOnline == null) continue;

                bool isOnline = status.IsOnline == true;
                var healthSummary = _collectorService != null && isOnline
                    ? _collectorService.GetHealthSummary(server)
                    : null;
                bool hasErrors = healthSummary?.ErroringCollectors > 0;
                server.HasCollectorErrors = hasErrors;

                if (_previousConnectionStates.TryGetValue(server.Id, out var wasOnline))
                {
                    if (App.AlertsEnabled && App.NotifyConnectionChanges)
                    {
                        if (wasOnline && !isOnline)
                        {
                            _trayService?.ShowNotification(
                                "Server Offline",
                                $"{server.DisplayNameWithIntent} is unreachable: {status.ErrorMessage ?? "unknown error"}",
                                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error);
                        }
                        else if (!wasOnline && isOnline)
                        {
                            _trayService?.ShowNotification(
                                "Server Online",
                                $"{server.DisplayNameWithIntent} is back online",
                                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
                        }
                    }

                    if (wasOnline != isOnline)
                    {
                        needsRefresh = true;
                    }
                }
                else
                {
                    /* First time seeing this server's status — need to refresh */
                    needsRefresh = true;
                }

                if (_previousCollectorErrorStates.TryGetValue(server.Id, out var prevHasErrors) && prevHasErrors != hasErrors)
                    needsRefresh = true;

                /* One-time balloon when blocking/deadlock capture can't start because the
                   XE session couldn't be created (#1086). Edge-triggered on the false→true
                   transition so it doesn't re-fire every poll while the condition persists. */
                bool xeSessionDown = healthSummary?.XeSessionFailures.Count > 0;
                _previousXeSessionFailureStates.TryGetValue(server.Id, out var wasXeSessionDown);

                if (App.AlertsEnabled && xeSessionDown && !wasXeSessionDown)
                {
                    var captures = string.Join(" and ", healthSummary!.XeSessionFailures
                        .Select(f => f.CollectorName == "blocked_process_report" ? "blocking" : "deadlock"));
                    var reason = healthSummary.XeSessionFailures[0].XeSessionMessage ?? "unknown error";

                    _trayService?.ShowNotification(
                        "Capture Not Running",
                        $"{server.DisplayNameWithIntent}: {captures} capture can't start — {reason}",
                        Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning);
                }

                _previousXeSessionFailureStates[server.Id] = xeSessionDown;
                _previousConnectionStates[server.Id] = isOnline;
                _previousCollectorErrorStates[server.Id] = hasErrors;
            }

            if (needsRefresh)
            {
                RefreshServerList();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConnectionAlerts", $"Connection check notify failed: {ex.Message}");
        }
    }

    private async void CheckPerformanceAlerts(ServerSummaryItem summary)
    {
        if (!App.AlertsEnabled || _trayService == null) return;

        var key = summary.ServerId.ToString();
        var now = DateTime.UtcNow;
        var alertCooldown = TimeSpan.FromMinutes(App.AlertCooldownMinutes);

        /* Skip popup/email alerts if user has acknowledged or silenced this server */
        bool suppressPopups = !_alertStateService.ShouldShowAlerts(key);

        /* CPU alerts — uses the metric the user selected (Total non-idle CPU by default, or SQL Server only). */
        var alertCpuValue = summary.CpuPercentForAlert;
        string cpuMetricLabel = App.AlertCpuMode == CpuAlertMode.Total ? "Total CPU" : "SQL CPU";
        bool cpuExceeded = App.AlertCpuEnabled
            && alertCpuValue.HasValue
            && alertCpuValue.Value >= App.AlertCpuThreshold;

        if (cpuExceeded)
        {
            _activeCpuAlert[key] = true;
            if (!suppressPopups && (!_lastCpuAlert.TryGetValue(key, out var lastCpu) || now - lastCpu >= alertCooldown))
            {
                var muteCtx = new AlertMuteContext { ServerName = summary.DisplayName, MetricName = "High CPU" };
                bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
                _lastCpuAlert[key] = now;

                if (!isMuted)
                {
                    _trayService.ShowSnoozableNotification(
                        "High CPU",
                        $"{summary.DisplayName}: {cpuMetricLabel} at {alertCpuValue:F0}% (threshold: {App.AlertCpuThreshold}%)",
                        Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning,
                        summary.DisplayName,
                        "High CPU",
                        _muteRuleService);
                }

                var cpuDetailText = $"  {cpuMetricLabel}: {alertCpuValue:F0}%\n  Threshold: {App.AlertCpuThreshold}%";

                await _emailAlertService.TrySendAlertEmailAsync(
                    "High CPU",
                    summary.DisplayName,
                    $"{alertCpuValue:F0}% ({cpuMetricLabel})",
                    $"{App.AlertCpuThreshold}%",
                    summary.ServerId,
                    muted: isMuted,
                    detailText: cpuDetailText);
            }
        }
        else if (_activeCpuAlert.TryGetValue(key, out var wasCpu) && wasCpu)
        {
            _activeCpuAlert[key] = false;
            /* Only announce "resolved" if the user is still watching this alert and the server
               isn't silenced. Disabling the alert flips cpuExceeded false (it includes the enabled
               flag) and silencing sets suppressPopups — neither means CPU actually recovered. */
            if (!suppressPopups && App.AlertCpuEnabled)
            {
                _trayService.ShowNotification(
                    "CPU Resolved",
                    $"{summary.DisplayName}: {cpuMetricLabel} back to {alertCpuValue:F0}%",
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
            }
        }

        /* Blocking alerts */
        var effectiveBlockingCount = summary.BlockingCount;
        if (App.AlertBlockingEnabled && App.AlertExcludedDatabases.Count > 0
            && summary.BlockingCount >= App.AlertBlockingThreshold && _dataService != null)
        {
            try
            {
                var blockingRows = await Task.Run(() => _dataService.GetRecentBlockedProcessReportsAsync(summary.ServerId, hoursBack: 1));
                effectiveBlockingCount = blockingRows
                    .Count(r => string.IsNullOrEmpty(r.DatabaseName) ||
                        !App.AlertExcludedDatabases.Any(e =>
                            string.Equals(e, r.DatabaseName, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex)
            {
                AppLogger.Error("Alerts", $"Failed to filter blocking count for {summary.DisplayName}: {ex.Message}");
            }
        }

        /* Edge-trigger the rolling 1-hour blocking count so the same blocked-process reports
           don't re-alert every cooldown for the whole hour they linger in the window (#1091).
           See RollingCountAlertGate for the watermark semantics. */
        int blockingWatermark = _lastAlertedBlockingCount.TryGetValue(key, out var labc) ? labc : 0;
        bool blockingCooldownElapsed = !_lastBlockingAlert.TryGetValue(key, out var lastBlocking) || now - lastBlocking >= alertCooldown;
        var blockingDecision = App.AlertBlockingEnabled
            ? RollingCountAlertGate.Evaluate(effectiveBlockingCount, App.AlertBlockingThreshold, blockingWatermark, blockingCooldownElapsed, suppressPopups)
            : new RollingCountAlertGate.Decision(false, false, 0);
        _lastAlertedBlockingCount[key] = blockingDecision.Watermark;

        bool wasBlockingActive = _activeBlockingAlert.TryGetValue(key, out var wasBlocking) && wasBlocking;
        _activeBlockingAlert[key] = blockingDecision.Active;

        if (blockingDecision.Fire)
        {
            var muteCtx = new AlertMuteContext { ServerName = summary.DisplayName, MetricName = "Blocking Detected" };
            bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
            _lastBlockingAlert[key] = now;

            if (!isMuted)
            {
                _trayService.ShowSnoozableNotification(
                    "Blocking Detected",
                    $"{summary.DisplayName}: {effectiveBlockingCount} blocking session(s)",
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning,
                    summary.DisplayName,
                    "Blocking Detected",
                    _muteRuleService);
            }

            var blockingContext = await BuildBlockingContextAsync(summary.ServerId);
            var detailText = ContextToDetailText(blockingContext);

            await _emailAlertService.TrySendAlertEmailAsync(
                "Blocking Detected",
                summary.DisplayName,
                effectiveBlockingCount.ToString(),
                App.AlertBlockingThreshold.ToString(),
                summary.ServerId,
                blockingContext,
                muted: isMuted,
                detailText: detailText);
        }
        else if (!blockingDecision.Active && wasBlockingActive)
        {
            if (!suppressPopups && App.AlertBlockingEnabled)
            {
                _trayService.ShowNotification(
                    "Blocking Cleared",
                    $"{summary.DisplayName}: No active blocking",
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
            }
        }

        /* Deadlock alerts */
        var effectiveDeadlockCount = summary.DeadlockCount;
        if (App.AlertDeadlockEnabled && App.AlertExcludedDatabases.Count > 0
            && summary.DeadlockCount >= App.AlertDeadlockThreshold && _dataService != null)
        {
            try
            {
                var deadlockRows = await Task.Run(() => _dataService.GetRecentDeadlocksAsync(summary.ServerId, hoursBack: 1));
                effectiveDeadlockCount = deadlockRows
                    .Count(r => !IsDeadlockExcluded(r, App.AlertExcludedDatabases));
            }
            catch (Exception ex)
            {
                AppLogger.Error("Alerts", $"Failed to filter deadlock count for {summary.DisplayName}: {ex.Message}");
            }
        }

        /* Edge-trigger the rolling 1-hour deadlock count so the same deadlocks don't re-alert
           every cooldown for the whole hour they linger in the window (#1091). See
           RollingCountAlertGate for the watermark semantics. */
        int deadlockWatermark = _lastAlertedDeadlockCount.TryGetValue(key, out var ladc) ? ladc : 0;
        bool deadlockCooldownElapsed = !_lastDeadlockAlert.TryGetValue(key, out var lastDeadlock) || now - lastDeadlock >= alertCooldown;
        var deadlockDecision = App.AlertDeadlockEnabled
            ? RollingCountAlertGate.Evaluate(effectiveDeadlockCount, App.AlertDeadlockThreshold, deadlockWatermark, deadlockCooldownElapsed, suppressPopups)
            : new RollingCountAlertGate.Decision(false, false, 0);
        _lastAlertedDeadlockCount[key] = deadlockDecision.Watermark;

        bool wasDeadlockActive = _activeDeadlockAlert.TryGetValue(key, out var wasDeadlock) && wasDeadlock;
        _activeDeadlockAlert[key] = deadlockDecision.Active;

        if (deadlockDecision.Fire)
        {
            var muteCtx = new AlertMuteContext { ServerName = summary.DisplayName, MetricName = "Deadlocks Detected" };
            bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
            _lastDeadlockAlert[key] = now;

            if (!isMuted)
            {
                _trayService.ShowSnoozableNotification(
                    "Deadlocks Detected",
                    $"{summary.DisplayName}: {effectiveDeadlockCount} deadlock(s) in the last hour",
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error,
                    summary.DisplayName,
                    "Deadlocks Detected",
                    _muteRuleService);
            }

            var deadlockContext = await BuildDeadlockContextAsync(summary.ServerId);
            var detailText = ContextToDetailText(deadlockContext);

            await _emailAlertService.TrySendAlertEmailAsync(
                "Deadlocks Detected",
                summary.DisplayName,
                effectiveDeadlockCount.ToString(),
                App.AlertDeadlockThreshold.ToString(),
                summary.ServerId,
                deadlockContext,
                muted: isMuted,
                detailText: detailText);
        }
        else if (!deadlockDecision.Active && wasDeadlockActive)
        {
            if (!suppressPopups && App.AlertDeadlockEnabled)
            {
                _trayService.ShowNotification(
                    "Deadlocks Cleared",
                    $"{summary.DisplayName}: No deadlocks in the last hour",
                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
            }
        }

        /* Poison wait alerts */
        if (App.AlertPoisonWaitEnabled && _dataService != null)
        {
            try
            {
                var poisonWaits = await Task.Run(() => _dataService.GetLatestPoisonWaitAvgsAsync(summary.ServerId));
                var triggered = poisonWaits.FindAll(w => w.AvgMsPerWait >= App.AlertPoisonWaitThresholdMs);

                if (triggered.Count > 0)
                {
                    _activePoisonWaitAlert[key] = true;
                    if (!suppressPopups && (!_lastPoisonWaitAlert.TryGetValue(key, out var lastPoisonWait) || now - lastPoisonWait >= alertCooldown))
                    {
                        var worst = triggered[0];
                        var allWaitNames = string.Join(", ", triggered.ConvertAll(w => $"{w.WaitType} ({w.AvgMsPerWait:F0}ms)"));

                        /* Poison wait mute check uses the worst (highest avg ms/wait) triggered wait type.
                           Limitation: if a user mutes a specific wait type that isn't the worst, the alert
                           still fires. Conversely, muting the worst type suppresses the entire alert even
                           if other unmuted poison waits are present. */
                        var muteCtx = new AlertMuteContext { ServerName = summary.DisplayName, MetricName = "Poison Wait", WaitType = worst.WaitType };
                        bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
                        _lastPoisonWaitAlert[key] = now;

                        if (!isMuted)
                        {
                            _trayService.ShowSnoozableNotification(
                                "Poison Wait",
                                $"{summary.DisplayName}: {worst.WaitType} avg {worst.AvgMsPerWait:F0}ms/wait",
                                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Error,
                                summary.DisplayName,
                                "Poison Wait",
                                _muteRuleService);
                        }

                        var poisonContext = BuildPoisonWaitContext(triggered);
                        var detailText = ContextToDetailText(poisonContext);

                        await _emailAlertService.TrySendAlertEmailAsync(
                            "Poison Wait",
                            summary.DisplayName,
                            allWaitNames,
                            $"{App.AlertPoisonWaitThresholdMs}ms avg",
                            summary.ServerId,
                            poisonContext,
                            numericCurrentValue: worst.AvgMsPerWait,
                            numericThresholdValue: App.AlertPoisonWaitThresholdMs,
                            muted: isMuted,
                            detailText: detailText);
                    }
                }
                else if (_activePoisonWaitAlert.TryGetValue(key, out var wasPoisonWait) && wasPoisonWait)
                {
                    _activePoisonWaitAlert[key] = false;
                    if (!suppressPopups)
                    {
                        _trayService.ShowNotification(
                            "Poison Waits Cleared",
                            $"{summary.DisplayName}: Poison wait avg below threshold",
                            Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Alerts", $"Failed to check poison waits for {summary.DisplayName}: {ex.Message}");
            }
        }

        /* Long-running query alerts */
        if (App.AlertLongRunningQueryEnabled && _dataService != null)
        {
            try
            {
                var longRunning = await Task.Run(() => _dataService.GetLongRunningQueriesAsync(summary.ServerId, App.AlertLongRunningQueryThresholdMinutes, App.AlertLongRunningQueryMaxResults, App.AlertLongRunningQueryExcludeSpServerDiagnostics, App.AlertLongRunningQueryExcludeWaitFor, App.AlertLongRunningQueryExcludeBackups, App.AlertLongRunningQueryExcludeMiscWaits, App.AlertLongRunningQueryExcludeCdc));

                if (App.AlertExcludedDatabases.Count > 0)
                {
                    longRunning = longRunning
                        .Where(q => string.IsNullOrEmpty(q.DatabaseName) ||
                            !App.AlertExcludedDatabases.Any(e =>
                                string.Equals(e, q.DatabaseName, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                }

                if (longRunning.Count > 0)
                {
                    _activeLongRunningQueryAlert[key] = true;
                    if (!suppressPopups && (!_lastLongRunningQueryAlert.TryGetValue(key, out var lastLrq) || now - lastLrq >= alertCooldown))
                    {
                        var worst = longRunning[0];
                        var elapsedMinutes = worst.ElapsedSeconds / 60;
                        var preview = TruncateText(worst.QueryText, 80);
                        var previewSuffix = string.IsNullOrEmpty(preview) ? "" : $" — {preview}";

                        var muteCtx = new AlertMuteContext
                        {
                            ServerName = summary.DisplayName,
                            MetricName = "Long-Running Query",
                            DatabaseName = worst.DatabaseName,
                            QueryText = worst.QueryText
                        };
                        bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
                        _lastLongRunningQueryAlert[key] = now;

                        if (!isMuted)
                        {
                            _trayService.ShowSnoozableNotification(
                                "Long-Running Query",
                                $"{summary.DisplayName}: Session #{worst.SessionId} running {elapsedMinutes}m{previewSuffix}",
                                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning,
                                summary.DisplayName,
                                "Long-Running Query",
                                _muteRuleService);
                        }

                        var lrqContext = BuildLongRunningQueryContext(longRunning);
                        var detailText = ContextToDetailText(lrqContext);

                        await _emailAlertService.TrySendAlertEmailAsync(
                            "Long-Running Query",
                            summary.DisplayName,
                            $"{longRunning.Count} query(s), longest {elapsedMinutes}m",
                            $"{App.AlertLongRunningQueryThresholdMinutes}m",
                            summary.ServerId,
                            lrqContext,
                            numericCurrentValue: elapsedMinutes,
                            numericThresholdValue: App.AlertLongRunningQueryThresholdMinutes,
                            muted: isMuted,
                            detailText: detailText);
                    }
                }
                else if (_activeLongRunningQueryAlert.TryGetValue(key, out var wasLongRunning) && wasLongRunning)
                {
                    _activeLongRunningQueryAlert[key] = false;
                    if (!suppressPopups)
                    {
                        _trayService.ShowNotification(
                            "Long-Running Queries Cleared",
                            $"{summary.DisplayName}: No queries over threshold",
                            Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Alerts", $"Failed to check long-running queries for {summary.DisplayName}: {ex.Message}");
            }
        }

        /* TempDB space alerts */
        if (App.AlertTempDbSpaceEnabled && _dataService != null)
        {
            try
            {
                var tempDb = await Task.Run(() => _dataService.GetLatestTempDbSpaceAsync(summary.ServerId));

                if (tempDb != null && tempDb.UsedPercent >= App.AlertTempDbSpaceThresholdPercent)
                {
                    _activeTempDbSpaceAlert[key] = true;
                    if (!suppressPopups && (!_lastTempDbSpaceAlert.TryGetValue(key, out var lastTempDb) || now - lastTempDb >= alertCooldown))
                    {
                        var muteCtx = new AlertMuteContext { ServerName = summary.DisplayName, MetricName = "TempDB Space" };
                        bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
                        _lastTempDbSpaceAlert[key] = now;

                        if (!isMuted)
                        {
                            _trayService.ShowSnoozableNotification(
                                "TempDB Space",
                                $"{summary.DisplayName}: TempDB {tempDb.UsedPercent:F0}% used",
                                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning,
                                summary.DisplayName,
                                "TempDB Space",
                                _muteRuleService);
                        }

                        var tempDbContext = BuildTempDbSpaceContext(tempDb);
                        var detailText = ContextToDetailText(tempDbContext);

                        await _emailAlertService.TrySendAlertEmailAsync(
                            "TempDB Space",
                            summary.DisplayName,
                            $"{tempDb.UsedPercent:F0}% used ({tempDb.TotalReservedMb:F0} MB)",
                            $"{App.AlertTempDbSpaceThresholdPercent}%",
                            summary.ServerId,
                            tempDbContext,
                            numericCurrentValue: tempDb.UsedPercent,
                            numericThresholdValue: App.AlertTempDbSpaceThresholdPercent,
                            muted: isMuted,
                            detailText: detailText);
                    }
                }
                else if (_activeTempDbSpaceAlert.TryGetValue(key, out var wasTempDb) && wasTempDb)
                {
                    _activeTempDbSpaceAlert[key] = false;
                    if (!suppressPopups)
                    {
                        var pct = tempDb != null ? $"{tempDb.UsedPercent:F0}%" : "N/A";
                        _trayService.ShowNotification(
                            "TempDB Space Resolved",
                            $"{summary.DisplayName}: TempDB usage back to {pct}",
                            Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Alerts", $"Failed to check TempDB space for {summary.DisplayName}: {ex.Message}");
            }
        }

        /* Low volume free space alerts — not applicable to Azure SQL DB (no volume stats collected) */
        if (App.AlertLowDiskEnabled && _dataService != null)
        {
            try
            {
                var volumes = await Task.Run(() => _dataService.GetVolumeFreeSpaceAsync(summary.ServerId));
                var breached = GetBreachedVolumes(volumes);

                if (breached.Count > 0)
                {
                    var worst = breached[0];
                    _activeLowDiskAlert[key] = true;
                    double? lastLowDiskPercent =
                        _lastAlertedLowDiskPercent.TryGetValue(key, out var lowDiskPct) ? lowDiskPct : (double?)null;
                    /* #754 follow-up: notify only on a fresh or worsening breach, not every cooldown for a
                       standing full volume (which also re-recorded a history row and made Dismiss feel broken). */
                    if (!suppressPopups
                        && LowDiskAlertGate.ShouldAlert(worst.FreePercent, lastLowDiskPercent)
                        && (!_lastLowDiskAlert.TryGetValue(key, out var lastLowDisk) || now - lastLowDisk >= alertCooldown))
                    {
                        var muteCtx = new AlertMuteContext { ServerName = summary.DisplayName, MetricName = "Volume Free Space" };
                        bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
                        _lastLowDiskAlert[key] = now;
                        _lastAlertedLowDiskPercent[key] = worst.FreePercent;

                        if (!isMuted)
                        {
                            _trayService.ShowSnoozableNotification(
                                "Volume Free Space",
                                $"{summary.DisplayName}: {worst.MountPoint} {worst.FreePercent:F0}% free ({worst.FreeGb:F1} GB)",
                                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning,
                                summary.DisplayName,
                                "Volume Free Space",
                                _muteRuleService);
                        }

                        var lowDiskContext = BuildVolumeFreeSpaceContext(breached);
                        var detailText = ContextToDetailText(lowDiskContext);

                        await _emailAlertService.TrySendAlertEmailAsync(
                            "Volume Free Space",
                            summary.DisplayName,
                            $"{worst.MountPoint} {worst.FreePercent:F0}% free ({worst.FreeGb:F1} GB)",
                            FormatLowDiskThreshold(),
                            summary.ServerId,
                            lowDiskContext,
                            numericCurrentValue: worst.FreePercent,
                            numericThresholdValue: App.AlertLowDiskThresholdPercent,
                            muted: isMuted,
                            detailText: detailText);
                    }
                }
                else if (_activeLowDiskAlert.TryGetValue(key, out var wasLowDisk) && wasLowDisk)
                {
                    _activeLowDiskAlert[key] = false;
                    _lastAlertedLowDiskPercent.Remove(key);
                    if (!suppressPopups)
                    {
                        _trayService.ShowNotification(
                            "Volume Free Space Resolved",
                            $"{summary.DisplayName}: All volumes back above threshold",
                            Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Alerts", $"Failed to check volume free space for {summary.DisplayName}: {ex.Message}");
            }
        }

        /* Anomalous Agent job alerts */
        if (App.AlertLongRunningJobEnabled && _dataService != null)
        {
            try
            {
                var anomalousJobs = await Task.Run(() => _dataService.GetAnomalousJobsAsync(summary.ServerId, App.AlertLongRunningJobMultiplier));

                /* _lastLongRunningJobAlert is keyed per job *run* ({server}:{jobId}:{startTime}),
                   so unlike the per-server cooldown dicts it grows without bound. Drop entries
                   that have aged past the cooldown each pass. */
                foreach (var staleJobKey in _lastLongRunningJobAlert
                             .Where(kv => now - kv.Value >= alertCooldown)
                             .Select(kv => kv.Key)
                             .ToList())
                {
                    _lastLongRunningJobAlert.Remove(staleJobKey);
                }

                if (anomalousJobs.Count > 0)
                {
                    _activeLongRunningJobAlert[key] = true;
                    var worst = anomalousJobs[0];
                    var jobKey = $"{key}:{worst.JobId}:{worst.StartTime:O}";

                    if (!suppressPopups && (!_lastLongRunningJobAlert.TryGetValue(jobKey, out var lastJob) || now - lastJob >= alertCooldown))
                    {
                        var currentMinutes = worst.CurrentDurationSeconds / 60;

                        var muteCtx = new AlertMuteContext { ServerName = summary.DisplayName, MetricName = "Long-Running Job", JobName = worst.JobName };
                        bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
                        _lastLongRunningJobAlert[jobKey] = now;

                        if (!isMuted)
                        {
                            _trayService.ShowSnoozableNotification(
                                "Long-Running Job",
                                $"{summary.DisplayName}: {worst.JobName} at {worst.PercentOfAverage:F0}% of avg ({currentMinutes}m)",
                                Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning,
                                summary.DisplayName,
                                "Long-Running Job",
                                _muteRuleService);
                        }

                        var jobContext = BuildAnomalousJobContext(anomalousJobs);
                        var detailText = ContextToDetailText(jobContext);

                        await _emailAlertService.TrySendAlertEmailAsync(
                            "Long-Running Job",
                            summary.DisplayName,
                            $"{anomalousJobs.Count} job(s) exceeding {App.AlertLongRunningJobMultiplier}x average",
                            $"{App.AlertLongRunningJobMultiplier}x historical avg",
                            summary.ServerId,
                            jobContext,
                            numericCurrentValue: (double)(worst.PercentOfAverage ?? 0),
                            numericThresholdValue: App.AlertLongRunningJobMultiplier * 100,
                            muted: isMuted,
                            detailText: detailText);
                    }
                }
                else if (_activeLongRunningJobAlert.TryGetValue(key, out var wasJob) && wasJob)
                {
                    _activeLongRunningJobAlert[key] = false;
                    if (!suppressPopups)
                    {
                        _trayService.ShowNotification(
                            "Long-Running Jobs Cleared",
                            $"{summary.DisplayName}: No jobs exceeding threshold",
                            Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Info);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Alerts", $"Failed to check anomalous jobs for {summary.DisplayName}: {ex.Message}");
            }
        }

        /* Failed Agent job alerts — live msdb query for runs that failed in the lookback window.
           Failure outcomes aren't part of the collected running_jobs snapshot, so this queries the
           monitored server directly (mirrors the long-running-query live pattern). Failures are
           point-in-time events, so there is no "cleared" notification; the per-server watermark
           dedups so the same failure never re-fires. */
        if (App.AlertFailedJobEnabled && _collectorService != null)
        {
            try
            {
                var server = _serverManager.GetAllServers().FirstOrDefault(s =>
                    RemoteCollectorService.GetDeterministicHashCode(RemoteCollectorService.GetServerNameForStorage(s)).ToString() == key);
                var connStatus = server != null ? _serverManager.GetConnectionStatus(server.Id) : null;

                /* Only query online, non-Azure-SQL-DB servers whose login has msdb access.
                   Azure SQL DB has no SQL Agent; a login without msdb can't read sysjobhistory. */
                if (server != null
                    && connStatus != null
                    && connStatus.IsOnline == true
                    && connStatus.SqlEngineEdition != 5
                    && connStatus.HasMsdbAccess)
                {
                    /* Live msdb read via the collector's connection path (async SqlClient, already
                       off the UI thread; MFA serialization / throttle / retry handled inside). */
                    var failedJobs = await _collectorService.GetRecentlyFailedJobsAsync(server, App.AlertFailedJobLookbackMinutes);

                    if (failedJobs.Count > 0)
                    {
                        var newestFailure = failedJobs.Max(j => j.RunDateTime);
                        bool hasWatermark = _lastAlertedFailedJobTime.TryGetValue(key, out var lastFailure);
                        bool hasNewFailure = !hasWatermark || newestFailure > lastFailure;

                        if (hasNewFailure && !suppressPopups &&
                            (!_lastFailedJobAlert.TryGetValue(key, out var lastFailedAlert) || now - lastFailedAlert >= alertCooldown))
                        {
                            var mostRecent = failedJobs[0]; // ORDER BY run_datetime DESC
                            var jobNames = string.Join(", ", failedJobs.Select(j => j.JobName).Distinct().Take(3));

                            var muteCtx = new AlertMuteContext { ServerName = summary.DisplayName, MetricName = "Failed Agent Job", JobName = mostRecent.JobName };
                            bool isMuted = _muteRuleService.IsAlertMuted(muteCtx);
                            _lastFailedJobAlert[key] = now;
                            _lastAlertedFailedJobTime[key] = newestFailure;

                            if (!isMuted)
                            {
                                _trayService.ShowSnoozableNotification(
                                    "Failed Agent Job",
                                    $"{summary.DisplayName}: {failedJobs.Count} job failure(s) — {jobNames}",
                                    Hardcodet.Wpf.TaskbarNotification.BalloonIcon.Warning,
                                    summary.DisplayName,
                                    "Failed Agent Job",
                                    _muteRuleService);
                            }

                            var failedJobContext = BuildFailedJobContext(failedJobs);
                            var detailText = ContextToDetailText(failedJobContext);

                            await _emailAlertService.TrySendAlertEmailAsync(
                                "Failed Agent Job",
                                summary.DisplayName,
                                $"{failedJobs.Count} job failure(s) in last {App.AlertFailedJobLookbackMinutes}m — {jobNames}",
                                $"last {App.AlertFailedJobLookbackMinutes}m",
                                summary.ServerId,
                                failedJobContext,
                                numericCurrentValue: failedJobs.Count,
                                numericThresholdValue: 0,
                                muted: isMuted,
                                detailText: detailText);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Alerts", $"Failed to check failed jobs for {summary.DisplayName}: {ex.Message}");
            }
        }
    }

        private static string TruncateText(string text, int maxLength = 300)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";
        }

        private static string? ContextToDetailText(AlertContext? context)
        {
            if (context == null || context.Details.Count == 0) return null;
            var sb = new System.Text.StringBuilder();
            foreach (var detail in context.Details)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine(detail.Heading);
                foreach (var (label, value) in detail.Fields)
                    sb.AppendLine($"  {label}: {value}");
            }
            return sb.ToString().TrimEnd();
        }

        private async Task<AlertContext?> BuildBlockingContextAsync(int serverId)
        {
            try
            {
                if (_dataService == null) return null;

                var events = await Task.Run(() => _dataService.GetRecentBlockedProcessReportsAsync(serverId, hoursBack: 1));
                if (events == null || events.Count == 0) return null;

                if (App.AlertExcludedDatabases.Count > 0)
                {
                    events = events
                        .Where(e => string.IsNullOrEmpty(e.DatabaseName) ||
                            !App.AlertExcludedDatabases.Any(ex =>
                                string.Equals(ex, e.DatabaseName, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    if (events.Count == 0) return null;
                }

                var context = new AlertContext();
                var firstXml = (string?)null;

                foreach (var e in events.Take(3))
                {
                    var item = new AlertDetailItem
                    {
                        Heading = $"Blocked #{e.BlockedSpid} by #{e.BlockingSpid}",
                        Fields = new()
                    };

                    if (!string.IsNullOrEmpty(e.DatabaseName))
                        item.Fields.Add(("Database", e.DatabaseName));
                    if (!string.IsNullOrEmpty(e.BlockedSqlText))
                        item.Fields.Add(("Blocked Query", TruncateText(e.BlockedSqlText)));
                    if (!string.IsNullOrEmpty(e.BlockingSqlText))
                        item.Fields.Add(("Blocking Query", TruncateText(e.BlockingSqlText)));
                    item.Fields.Add(("Wait Time", e.WaitTimeFormatted));
                    if (!string.IsNullOrEmpty(e.LockMode))
                        item.Fields.Add(("Lock Mode", e.LockMode));

                    context.Details.Add(item);
                    if (firstXml == null && e.HasReportXml)
                        firstXml = e.BlockedProcessReportXml;
                }

                if (!string.IsNullOrEmpty(firstXml))
                {
                    context.AttachmentXml = firstXml;
                    context.AttachmentFileName = "blocked_process_report.xml";
                }

                return context;
            }
            catch (Exception ex)
            {
                AppLogger.Error("EmailAlert", $"Failed to fetch blocking detail for email: {ex.Message}");
                return null;
            }
        }

        private async Task<AlertContext?> BuildDeadlockContextAsync(int serverId)
        {
            try
            {
                if (_dataService == null) return null;

                var deadlocks = await Task.Run(() => _dataService.GetRecentDeadlocksAsync(serverId, hoursBack: 1));
                if (deadlocks == null || deadlocks.Count == 0) return null;

                if (App.AlertExcludedDatabases.Count > 0)
                {
                    deadlocks = deadlocks
                        .Where(d => !IsDeadlockExcluded(d, App.AlertExcludedDatabases))
                        .ToList();
                    if (deadlocks.Count == 0) return null;
                }

                var context = new AlertContext();
                var firstGraph = (string?)null;

                foreach (var d in deadlocks.Take(3))
                {
                    var item = new AlertDetailItem
                    {
                        Heading = "Deadlock Victim",
                        Fields = new()
                    };

                    if (!string.IsNullOrEmpty(d.VictimSqlText))
                        item.Fields.Add(("Victim SQL", TruncateText(d.VictimSqlText)));
                    if (!string.IsNullOrEmpty(d.ProcessSummary))
                        item.Fields.Add(("Processes", d.ProcessSummary));

                    context.Details.Add(item);
                    if (firstGraph == null && d.HasDeadlockXml)
                        firstGraph = d.DeadlockGraphXml;
                }

                if (!string.IsNullOrEmpty(firstGraph))
                {
                    context.AttachmentXml = firstGraph;
                    context.AttachmentFileName = "deadlock_graph.xml";
                }

                return context;
            }
            catch (Exception ex)
            {
                AppLogger.Error("EmailAlert", $"Failed to fetch deadlock detail for email: {ex.Message}");
                return null;
            }
        }

        private static bool IsDeadlockExcluded(DeadlockRow row, List<string> excludedDatabases)
        {
            if (string.IsNullOrEmpty(row.DeadlockGraphXml)) return false;
            try
            {
                var doc = XElement.Parse(row.DeadlockGraphXml);
                var dbNames = doc.Descendants("process")
                    .Select(p => p.Attribute("currentdbname")?.Value)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Cast<string>()
                    .ToList();
                if (dbNames.Count == 0) return false;
                return dbNames.All(db => excludedDatabases.Any(e =>
                    string.Equals(e, db, StringComparison.OrdinalIgnoreCase)));
            }
            catch { return false; }
        }

        private static AlertContext? BuildPoisonWaitContext(List<PoisonWaitDelta> triggeredWaits)
        {
            if (triggeredWaits.Count == 0) return null;

            var context = new AlertContext();
            foreach (var w in triggeredWaits)
            {
                context.Details.Add(new AlertDetailItem
                {
                    Heading = w.WaitType,
                    Fields = new()
                    {
                        ("Avg ms/wait", $"{w.AvgMsPerWait:F1}"),
                        ("Delta wait ms", $"{w.DeltaMs:N0}"),
                        ("Delta tasks", $"{w.DeltaTasks:N0}")
                    }
                });
            }
            return context;
        }

        private static AlertContext? BuildLongRunningQueryContext(List<LongRunningQueryInfo> queries)
        {
            if (queries.Count == 0) return null;

            var context = new AlertContext();
            foreach (var q in queries.GetRange(0, Math.Min(3, queries.Count)))
            {
                var item = new AlertDetailItem
                {
                    Heading = $"Session #{q.SessionId} — {q.ElapsedSeconds / 60}m {q.ElapsedSeconds % 60}s",
                    Fields = new()
                };

                if (!string.IsNullOrEmpty(q.DatabaseName))
                    item.Fields.Add(("Database", q.DatabaseName));
                if (!string.IsNullOrEmpty(q.QueryText))
                    item.Fields.Add(("Query", TruncateText(q.QueryText)));
                item.Fields.Add(("CPU Time", $"{q.CpuTimeMs:N0} ms"));
                item.Fields.Add(("Reads", $"{q.Reads:N0}"));
                item.Fields.Add(("Writes", $"{q.Writes:N0}"));
                if (!string.IsNullOrEmpty(q.WaitType))
                    item.Fields.Add(("Wait Type", q.WaitType));
                if (q.BlockingSessionId.HasValue && q.BlockingSessionId.Value > 0)
                    item.Fields.Add(("Blocked By", $"Session #{q.BlockingSessionId.Value}"));

                context.Details.Add(item);
            }
            return context;
        }

        /* Returns the volumes whose free space is under the configured % or GB threshold (a 0 threshold
           disables that dimension), worst (lowest free %) first, so the alert names the tightest volume. */
        private static List<VolumeFreeSpaceInfo> GetBreachedVolumes(List<VolumeFreeSpaceInfo> volumes)
        {
            int pct = App.AlertLowDiskThresholdPercent;
            int gb = App.AlertLowDiskThresholdGb;
            return volumes
                .Where(v => (pct > 0 && v.FreePercent < pct) || (gb > 0 && v.FreeGb < gb))
                .OrderBy(v => v.FreePercent)
                .ToList();
        }

        private static string FormatLowDiskThreshold()
        {
            var parts = new List<string>();
            if (App.AlertLowDiskThresholdPercent > 0) parts.Add($"{App.AlertLowDiskThresholdPercent}%");
            if (App.AlertLowDiskThresholdGb > 0) parts.Add($"{App.AlertLowDiskThresholdGb} GB");
            return parts.Count > 0 ? string.Join(" / ", parts) : "—";
        }

        private static AlertContext? BuildVolumeFreeSpaceContext(List<VolumeFreeSpaceInfo> volumes)
        {
            if (volumes.Count == 0) return null;

            var context = new AlertContext();
            foreach (var v in volumes.GetRange(0, Math.Min(5, volumes.Count)))
            {
                context.Details.Add(new AlertDetailItem
                {
                    Heading = $"{v.MountPoint} — {v.FreePercent:F0}% Free",
                    Fields = new()
                    {
                        ("Free Space", $"{v.FreeGb:F1} GB"),
                        ("Total Size", $"{v.TotalMb / 1024.0:F1} GB"),
                        ("Used", $"{(v.TotalMb - v.FreeMb) / 1024.0:F1} GB")
                    }
                });
            }
            return context;
        }

        private static AlertContext? BuildTempDbSpaceContext(TempDbSpaceInfo tempDb)
        {
            var context = new AlertContext();
            context.Details.Add(new AlertDetailItem
            {
                Heading = $"TempDB — {tempDb.UsedPercent:F0}% Used",
                Fields = new()
                {
                    ("Total Reserved", $"{tempDb.TotalReservedMb:F0} MB"),
                    ("Unallocated", $"{tempDb.UnallocatedMb:F0} MB"),
                    ("User Objects", $"{tempDb.UserObjectReservedMb:F0} MB"),
                    ("Internal Objects", $"{tempDb.InternalObjectReservedMb:F0} MB"),
                    ("Version Store", $"{tempDb.VersionStoreReservedMb:F0} MB"),
                    ("Top Consumer", tempDb.TopConsumerSessionId > 0
                        ? $"Session #{tempDb.TopConsumerSessionId} ({tempDb.TopConsumerMb:F0} MB)"
                        : "None")
                }
            });
            return context;
        }

        private static AlertContext? BuildAnomalousJobContext(List<AnomalousJobInfo> jobs)
        {
            if (jobs.Count == 0) return null;

            var context = new AlertContext();
            foreach (var j in jobs.GetRange(0, Math.Min(3, jobs.Count)))
            {
                context.Details.Add(new AlertDetailItem
                {
                    Heading = j.JobName,
                    Fields = new()
                    {
                        ("Current Duration", FormatDuration(j.CurrentDurationSeconds)),
                        ("Avg Duration", FormatDuration(j.AvgDurationSeconds)),
                        ("P95 Duration", FormatDuration(j.P95DurationSeconds)),
                        ("% of Average", j.PercentOfAverage.HasValue ? $"{j.PercentOfAverage:F0}%" : "N/A"),
                        ("Started", j.StartTime.ToString("yyyy-MM-dd HH:mm:ss"))
                    }
                });
            }
            return context;
        }

        private static AlertContext? BuildFailedJobContext(List<FailedJobInfo> jobs)
        {
            if (jobs.Count == 0) return null;

            var context = new AlertContext();
            foreach (var j in jobs.GetRange(0, Math.Min(5, jobs.Count)))
            {
                var item = new AlertDetailItem { Heading = j.JobName, Fields = new() };
                item.Fields.Add(("Job", j.JobName));
                item.Fields.Add(("Failed At", j.RunDateTimeFormatted));
                if (!string.IsNullOrEmpty(j.Message))
                    item.Fields.Add(("Message", TruncateText(j.Message, 300)));
                context.Details.Add(item);
            }
            return context;
        }

        private static string FormatDuration(long seconds)
        {
            if (seconds < 60) return $"{seconds}s";
            if (seconds < 3600) return $"{seconds / 60}m {seconds % 60}s";
            return $"{seconds / 3600}h {(seconds % 3600) / 60}m";
        }

    }
