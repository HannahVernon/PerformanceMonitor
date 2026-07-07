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

    /* Set around a programmatic drill-down navigation (heatmap / per-chart "Show Active Queries at This
       Time" / Overview lane) so the inner-tab AND sub-tab SelectionChanged auto-refreshes don't clobber the
       drill-down's own filtered read via an async race — Lite's _suppressActiveQueriesAutoRefresh. Gated in
       InnerTabs_SelectionChanged, QueriesSubTabs_SelectionChanged, and BlockingSubTabs_SelectionChanged
       (the Blocking/Deadlock chart drills target the Blocking tab). */
    private bool _suppressDrillDownAutoRefresh;
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
    /// "Latest Snapshot" button (Stage 3b) — asks the Darling service to CAPTURE a fresh snapshot NOW via a
    /// <c>snapshot_now</c> command, then re-reads the newest stored batch to show it. This restores Lite's
    /// "Live Snapshot" intent (a capture on demand) through the command plane rather than a direct hit on the
    /// monitored server. <c>snapshot_now</c> runs even while the service is paused (explicit operator intent —
    /// the service honors it), so the button stays enabled while paused. A read-only seat cannot command the
    /// service, so it degrades to simply re-reading the most recent stored batch (the pre-3b behavior).
    /// </summary>
    private async void LatestSnapshot_Click(object sender, RoutedEventArgs e)
    {
        LatestSnapshotButton.IsEnabled = false;
        try
        {
            if (!_dataService.IsReadOnly)
            {
                LatestSnapshotIndicator.Text = "Capturing...";
                try
                {
                    var result = await _dataService.RequestSnapshotNowAsync(_server.ServerId);
                    if (result is null)
                    {
                        StatusChanged?.Invoke("snapshot: still running — showing the latest stored capture");
                    }
                    else if (result.Status != ViewerDataService.StatusSucceeded)
                    {
                        StatusChanged?.Invoke($"snapshot: the service reported {result.ResultStatus} — showing the latest stored capture");
                    }
                }
                catch (ViewerReadOnlyException)
                {
                    /* Grants changed under us — fall back to reading whatever is stored. */
                }
            }

            LatestSnapshotIndicator.Text = "Loading...";
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
        _suppressDrillDownAutoRefresh = true;
        try
        {
            /* Switch to the Queries inner tab first (a no-op when the caller is already there — the heatmap
               sub-tab lives ON the Queries tab; a CPU/Memory/tempdb/Perfmon chart or an Overview lane does
               not), then to the Active Queries sub-tab. Both switches are suppressed so the shell's generic
               inner-tab loader and the sub-tab loader don't race this targeted read. */
            InnerTabs.SelectedIndex = QueriesInnerTabIndex;
            QueriesSubTabControl.SelectedIndex = ActiveQueriesSubTabIndex;
        }
        finally
        {
            _suppressDrillDownAutoRefresh = false;
        }

        var snapshots = await _dataService.GetLatestQuerySnapshotsAsync(_server.ServerId, fromUtc, toUtc);
        _querySnapshotsFilterMgr!.UpdateData(snapshots);
        LatestSnapshotIndicator.Text = indicator;

        await LoadActiveQueriesSlicerAsync(fromUtc.AddHours(-1), toUtc.AddHours(1));
    }
}
