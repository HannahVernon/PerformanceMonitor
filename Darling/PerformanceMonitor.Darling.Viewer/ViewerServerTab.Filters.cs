/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

// Column-filter wiring copied from Lite/Controls/ServerTab.Filters.cs for the copy-parity program
// (copy-don't-promote: the Darling viewer owns its own copy; Lite/Dashboard are untouched). Byte-
// identical popup plumbing; the manager set is scoped to the four Configuration sub-grids — the only
// filterable viewer grids ported so far — and grows as later waves add their own grids. Also holds
// LoadConfigurationAsync, the Configuration tab's parallel latest-snapshot load that feeds the managers.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PerformanceMonitor.Ui;

namespace PerformanceMonitor.Darling.Viewer;

public partial class ViewerServerTab : UserControl
{
    /* Grid -> its filter manager, keyed for the FilterButton_Click visual-tree walk. */
    private readonly Dictionary<DataGrid, IDataGridFilterManager> _filterManagers = new();

    private DataGridFilterManager<ServerConfigRow>? _serverConfigFilterMgr;
    private DataGridFilterManager<DatabaseConfigRow>? _databaseConfigFilterMgr;
    private DataGridFilterManager<DatabaseScopedConfigRow>? _dbScopedConfigFilterMgr;
    private DataGridFilterManager<TraceFlagRow>? _traceFlagsFilterMgr;

    private Popup? _filterPopup;
    private ColumnFilterPopup? _filterPopupContent;
    private DataGrid? _currentFilterGrid;

    /* ========== Column Filtering ========== */

    private void InitializeFilterManagers()
    {
        _serverConfigFilterMgr = new DataGridFilterManager<ServerConfigRow>(ServerConfigGrid);
        _databaseConfigFilterMgr = new DataGridFilterManager<DatabaseConfigRow>(DatabaseConfigGrid);
        _dbScopedConfigFilterMgr = new DataGridFilterManager<DatabaseScopedConfigRow>(DatabaseScopedConfigGrid);
        _traceFlagsFilterMgr = new DataGridFilterManager<TraceFlagRow>(TraceFlagsGrid);

        _filterManagers[ServerConfigGrid] = _serverConfigFilterMgr;
        _filterManagers[DatabaseConfigGrid] = _databaseConfigFilterMgr;
        _filterManagers[DatabaseScopedConfigGrid] = _dbScopedConfigFilterMgr;
        _filterManagers[TraceFlagsGrid] = _traceFlagsFilterMgr;
    }

    /// <summary>
    /// Configuration tab load: fires all four latest-snapshot reads concurrently (NpgsqlDataSource
    /// pools a connection for each), then hands each result to its filter manager's UpdateData so
    /// active column filters survive the refresh. Mirrors Lite's RefreshConfigurationAsync
    /// (Task.WhenAll of the four) — but the reads are genuinely async, so there is no Task.Run wrap.
    /// LoadInnerTabAsync owns the try/catch that surfaces failures on the status bar.
    /// </summary>
    private async Task LoadConfigurationAsync()
    {
        var serverConfigTask = _dataService.GetLatestServerConfigAsync(_server.ServerId);
        var databaseConfigTask = _dataService.GetLatestDatabaseConfigAsync(_server.ServerId);
        var databaseScopedConfigTask = _dataService.GetLatestDatabaseScopedConfigAsync(_server.ServerId);
        var traceFlagsTask = _dataService.GetLatestTraceFlagsAsync(_server.ServerId);

        await Task.WhenAll(serverConfigTask, databaseConfigTask, databaseScopedConfigTask, traceFlagsTask);

        _serverConfigFilterMgr!.UpdateData(serverConfigTask.Result);
        _databaseConfigFilterMgr!.UpdateData(databaseConfigTask.Result);
        _dbScopedConfigFilterMgr!.UpdateData(databaseScopedConfigTask.Result);
        _traceFlagsFilterMgr!.UpdateData(traceFlagsTask.Result);
    }

    private void EnsureFilterPopup()
    {
        if (_filterPopup == null)
        {
            _filterPopupContent = new ColumnFilterPopup();
            _filterPopup = new Popup
            {
                Child = _filterPopupContent,
                StaysOpen = false,
                Placement = PlacementMode.Bottom,
                AllowsTransparency = true
            };
        }
    }

    private void FilterButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string columnName) return;

        /* Walk up visual tree to find the parent DataGrid */
        var dataGrid = FindParentDataGridFromElement(button);
        if (dataGrid == null || !_filterManagers.TryGetValue(dataGrid, out var manager)) return;

        _currentFilterGrid = dataGrid;

        EnsureFilterPopup();

        /* Rewire events to the current grid */
        _filterPopupContent!.FilterApplied -= FilterPopup_FilterApplied;
        _filterPopupContent.FilterCleared -= FilterPopup_FilterCleared;
        _filterPopupContent.FilterApplied += FilterPopup_FilterApplied;
        _filterPopupContent.FilterCleared += FilterPopup_FilterCleared;

        /* Initialize with existing filter state */
        manager.Filters.TryGetValue(columnName, out var existingFilter);
        _filterPopupContent.Initialize(columnName, existingFilter);

        _filterPopup!.PlacementTarget = button;
        _filterPopup.IsOpen = true;
    }

    private void FilterPopup_FilterApplied(object? sender, FilterAppliedEventArgs e)
    {
        if (_filterPopup != null)
            _filterPopup.IsOpen = false;

        if (_currentFilterGrid != null && _filterManagers.TryGetValue(_currentFilterGrid, out var manager))
        {
            manager.SetFilter(e.FilterState);
        }
    }

    private void FilterPopup_FilterCleared(object? sender, EventArgs e)
    {
        if (_filterPopup != null)
            _filterPopup.IsOpen = false;
    }

    private static DataGrid? FindParentDataGridFromElement(DependencyObject element)
    {
        var current = element;
        while (current != null)
        {
            if (current is DataGrid dg)
                return dg;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
