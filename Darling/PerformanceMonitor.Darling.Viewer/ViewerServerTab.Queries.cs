/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Controls;
using PerformanceMonitor.Common;

namespace PerformanceMonitor.Darling.Viewer;

/// <summary>
/// The Queries inner tab (W1f-1) — the nested Top Queries / Top Procedures / Query Store sub-tab group,
/// copied from Lite's <c>ServerTab</c> (Refresh / Slicers / Grids partials) with reads rewired to the
/// <see cref="ViewerDataService"/> Postgres reads. A Queries sub-tab switch reloads through the shell's
/// overlap-guarded <see cref="RefreshActiveInnerTabAsync"/> (the Queries tab is the active inner tab
/// whenever its sub-tabs are visible), and <see cref="LoadQueriesAsync"/> loads only the newly-visible
/// sub-tab's grid + slicer + (when Compare is active) its comparison grid over the fixed 24-hour window.
/// Each grid carries a UTC time-range slicer whose drag re-reads the grid over the selection; sorting a
/// grid re-labels the slicer's aggregate curve (Lite's *Grid_Sorting). Deferred (no plan host / no live
/// server, matching W1e's Blocking tab): the per-row double-click history windows and the slicer
/// overlay-on-select — only drag-to-narrow + sort-driven metric re-labeling are wired.
/// </summary>
public partial class ViewerServerTab
{
    /* Queries sub-tab order — Lite's QueriesSubTabControl minus the deferred Performance Trends /
       Active Queries / Query Heatmap sub-tabs (W1f-2), so the three grids sit at 0/1/2. */
    private const int TopQueriesSubTabIndex = 0;
    private const int TopProceduresSubTabIndex = 1;
    private const int QueryStoreSubTabIndex = 2;

    private string _queryStatsSlicerMetric = "TotalCpu";
    private List<TimeSliceBucket>? _queryStatsSlicerData;
    private string _procStatsSlicerMetric = "TotalCpu";
    private List<TimeSliceBucket>? _procStatsSlicerData;
    private string _queryStoreSlicerMetric = "TotalCpu";
    private List<TimeSliceBucket>? _queryStoreSlicerData;

    /// <summary>
    /// Wires the Queries tab up-front (from the constructor, after InitializeComponent): the three grids'
    /// in-grid bar-cell maxima recompute hook (<see cref="SetupBarCellMaxes"/>) and each slicer's
    /// RangeChanged handler (dragging a slicer re-reads its grid over the selection).
    /// </summary>
    private void InitializeQueriesTab()
    {
        SetupBarCellMaxes();

        QueryStatsSlicer.RangeChanged += OnQueryStatsSlicerChanged;
        ProcStatsSlicer.RangeChanged += OnProcStatsSlicerChanged;
        QueryStoreSlicer.RangeChanged += OnQueryStoreSlicerChanged;
    }

