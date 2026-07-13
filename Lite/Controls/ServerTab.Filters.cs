/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor Lite.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using PerformanceMonitor.Ui;
using PerformanceMonitorLite.Models;
using PerformanceMonitorLite.Services;

namespace PerformanceMonitorLite.Controls;

public partial class ServerTab : UserControl
{
    /* ========== Column Filtering ========== */

    private void InitializeFilterManagers()
    {
        _querySnapshotsFilterMgr = new DataGridFilterManager<QuerySnapshotRow>(QuerySnapshotsGrid);
        _queryStatsFilterMgr = new DataGridFilterManager<QueryStatsRow>(QueryStatsGrid);
        _procStatsFilterMgr = new DataGridFilterManager<ProcedureStatsRow>(ProcedureStatsGrid);
        _queryStoreFilterMgr = new DataGridFilterManager<QueryStoreRow>(QueryStoreGrid);
        _blockedProcessFilterMgr = new DataGridFilterManager<BlockedProcessReportRow>(BlockedProcessReportGrid);
        _deadlockFilterMgr = new DataGridFilterManager<DeadlockProcessDetail>(DeadlockGrid);
        _runningJobsFilterMgr = new DataGridFilterManager<RunningJobRow>(RunningJobsGrid);
        _serverConfigFilterMgr = new DataGridFilterManager<ServerConfigRow>(ServerConfigGrid);
        _databaseConfigFilterMgr = new DataGridFilterManager<DatabaseConfigRow>(DatabaseConfigGrid);
        _dbScopedConfigFilterMgr = new DataGridFilterManager<DatabaseScopedConfigRow>(DatabaseScopedConfigGrid);
        _traceFlagsFilterMgr = new DataGridFilterManager<TraceFlagRow>(TraceFlagsGrid);
        _collectionHealthFilterMgr = new DataGridFilterManager<CollectorHealthRow>(CollectionHealthGrid);
        _collectionLogFilterMgr = new DataGridFilterManager<CollectionLogRow>(CollectionLogGrid);
        _latchStatsFilterMgr = new DataGridFilterManager<LatchStatsSnapshotRow>(LatchStatsGrid);
        _spinlockStatsFilterMgr = new DataGridFilterManager<SpinlockStatsSnapshotRow>(SpinlockStatsGrid);
        _planCacheCompositionFilterMgr = new DataGridFilterManager<PlanCacheSnapshotRow>(PlanCacheCompositionGrid);
        /* System Events grids (the two chart sub-tabs have no grid, so register no filter manager). */
        _seSchedulerFilterMgr = new DataGridFilterManager<SchedulerIssueRow>(SchedulerIssuesGrid);
        _seSevereErrorFilterMgr = new DataGridFilterManager<SevereErrorRow>(SevereErrorsGrid);
        _seMemoryConditionsFilterMgr = new DataGridFilterManager<MemoryConditionsRow>(MemoryConditionsGrid);
        _seMemoryBrokerFilterMgr = new DataGridFilterManager<MemoryBrokerRow>(MemoryBrokerGrid);
        _seMemoryNodeOomFilterMgr = new DataGridFilterManager<MemoryNodeOomRow>(MemoryNodeOomGrid);
        _seSignificantWaitsFilterMgr = new DataGridFilterManager<SignificantWaitRow>(SignificantWaitsGrid);
        _seCpuTasksFilterMgr = new DataGridFilterManager<CpuTasksRow>(CpuTasksGrid);
        _seIoIssuesFilterMgr = new DataGridFilterManager<IoIssuesRow>(IoIssuesGrid);
        _seDefaultTraceFilterMgr = new DataGridFilterManager<DefaultTraceEventRow>(DefaultTraceGrid);
        /* Configuration Changes grids (one per sub-tab). */
        _serverConfigChangesFilterMgr = new DataGridFilterManager<ServerConfigChangeRow>(ServerConfigChangesGrid);
        _databaseConfigChangesFilterMgr = new DataGridFilterManager<DatabaseConfigChangeRow>(DatabaseConfigChangesGrid);
        _traceFlagChangesFilterMgr = new DataGridFilterManager<TraceFlagChangeRow>(TraceFlagChangesGrid);
        /* Expensive Queries grid (the LAST Queries sub-tab). */
        _expensiveQueriesFilterMgr = new DataGridFilterManager<UnifiedExpensiveQueryRow>(ExpensiveQueriesGrid);

        _filterManagers[QuerySnapshotsGrid] = _querySnapshotsFilterMgr;
        _filterManagers[QueryStatsGrid] = _queryStatsFilterMgr;
        _filterManagers[ProcedureStatsGrid] = _procStatsFilterMgr;
        _filterManagers[QueryStoreGrid] = _queryStoreFilterMgr;
        _filterManagers[BlockedProcessReportGrid] = _blockedProcessFilterMgr;
        _filterManagers[DeadlockGrid] = _deadlockFilterMgr;
        _filterManagers[RunningJobsGrid] = _runningJobsFilterMgr;
        _filterManagers[ServerConfigGrid] = _serverConfigFilterMgr;
        _filterManagers[DatabaseConfigGrid] = _databaseConfigFilterMgr;
        _filterManagers[DatabaseScopedConfigGrid] = _dbScopedConfigFilterMgr;
        _filterManagers[TraceFlagsGrid] = _traceFlagsFilterMgr;
        _filterManagers[CollectionHealthGrid] = _collectionHealthFilterMgr;
        _filterManagers[CollectionLogGrid] = _collectionLogFilterMgr;
        _filterManagers[LatchStatsGrid] = _latchStatsFilterMgr;
        _filterManagers[SpinlockStatsGrid] = _spinlockStatsFilterMgr;
        _filterManagers[PlanCacheCompositionGrid] = _planCacheCompositionFilterMgr;
        _filterManagers[SchedulerIssuesGrid] = _seSchedulerFilterMgr;
        _filterManagers[SevereErrorsGrid] = _seSevereErrorFilterMgr;
        _filterManagers[MemoryConditionsGrid] = _seMemoryConditionsFilterMgr;
        _filterManagers[MemoryBrokerGrid] = _seMemoryBrokerFilterMgr;
        _filterManagers[MemoryNodeOomGrid] = _seMemoryNodeOomFilterMgr;
        _filterManagers[SignificantWaitsGrid] = _seSignificantWaitsFilterMgr;
        _filterManagers[CpuTasksGrid] = _seCpuTasksFilterMgr;
        _filterManagers[IoIssuesGrid] = _seIoIssuesFilterMgr;
        _filterManagers[DefaultTraceGrid] = _seDefaultTraceFilterMgr;
        _filterManagers[ServerConfigChangesGrid] = _serverConfigChangesFilterMgr;
        _filterManagers[DatabaseConfigChangesGrid] = _databaseConfigChangesFilterMgr;
        _filterManagers[TraceFlagChangesGrid] = _traceFlagChangesFilterMgr;
        _filterManagers[ExpensiveQueriesGrid] = _expensiveQueriesFilterMgr;
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

    private DataGrid? _currentFilterGrid;

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
