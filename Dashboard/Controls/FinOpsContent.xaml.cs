/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using PerformanceMonitorDashboard.Helpers;
using PerformanceMonitorDashboard.Models;
using PerformanceMonitorDashboard.Services;
using PerformanceMonitor.Ui;
using PerformanceMonitor.Common;

namespace PerformanceMonitorDashboard.Controls
{
    public partial class FinOpsContent : UserControl
    {
        private DatabaseService? _databaseService;
        private ServerManager? _serverManager;
        private CredentialService? _credentialService;
        private List<FinOpsServerInventory>? _serverInventoryCache;
        private DateTime _serverInventoryCacheTime;
        private decimal _currentServerMonthlyCost;

        private DataGridFilterManager<FinOpsDatabaseSizeStats>? _dbSizesFilterMgr;
        private Popup? _dbSizeFilterPopup;
        private ColumnFilterPopup? _dbSizeFilterPopupContent;

        public FinOpsContent()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            TabHelpers.AutoSizeColumnMinWidths(RecommendationsDataGrid);
            TabHelpers.AutoSizeColumnMinWidths(DatabaseResourcesDataGrid);
            TabHelpers.AutoSizeColumnMinWidths(DatabaseSizesDataGrid);
            TabHelpers.AutoSizeColumnMinWidths(ApplicationConnectionsDataGrid);
            TabHelpers.AutoSizeColumnMinWidths(ServerInventoryDataGrid);
            TabHelpers.AutoSizeColumnMinWidths(TopTotalGrid);
            TabHelpers.AutoSizeColumnMinWidths(TopAvgGrid);
            TabHelpers.AutoSizeColumnMinWidths(StorageGrowthDataGrid);
            TabHelpers.AutoSizeColumnMinWidths(IdleDatabasesDataGrid);
            TabHelpers.AutoSizeColumnMinWidths(TempdbPressureDataGrid);
            TabHelpers.AutoSizeColumnMinWidths(WaitCategorySummaryDataGrid);
            TabHelpers.AutoSizeColumnMinWidths(ExpensiveQueriesDataGrid);
            TabHelpers.AutoSizeColumnMinWidths(IndexAnalysisSummaryGrid);
            TabHelpers.AutoSizeColumnMinWidths(IndexAnalysisDetailGrid);
            TabHelpers.AutoSizeColumnMinWidths(HighImpactDataGrid);

            TabHelpers.FreezeColumns(RecommendationsDataGrid, 1);
            TabHelpers.FreezeColumns(DatabaseResourcesDataGrid, 1);
            TabHelpers.FreezeColumns(DatabaseSizesDataGrid, 1);
            TabHelpers.FreezeColumns(ApplicationConnectionsDataGrid, 1);
            TabHelpers.FreezeColumns(ServerInventoryDataGrid, 1);
            TabHelpers.FreezeColumns(TopTotalGrid, 1);
            TabHelpers.FreezeColumns(TopAvgGrid, 1);
            TabHelpers.FreezeColumns(StorageGrowthDataGrid, 1);
            TabHelpers.FreezeColumns(IdleDatabasesDataGrid, 1);
            TabHelpers.FreezeColumns(WaitCategorySummaryDataGrid, 1);
            TabHelpers.FreezeColumns(ExpensiveQueriesDataGrid, 1);
            TabHelpers.FreezeColumns(IndexAnalysisDetailGrid, 1);
            TabHelpers.FreezeColumns(HighImpactDataGrid, 1);

            _dbSizesFilterMgr = new DataGridFilterManager<FinOpsDatabaseSizeStats>(DatabaseSizesDataGrid);
        }

        /// <summary>
        /// Initializes the control with required dependencies.
        /// </summary>
        public void Initialize(ServerManager serverManager, CredentialService credentialService)
        {
            _serverManager = serverManager ?? throw new ArgumentNullException(nameof(serverManager));
            _credentialService = credentialService ?? throw new ArgumentNullException(nameof(credentialService));

            var servers = _serverManager.GetAllServers();
            ServerSelector.ItemsSource = servers;
            if (servers.Count > 0)
                ServerSelector.SelectedIndex = 0;
        }

        private async void ServerSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ServerSelector.SelectedItem is ServerConnection server && _credentialService != null)
            {
                var connectionString = server.GetConnectionString(_credentialService);
                _databaseService = new DatabaseService(connectionString);
                _currentServerMonthlyCost = server.MonthlyCostUsd;
                await RefreshDataAsync();
            }
        }

        /// <summary>
        /// Refreshes all FinOps data. Can be called from parent control.
        /// </summary>
        public async Task RefreshDataAsync()
        {
            try
            {
                // Re-read monthly cost from server manager in case user edited the server config
                if (ServerSelector.SelectedItem is ServerConnection selectedServer && _serverManager != null)
                {
                    var fresh = _serverManager.GetServerById(selectedServer.Id);
                    _currentServerMonthlyCost = fresh?.MonthlyCostUsd ?? selectedServer.MonthlyCostUsd;
                }

                await Task.WhenAll(
                    LoadRecommendationsAsync(),
                    LoadUtilizationAsync(),
                    LoadDatabaseResourcesAsync(),
                    LoadDatabaseSizesAsync(),
                    LoadApplicationConnectionsAsync(),
                    LoadServerInventoryAsync(),
                    LoadStorageGrowthAsync(),
                    LoadObjectSizeGrowthAsync(),
                    LoadIndexUsageAsync(),
                    LoadIndexLockingAsync(),
                    LoadIdleDatabasesAsync(),
                    LoadTempdbSummaryAsync(),
                    LoadWaitCategorySummaryAsync(),
                    LoadExpensiveQueriesAsync(),
                    LoadMemoryGrantEfficiencyAsync(),
                    LoadHighImpactQueriesAsync()
                );
            }
            catch (Exception ex)
            {
                Logger.Error($"Error refreshing FinOps data: {ex.Message}", ex);
            }
        }

    }
}
