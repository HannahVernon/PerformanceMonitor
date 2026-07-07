/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using PerformanceMonitor.Common;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Queries → Active Queries sub-tab (W1f-2): the <c>ActiveQueriesSlicer</c> + <c>QuerySnapshotsGrid</c>
/// of captured running-query snapshots, copied from Lite's <c>ServerTab</c> (Slicers / Grids / Refresh
/// partials) with reads rewired to <see cref="ViewerDataService"/> Postgres. The grid loads every stored
/// snapshot over the toolbar's settable window (newest first); dragging the slicer re-reads the grid over the
/// selection, and sorting the grid re-labels the slicer's aggregate curve (Lite's <c>QuerySnapshotsGrid_Sorting</c>).
/// Two deliberate deviations from Lite:
///   (1) The "Latest Snapshot" button (Lite's "Live Snapshot") no longer queries the monitored server —
///       per Erik's decision the viewer reads only stored data, so it re-reads the newest persisted
///       snapshot batch (<see cref="ViewerDataService.GetLatestQuerySnapshotBatchAsync"/>).
///   (2) The Estimated / Actual plan buttons open the stored plan through the <see cref="OpenPlanTab"/>
///       partial hook (the plan-host wave supplies the body) rather than Lite's save-to-file.
/// </summary>
public partial class ViewerServerTab
{
    private string _activeQueriesSlicerMetric = "Sessions";
    private List<TimeSliceBucket>? _activeQueriesSlicerData;

    /* Set around a programmatic switch to the Active Queries sub-tab (a heatmap drill-down) so the
       sub-tab SelectionChanged auto-refresh doesn't clobber the drill-down's own filtered snapshot via
       an async race — Lite's _suppressActiveQueriesAutoRefresh. */
    private bool _suppressActiveQueriesAutoRefresh;
    /* OpenPlanTab is implemented by the plan-host partial (ViewerServerTab.Plans.cs). */

    /// <summary>Wires the Active Queries slicer's RangeChanged (drag re-reads the grid). Called from
    /// <see cref="InitializeQueriesTab"/> after InitializeComponent so the named slicer exists.</summary>
    private void InitializeActiveQueriesTab()
    {
        ActiveQueriesSlicer.RangeChanged += OnActiveQueriesSlicerChanged;
    }

    /// <summary>Loads the Active Queries sub-tab: every stored snapshot over the window (newest first)
    /// plus the hourly slicer. Mirrors Lite's Queries sub-tab-1 refresh.</summary>
    private async Task LoadActiveQueriesAsync(DateTime startUtc, DateTime endUtc)
    {
        var snapshots = await _dataService.GetLatestQuerySnapshotsAsync(_server.ServerId, startUtc, endUtc);
        _querySnapshotsFilterMgr!.UpdateData(snapshots);
        LatestSnapshotIndicator.Text = "";
        await LoadActiveQueriesSlicerAsync(startUtc, endUtc);
    }

    private async Task LoadActiveQueriesSlicerAsync(DateTime startUtc, DateTime endUtc)
    {
        var data = await _dataService.GetActiveQuerySlicerDataAsync(_server.ServerId, startUtc, endUtc);
        _activeQueriesSlicerData = data;
        _activeQueriesSlicerMetric = "Sessions";
        if (data.Count > 0)
            ActiveQueriesSlicer.LoadData(data, "Sessions", startUtc, endUtc);
    }

