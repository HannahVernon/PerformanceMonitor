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
    /// collapsible Critical / Warning / Info sections. Each card shows a severity badge, the
    /// affected database, a headline + advice, a "Show T-SQL" expander with a working Copy button,
    /// and Apply + Mute buttons.
    ///
    /// <para>
    /// Scope (WS1b-1): the surface is read-only. Copy is wired (trivial clipboard write). Refresh
    /// re-runs the reader; "Generate now" runs an on-demand analysis for the current server and
    /// then refreshes. Apply and Mute are rendered DISABLED with an "Available in the next update"
    /// tooltip — their action wiring (and the informed-consent gate) is WS1b-2.
    /// </para>
    /// </summary>
    public partial class RecommendationsContent : UserControl
    {
        private DatabaseService? _databaseService;
        private SqlServerFindingStore? _findingStore;
        private RecommendationsReader? _reader;
        private ServerConnection? _serverConnection;
        private ICredentialService? _credentialService;

        private int _hoursBack = 24;
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
        /// <see cref="AnalysisScheduler"/> does.
        /// </summary>
        public void Initialize(
            DatabaseService databaseService,
            ServerConnection serverConnection,
            ICredentialService credentialService)
        {
            _databaseService = databaseService ?? throw new ArgumentNullException(nameof(databaseService));
            _serverConnection = serverConnection ?? throw new ArgumentNullException(nameof(serverConnection));
            _credentialService = credentialService ?? throw new ArgumentNullException(nameof(credentialService));
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

                var items = await _reader.GetRecommendationsAsync(serverId, serverName, _hoursBack);

                ApplyViewModel(RecommendationsViewModel.FromItems(items));
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
        /// Copies a card's T-SQL to the clipboard. The only wired action in WS1b-1.
        /// </summary>
        private void CopySql_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.DataContext is not RecommendationCardViewModel card)
                return;

            if (string.IsNullOrEmpty(card.CopyPasteSql))
                return;

            // SetDataObject with copy=false avoids WPF's problematic Clipboard.Flush() (matches
            // the convention in CriticalIssuesContent / AlertDetailWindow).
            Clipboard.SetDataObject(card.CopyPasteSql, false);
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
