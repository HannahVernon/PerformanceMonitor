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
using PerformanceMonitor.Analysis;
using PerformanceMonitorDashboard.Analysis;
using PerformanceMonitorDashboard.Helpers;
using PerformanceMonitorDashboard.Interfaces;
using PerformanceMonitorDashboard.Models;
using PerformanceMonitorDashboard.Services;
using PerformanceMonitorDashboard.Services.Recommendations;

namespace PerformanceMonitorDashboard.Controls
{
    /// <summary>
    /// The unified, read-only Recommendations surface (recommendations rebuild WS1b-1). Renders
    /// the de-duped <see cref="RecommendationItem"/> list from
    /// <see cref="RecommendationsReader.GetRecommendationsAsync"/> as a card list grouped into
    /// collapsible Critical / Warning / Info sections.
    ///
    /// <para>
    /// Per-card affordances split on whether the finding is an <b>incident</b> (time-bound) or a
    /// standing <b>config-fix</b>. Incidents get "Open in Active Queries" (raises
    /// <see cref="OpenActiveQueriesRequested"/> so the host tab deep-links to the time window) and
    /// "Ask AI" (copies an MCP investigation prompt). Config-fixes get "Copy fix" (copies the
    /// ALTER). Both kinds get Apply when a built remediation exists — rendered DISABLED with an
    /// "Available in the next update" tooltip; the Apply action + informed-consent gate are WS1b-2.
    /// </para>
    /// </summary>
    public partial class RecommendationsContent : UserControl
    {
        /// <summary>
        /// Raised when the user clicks "Open in Active Queries" on an incident card. Carries the
        /// finding's RAW UTC time window; the host (<c>ServerTab</c>) applies the ±1h grace, the
        /// UTC→server-local conversion, selects the Performance/Queries tab + Active Queries
        /// sub-tab, and scopes it to the window. Mirrors
        /// <c>CriticalIssuesContent.InvestigateRequested</c>.
        /// </summary>
        public event Action<DateTime, DateTime>? OpenActiveQueriesRequested;

        private DatabaseService? _databaseService;
        private SqlServerFindingStore? _findingStore;
        private RecommendationsReader? _reader;
        private ServerConnection? _serverConnection;
        private ICredentialService? _credentialService;

        private int _hoursBack = 24;
        private int _utcOffsetMinutes;
        private bool _isBusy;

        public RecommendationsContent()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Initializes the control with the dependencies it needs to read recommendations and to
        /// run an on-demand analysis. <paramref name="serverConnection"/> +
        /// <paramref name="credentialService"/> are used only by "Generate now" to build a
        /// connection string and derive the deterministic server id, exactly as the
        /// <see cref="AnalysisScheduler"/> does. <paramref name="utcOffsetMinutes"/> is the
        /// monitored server's UTC offset, used to normalize the legacy producer's server-local
        /// timestamps to UTC (reader) and to render the Ask-AI prompt window in server-local time.
        /// </summary>
        public void Initialize(
            DatabaseService databaseService,
            ServerConnection serverConnection,
            ICredentialService credentialService,
            int utcOffsetMinutes = 0)
        {
            _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
            _serverConnection = serverConnection ?? throw new ArgumentNullException(nameof(serverConnection));
            _credentialService = credentialService ?? throw new ArgumentNullException(nameof(credentialService));
            _utcOffsetMinutes = utcOffsetMinutes;
            _findingStore = new SqlServerFindingStore(databaseService.ConnectionString);
            _reader = new RecommendationsReader(databaseService, _findingStore);
        }

        /// <summary>
        /// Sets the look-back window used for both producer reads (mirrors
        /// <c>CriticalIssuesContent.SetTimeRange</c>'s hours-back contract; the Recommendations
        /// reader takes a single hours-back window).
        /// </summary>
        public void SetTimeRange(int hoursBack)
        {
            _hoursBack = hoursBack <= 0 ? 24 : hoursBack;
        }

        /// <summary>
        /// Re-reads recommendations for this server and re-renders. Safe to call from the parent
        /// tab's refresh. Read-only: surfaces Loaded or the all-clear Empty state. The
        /// insufficient-data state is surfaced by <see cref="GenerateNowButton_Click"/> (the
        /// engine owns that determination), not by this read-only path.
        /// </summary>
        public async Task RefreshDataAsync()
        {
            if (_reader is null || _serverConnection is null)
                return;

            if (_isBusy)
                return;

            _isBusy = true;
            try
            {
                ApplyViewModel(RecommendationsViewModel.Loading());

                var serverName = _serverConnection.ServerName;
                var serverId = ServerIdHelper.GetDeterministicHashCode(serverName);

                var items = await _reader.GetRecommendationsAsync(
                    serverId, serverName, _hoursBack, utcOffsetMinutes: _utcOffsetMinutes);

                ApplyViewModel(RecommendationsViewModel.FromItems(items, serverName, _utcOffsetMinutes));
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading recommendations: {ex.Message}", ex);
                // Fall back to the empty state rather than leaving the spinner up.
                ApplyViewModel(RecommendationsViewModel.FromItems(Array.Empty<RecommendationItem>()));
            }
            finally
            {
                _isBusy = false;
            }
        }