    private async void OnActiveQueriesSlicerChanged(object? sender, SlicerRangeEventArgs e)
    {
        try
        {
            var snapshots = await _dataService.GetLatestQuerySnapshotsAsync(_server.ServerId, e.StartUtc, e.EndUtc);
            _querySnapshotsFilterMgr!.UpdateData(snapshots);
            LatestSnapshotIndicator.Text = "";
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"active-queries slicer failed: {ex.Message}");
        }
    }

    /// <summary>Sorting the snapshot grid swaps the slicer's aggregate curve to match the sorted column
    /// (Lite's <c>QuerySnapshotsGrid_Sorting</c>).</summary>
    private void QuerySnapshotsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (_activeQueriesSlicerData == null || _activeQueriesSlicerData.Count == 0) return;

        var col = SortColumnPath(e.Column);
        var (metric, label) = col switch
        {
            "CpuTimeMs" => ("TotalCpu", "Total CPU (ms)"),
            "TotalElapsedTimeMs" => ("TotalElapsed", "Total Elapsed (ms)"),
            "Reads" => ("TotalReads", "Total Reads"),
            "LogicalReads" => ("TotalLogicalReads", "Total Logical Reads"),
            "Writes" => ("TotalWrites", "Total Writes"),
            _ => ("Sessions", "Sessions"),
        };

        if (metric == _activeQueriesSlicerMetric) return;
        _activeQueriesSlicerMetric = metric;

        foreach (var bucket in _activeQueriesSlicerData)
        {
            bucket.Value = metric switch
            {
                "TotalCpu" => bucket.TotalCpu,
                "TotalElapsed" => bucket.TotalElapsed,
                "TotalReads" => bucket.TotalReads,
                "TotalLogicalReads" => bucket.TotalLogicalReads,
                "TotalWrites" => bucket.TotalWrites,
                _ => bucket.SessionCount,
            };
        }

        ActiveQueriesSlicer.UpdateMetric(label);
    }

    /// <summary>
    /// "Latest Snapshot" button — re-reads the newest STORED snapshot batch (no live SQL). Semantics
    /// change from Lite's "Live Snapshot" (query the monitored server now): the viewer only sees what the
    /// collector persisted, so this shows the most recent captured batch of running queries.
    /// </summary>
    private async void LatestSnapshot_Click(object sender, RoutedEventArgs e)
    {
        LatestSnapshotButton.IsEnabled = false;
        LatestSnapshotIndicator.Text = "Loading...";
        try
        {
            var (batchTime, rows) = await _dataService.GetLatestQuerySnapshotBatchAsync(_server.ServerId);
            _querySnapshotsFilterMgr!.UpdateData(rows);
            LatestSnapshotIndicator.Text = batchTime.HasValue
                ? $"Latest snapshot: {ViewerTimeHelper.ForDisplay(batchTime.Value):yyyy-MM-dd HH:mm:ss}"
                : "No snapshots stored";
        }
        catch (Exception ex)
        {
            LatestSnapshotIndicator.Text = "";
            StatusChanged?.Invoke($"latest snapshot failed: {ex.Message}");
        }
        finally
        {
            LatestSnapshotButton.IsEnabled = true;
        }
    }

    /// <summary>Opens the snapshot's stored ESTIMATED plan in the Plan Viewer (gated on HasQueryPlan).</summary>
    private void OpenSnapshotEstimatedPlan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ViewerQuerySnapshotRow row) return;
        if (string.IsNullOrEmpty(row.QueryPlan)) return;
        OpenPlanTab(row.QueryPlan, $"Estimated Plan — Session {row.SessionId}", row.QueryText);
    }

    /// <summary>Opens the snapshot's stored ACTUAL (live) plan in the Plan Viewer (gated on HasLiveQueryPlan).</summary>
    private void OpenSnapshotActualPlan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not ViewerQuerySnapshotRow row) return;
        if (string.IsNullOrEmpty(row.LiveQueryPlan)) return;
        OpenPlanTab(row.LiveQueryPlan, $"Actual Plan — Session {row.SessionId}", row.QueryText);
    }

    /// <summary>
    /// Switches to the Active Queries sub-tab and loads the grid + slicer for a narrow window — the target
    /// of the heatmap drill-down. The suppress flag skips the sub-tab auto-refresh so it doesn't clobber
    /// this filtered read (Lite's SelectActiveQueriesForDrillDown). The slicer window is padded ±1h so the
    /// hourly buckets still overlap a narrow drill window (Lite's LoadActiveQueriesSlicerAsync padding).
    /// </summary>
    private async Task NavigateToActiveQueriesForWindowAsync(DateTime fromUtc, DateTime toUtc, string indicator)
    {
        _suppressActiveQueriesAutoRefresh = true;
        try
        {
            QueriesSubTabControl.SelectedIndex = ActiveQueriesSubTabIndex;
        }
        finally
        {
            _suppressActiveQueriesAutoRefresh = false;
        }

        var snapshots = await _dataService.GetLatestQuerySnapshotsAsync(_server.ServerId, fromUtc, toUtc);
        _querySnapshotsFilterMgr!.UpdateData(snapshots);
        LatestSnapshotIndicator.Text = indicator;

        await LoadActiveQueriesSlicerAsync(fromUtc.AddHours(-1), toUtc.AddHours(1));
    }
}