    /// <summary>
    /// A Queries sub-tab switch reloads through the shell's overlap-guarded
    /// <see cref="RefreshActiveInnerTabAsync"/> (mirrors <see cref="BlockingSubTabs_SelectionChanged"/>).
    /// Gated on <see cref="System.Windows.FrameworkElement.IsLoaded"/> and the sub-TabControl's own
    /// selection so build-time and bubbled selections (the child grids, the Compare combo) are ignored.
    /// </summary>
    private async void QueriesSubTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, QueriesSubTabControl) || !IsLoaded)
        {
            return;
        }

        await RefreshActiveInnerTabAsync();
    }

    /// <summary>
    /// Loads the Queries tab's ACTIVE sub-tab only (Lite's subTabOnly gating): Top Queries / Top
    /// Procedures / Query Store each read their grid + slicer + comparison over the fixed 24-hour window.
    /// The shell's <see cref="LoadInnerTabAsync"/> owns the try/catch that surfaces failures.
    /// </summary>
    private async Task LoadQueriesAsync()
    {
        var endUtc = DateTime.UtcNow;
        var startUtc = endUtc - s_dataWindow;

        switch (QueriesSubTabControl.SelectedIndex)
        {
            case TopProceduresSubTabIndex:
                await LoadTopProceduresAsync(startUtc, endUtc);
                break;
            case QueryStoreSubTabIndex:
                await LoadQueryStoreAsync(startUtc, endUtc);
                break;
            case TopQueriesSubTabIndex:
            default:
                await LoadTopQueriesAsync(startUtc, endUtc);
                break;
        }
    }

    private async Task LoadTopQueriesAsync(DateTime startUtc, DateTime endUtc)
    {
        var rows = await _dataService.GetTopQueriesByCpuAsync(_server.ServerId, startUtc, endUtc);
        _queryStatsFilterMgr!.UpdateData(rows);
        SetDefaultSortIfNone(QueryStatsGrid, "TotalElapsedMs", ListSortDirection.Descending);
        await LoadQueryStatsSlicerAsync(startUtc, endUtc);
        await RefreshQueryStatsComparisonAsync(startUtc, endUtc);
    }

    private async Task LoadTopProceduresAsync(DateTime startUtc, DateTime endUtc)
    {
        var rows = await _dataService.GetTopProceduresByCpuAsync(_server.ServerId, startUtc, endUtc);
        _procStatsFilterMgr!.UpdateData(rows);
        SetDefaultSortIfNone(ProcedureStatsGrid, "TotalElapsedMs", ListSortDirection.Descending);
        await LoadProcStatsSlicerAsync(startUtc, endUtc);
        await RefreshProcStatsComparisonAsync(startUtc, endUtc);
    }

    private async Task LoadQueryStoreAsync(DateTime startUtc, DateTime endUtc)
    {
        var rows = await _dataService.GetQueryStoreTopQueriesAsync(_server.ServerId, startUtc, endUtc);
        _queryStoreFilterMgr!.UpdateData(rows);
        SetDefaultSortIfNone(QueryStoreGrid, "TotalDurationMs", ListSortDirection.Descending);
        await LoadQueryStoreSlicerAsync(startUtc, endUtc);
        await RefreshQueryStoreComparisonAsync(startUtc, endUtc);
    }

    // ── Slicers (Lite's ServerTab.Slicers.cs; the slicer sends UTC bounds, the viewer reads take naive UTC) ──

    private async Task LoadQueryStatsSlicerAsync(DateTime startUtc, DateTime endUtc)
    {
        var data = await _dataService.GetQueryStatsSlicerDataAsync(_server.ServerId, startUtc, endUtc);
        _queryStatsSlicerData = data;
        _queryStatsSlicerMetric = "TotalCpu";
        if (data.Count > 0)
            QueryStatsSlicer.LoadData(data, "Total CPU (ms)", startUtc, endUtc);
    }

    private async void OnQueryStatsSlicerChanged(object? sender, SlicerRangeEventArgs e)
    {
        try
        {
            var rows = await _dataService.GetTopQueriesByCpuAsync(_server.ServerId, e.StartUtc, e.EndUtc);
            _queryStatsFilterMgr!.UpdateData(rows);
            await RefreshQueryStatsComparisonAsync(e.StartUtc, e.EndUtc);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"query-stats slicer failed: {ex.Message}");
        }
    }

    private async Task LoadProcStatsSlicerAsync(DateTime startUtc, DateTime endUtc)
    {
        var data = await _dataService.GetProcStatsSlicerDataAsync(_server.ServerId, startUtc, endUtc);
        _procStatsSlicerData = data;
        _procStatsSlicerMetric = "TotalCpu";
        if (data.Count > 0)
            ProcStatsSlicer.LoadData(data, "Total CPU (ms)", startUtc, endUtc);
    }

    private async void OnProcStatsSlicerChanged(object? sender, SlicerRangeEventArgs e)
    {
        try
        {
            var rows = await _dataService.GetTopProceduresByCpuAsync(_server.ServerId, e.StartUtc, e.EndUtc);
            _procStatsFilterMgr!.UpdateData(rows);
            await RefreshProcStatsComparisonAsync(e.StartUtc, e.EndUtc);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"procedure-stats slicer failed: {ex.Message}");
        }
    }

    private async Task LoadQueryStoreSlicerAsync(DateTime startUtc, DateTime endUtc)
    {
        var data = await _dataService.GetQueryStoreSlicerDataAsync(_server.ServerId, startUtc, endUtc);
        _queryStoreSlicerData = data;
        _queryStoreSlicerMetric = "TotalCpu";
        if (data.Count > 0)
            QueryStoreSlicer.LoadData(data, "Total CPU (ms)", startUtc, endUtc);
    }

    private async void OnQueryStoreSlicerChanged(object? sender, SlicerRangeEventArgs e)
    {
        try
        {
            var rows = await _dataService.GetQueryStoreTopQueriesAsync(_server.ServerId, e.StartUtc, e.EndUtc);
            _queryStoreFilterMgr!.UpdateData(rows);
            await RefreshQueryStoreComparisonAsync(e.StartUtc, e.EndUtc);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"query-store slicer failed: {ex.Message}");
        }
    }

    // ── Sort-driven slicer metric re-labeling (Lite's ServerTab.Grids.cs *Grid_Sorting) ──

    /// <summary>Sorting the Top Queries grid swaps the slicer's aggregate curve to match the sorted column.</summary>
    private void QueryStatsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (_queryStatsSlicerData == null || _queryStatsSlicerData.Count == 0) return;

        var col = SortColumnPath(e.Column);
        var (metric, label) = col switch
        {
            "TotalCpuMs" => ("TotalCpu", "Total CPU (ms)"),
            "AvgCpuMs" => ("AvgCpu", "Avg CPU (ms)"),
            "TotalElapsedMs" => ("TotalElapsed", "Total Duration (ms)"),
            "AvgElapsedMs" => ("AvgElapsed", "Avg Duration (ms)"),
            "TotalLogicalReads" => ("TotalReads", "Total Reads"),
            "AvgReads" => ("AvgReads", "Avg Reads"),
            "TotalLogicalWrites" => ("TotalWrites", "Total Writes"),
            "TotalPhysicalReads" => ("TotalPhysReads", "Total Physical Reads"),
            _ => ("TotalCpu", "Total CPU (ms)"),
        };

        if (metric == _queryStatsSlicerMetric) return;
        _queryStatsSlicerMetric = metric;

        foreach (var bucket in _queryStatsSlicerData)
        {
            var n = bucket.SessionCount > 0 ? bucket.SessionCount : 1;
            bucket.Value = metric switch
            {
                "TotalCpu" => bucket.TotalCpu,
                "AvgCpu" => bucket.TotalCpu / n,
                "TotalElapsed" => bucket.TotalElapsed,
                "AvgElapsed" => bucket.TotalElapsed / n,
                "TotalReads" => bucket.TotalReads,
                "AvgReads" => bucket.TotalReads / n,
                "TotalWrites" => bucket.TotalWrites,
                "TotalPhysReads" => bucket.TotalPhysicalReads,
                _ => bucket.TotalCpu,
            };
        }

        QueryStatsSlicer.UpdateMetric(label);
    }

    /// <summary>Sorting the Top Procedures grid swaps the slicer's aggregate curve to match the sorted column.</summary>
    private void ProcedureStatsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (_procStatsSlicerData == null || _procStatsSlicerData.Count == 0) return;

        var col = SortColumnPath(e.Column);
        var (metric, label) = col switch
        {
            "TotalCpuMs" => ("TotalCpu", "Total CPU (ms)"),
            "AvgCpuMs" => ("AvgCpu", "Avg CPU (ms)"),
            "TotalElapsedMs" => ("TotalElapsed", "Total Duration (ms)"),
            "AvgElapsedMs" => ("AvgElapsed", "Avg Duration (ms)"),
            "TotalLogicalReads" or "AvgReads" => ("TotalReads", "Total Reads"),
            "TotalLogicalWrites" => ("TotalWrites", "Total Writes"),
            "TotalPhysicalReads" => ("TotalReads", "Total Physical Reads"),
            _ => ("TotalCpu", "Total CPU (ms)"),
        };

        if (metric == _procStatsSlicerMetric) return;
        _procStatsSlicerMetric = metric;

        foreach (var bucket in _procStatsSlicerData)
        {
            var n = bucket.SessionCount > 0 ? bucket.SessionCount : 1;
            bucket.Value = metric switch
            {
                "TotalCpu" => bucket.TotalCpu,
                "AvgCpu" => bucket.TotalCpu / n,
                "TotalElapsed" => bucket.TotalElapsed,
                "AvgElapsed" => bucket.TotalElapsed / n,
                "TotalReads" => bucket.TotalReads,
                "TotalWrites" => bucket.TotalWrites,
                _ => bucket.TotalCpu,
            };
        }

        ProcStatsSlicer.UpdateMetric(label);
    }

    /// <summary>Sorting the Query Store grid swaps the slicer's aggregate curve to match the sorted column.</summary>
    private void QueryStoreGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        if (_queryStoreSlicerData == null || _queryStoreSlicerData.Count == 0) return;

        var col = SortColumnPath(e.Column);
        var (metric, label) = col switch
        {
            "TotalCpuMs" => ("TotalCpu", "Total CPU (ms)"),
            "AvgCpuTimeMs" => ("AvgCpu", "Avg CPU (ms)"),
            "TotalDurationMs" => ("TotalElapsed", "Total Duration (ms)"),
            "AvgDurationMs" => ("AvgElapsed", "Avg Duration (ms)"),
            "AvgLogicalReads" => ("TotalReads", "Avg Reads"),
            "AvgLogicalWrites" => ("TotalWrites", "Avg Writes"),
            "AvgPhysicalReads" => ("TotalReads", "Avg Physical Reads"),
            "TotalExecutions" => ("Sessions", "Executions"),
            _ => ("TotalCpu", "Total CPU (ms)"),
        };

        if (metric == _queryStoreSlicerMetric) return;
        _queryStoreSlicerMetric = metric;

        foreach (var bucket in _queryStoreSlicerData)
        {
            var n = bucket.SessionCount > 0 ? bucket.SessionCount : 1;
            bucket.Value = metric switch
            {
                "TotalCpu" => bucket.TotalCpu,
                "AvgCpu" => bucket.TotalCpu / n,
                "TotalElapsed" => bucket.TotalElapsed,
                "AvgElapsed" => bucket.TotalElapsed / n,
                "TotalReads" => bucket.TotalReads,
                "TotalWrites" => bucket.TotalWrites,
                "Sessions" => bucket.SessionCount,
                _ => bucket.TotalCpu,
            };
        }

        QueryStoreSlicer.UpdateMetric(label);
    }

    /// <summary>The sorted column's member path (SortMemberPath, else the bound Binding path) — Lite's fallback.</summary>
    private static string SortColumnPath(DataGridColumn column)
    {
        var col = column.SortMemberPath ?? "";
        if (string.IsNullOrEmpty(col) && column is DataGridBoundColumn bc &&
            bc.Binding is System.Windows.Data.Binding b)
        {
            col = b.Path.Path;
        }
        return col;
    }

    /// <summary>
    /// Sets the grid's default sort when the user has not sorted it yet (Lite's SetDefaultSortIfNone) —
    /// so the first render matches the read's ORDER BY. The filter manager preserves sort across reloads,
    /// so this only fires on the initial load.
    /// </summary>
    private static void SetDefaultSortIfNone(DataGrid grid, string bindingPath, ListSortDirection direction)
    {
        if (grid.Items.SortDescriptions.Count > 0) return;

        grid.Items.SortDescriptions.Add(new SortDescription(bindingPath, direction));
        foreach (var column in grid.Columns)
        {
            if (column.SortMemberPath == bindingPath ||
                (column is DataGridBoundColumn bc && bc.Binding is System.Windows.Data.Binding b && b.Path.Path == bindingPath))
            {
                column.SortDirection = direction;
                return;
            }
        }
    }
}