        /// <summary>
        /// Runs an on-demand analysis for the current server (same construction path the
        /// <see cref="AnalysisScheduler"/> uses), then re-reads. If the engine reports insufficient
        /// collected history, surfaces the insufficient-data state with its message; otherwise the
        /// freshly-persisted findings are read back by <see cref="RefreshDataAsync"/>.
        /// </summary>
        private async void GenerateNowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_serverConnection is null || _credentialService is null)
                return;

            if (_isBusy)
                return;

            _isBusy = true;
            try
            {
                ApplyViewModel(RecommendationsViewModel.Loading());
                StatusText.Text = "Generating…";

                var serverName = _serverConnection.ServerName;
                var serverId = ServerIdHelper.GetDeterministicHashCode(serverName);
                var displayName = _serverConnection.DisplayNameWithIntent;
                var connectionString = _serverConnection.GetConnectionString(_credentialService);

                // Fresh per-run AnalysisService (IsAnalyzing is per-instance), mirroring the
                // scheduler. AnalyzeAsync persists findings; we then read them back.
                var planFetcher = new SqlServerPlanFetcher(connectionString);
                var analysisService = new AnalysisService(connectionString, planFetcher);

                await analysisService.AnalyzeAsync(serverId, displayName, hoursBack: 4);

                if (analysisService.InsufficientDataMessage is { Length: > 0 } message)
                {
                    StatusText.Text = string.Empty;
                    ApplyViewModel(RecommendationsViewModel.InsufficientData(message));
                    return;
                }

                StatusText.Text = string.Empty;
                _isBusy = false; // release before re-entering RefreshDataAsync's own guard
                await RefreshDataAsync();
                return;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error generating recommendations: {ex.Message}", ex);
                StatusText.Text = "Generate failed — see log.";
                ApplyViewModel(RecommendationsViewModel.FromItems(Array.Empty<RecommendationItem>()));
            }
            finally
            {
                _isBusy = false;
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            StatusText.Text = string.Empty;
            await RefreshDataAsync();
        }

        /// <summary>
        /// Deep-links an incident card to the Active Queries view scoped to the finding's window.
        /// Raises <see cref="OpenActiveQueriesRequested"/> with the RAW UTC window; the host tab
        /// applies the grace + timezone conversion. Wired in WS1b-1.
        /// </summary>
        private void OpenInActiveQueries_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not RecommendationCardViewModel card)
                return;

            // Engine findings always carry a window; legacy incidents carry log_date. Fall back to
            // a "now"-anchored band only if a producer somehow omitted it (handler widens anyway).
            var fromUtc = card.WindowStartUtc ?? DateTime.UtcNow.AddHours(-2);
            var toUtc = card.WindowEndUtc ?? DateTime.UtcNow;

            OpenActiveQueriesRequested?.Invoke(fromUtc, toUtc);
        }

        /// <summary>
        /// Copies the MCP investigation prompt for an incident card to the clipboard (no dialog;
        /// a brief status confirmation). Wired in WS1b-1.
        /// </summary>
        private void AskAi_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not RecommendationCardViewModel card)
                return;

            // SetDataObject with copy=false avoids WPF's problematic Clipboard.Flush() (matches the
            // convention in CriticalIssuesContent / AlertDetailWindow).
            Clipboard.SetDataObject(card.AskAiPrompt, false);
            StatusText.Text = "AI prompt copied to clipboard.";
        }

        /// <summary>
        /// Copies a config-fix card's ALTER statement to the clipboard. Wired in WS1b-1.
        /// </summary>
        private void CopyFix_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not RecommendationCardViewModel card)
                return;

            if (string.IsNullOrEmpty(card.CopyPasteSql))
                return;

            Clipboard.SetDataObject(card.CopyPasteSql, false);
            StatusText.Text = "Fix copied to clipboard.";
        }

        /// <summary>
        /// Swaps the visible region to match the view-model's state and binds the sections.
        /// </summary>
        private void ApplyViewModel(RecommendationsViewModel vm)
        {
            switch (vm.State)
            {
                case RecommendationsState.Loading:
                    LoadingOverlay.IsLoading = true;
                    SectionsScroll.Visibility = Visibility.Collapsed;
                    EmptyMessage.Visibility = Visibility.Collapsed;
                    InsufficientDataMessage.Visibility = Visibility.Collapsed;
                    break;

                case RecommendationsState.InsufficientData:
                    LoadingOverlay.IsLoading = false;
                    SectionsScroll.Visibility = Visibility.Collapsed;
                    EmptyMessage.Visibility = Visibility.Collapsed;
                    InsufficientDataMessage.Text = vm.InsufficientDataMessage;
                    InsufficientDataMessage.Visibility = Visibility.Visible;
                    break;

                case RecommendationsState.Empty:
                    LoadingOverlay.IsLoading = false;
                    SectionsList.ItemsSource = null;
                    SectionsScroll.Visibility = Visibility.Collapsed;
                    InsufficientDataMessage.Visibility = Visibility.Collapsed;
                    EmptyMessage.Visibility = Visibility.Visible;
                    break;

                case RecommendationsState.Loaded:
                default:
                    LoadingOverlay.IsLoading = false;
                    EmptyMessage.Visibility = Visibility.Collapsed;
                    InsufficientDataMessage.Visibility = Visibility.Collapsed;
                    SectionsList.ItemsSource = vm.Sections;
                    SectionsScroll.Visibility = Visibility.Visible;
                    break;
            }
        }
    }
}
